using System;
using System.Collections.Generic;
using Editor.Core.Data;
using Editor.ECS;
using Editor.ECS.Components;
using Editor.ECS.Components.Rendering;

namespace Editor.Core.Services
{
    /// <summary>
    /// Simple 3D vector for raycasting.
    /// </summary>
    public struct Vector3f
    {
        public float X, Y, Z;
        
        public Vector3f(float x, float y, float z) { X = x; Y = y; Z = z; }
        
        public static Vector3f operator +(Vector3f a, Vector3f b) => new Vector3f(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vector3f operator -(Vector3f a, Vector3f b) => new Vector3f(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vector3f operator *(Vector3f a, float s) => new Vector3f(a.X * s, a.Y * s, a.Z * s);
        public static Vector3f operator *(float s, Vector3f a) => new Vector3f(a.X * s, a.Y * s, a.Z * s);
        
        public float Length => (float)Math.Sqrt(X * X + Y * Y + Z * Z);
        public Vector3f Normalized { get { float l = Length; return l > 0 ? new Vector3f(X/l, Y/l, Z/l) : this; } }
    }

    /// <summary>
    /// Service for raycasting and picking entities in the viewport.
    /// </summary>
    public class RaycastService
    {
        private static RaycastService _instance;
        public static RaycastService Instance => _instance ?? (_instance = new RaycastService());

        /// <summary>Vertical field-of-view (degrees) the EDITOR viewport renders with. This MUST match the value
        /// GamePreviewView pushes via VortexAPI.SetViewFOV and the native renderer's m_fov_degrees (60°) — otherwise
        /// every picking ray is computed for a different frustum than what's on screen, so hits only line up near the
        /// screen centre and drift ever further off toward the edges (the "hitboxes are wrong / I have to get ultra
        /// close" bug). Single source of truth: change it here and pass it to SetViewFOV.</summary>
        public const float EditorFovYDegrees = 60.0f;
        /// <summary>The editor view's clip planes; play mode replaces them with the main camera's (#327) and the
        /// editor view puts them back together with its FOV.</summary>
        public const float EditorNearClip = 0.1f;
        /// <summary>The editor view's far plane. 1000 m unless a terrain in the scene is larger: TerrainService.Submit raises it to
        /// 1.5 × the largest terrain's size so a far backdrop terrain is drawn to its edge (and lowers it again when it goes).</summary>
        public static float EditorFarClip { get; set; } = 1000.0f;

        private RaycastService() { }

        /// <summary>
        /// Result of a raycast operation.
        /// </summary>
        public class RaycastHit
        {
            public GameEntity Entity { get; set; }
            public Vector3f Point { get; set; }
            public float Distance { get; set; }
        }

        /// <summary>
        /// Pick an entity at screen coordinates.
        /// </summary>
        /// <param name="screenX">Screen X coordinate (0-1 normalized)</param>
        /// <param name="screenY">Screen Y coordinate (0-1 normalized)</param>
        /// <param name="scene">Scene to search in</param>
        /// <returns>The picked entity or null</returns>
        public GameEntity PickEntity(float screenX, float screenY, Scene scene)
        {
            // Use default 16:9 aspect ratio
            return PickEntity(screenX, screenY, scene, 16.0f / 9.0f);
        }

        /// <summary>
        /// Pick an entity at screen coordinates with explicit aspect ratio.
        /// </summary>
        public GameEntity PickEntity(float screenX, float screenY, Scene scene, float aspectRatio)
        {
            if (scene == null || scene.Entities == null) return null;

            var ray = ScreenToRayWithAspect(screenX, screenY, aspectRatio, 1.0f);

            RaycastHit closestHit = null;
            float closestDistance = float.MaxValue;

            foreach (var entity in scene.Entities)
            {
                // Pick == render: an eye-hidden or inactive subtree is not drawn, so it must not
                // swallow clicks either (SceneRenderService.SubmitEntityRecursive has the same gate).
                if (!SceneRenderService.IsEditorSubtreeVisible(entity)) continue;

                var hit = RaycastEntity(ray, entity);
                if (hit != null && hit.Distance < closestDistance)
                {
                    closestDistance = hit.Distance;
                    closestHit = hit;
                }

                // Also check children recursively
                if (entity.Children != null)
                {
                    foreach (var child in entity.Children)
                    {
                        var childHit = RaycastEntityRecursive(ray, child);
                        if (childHit != null && childHit.Distance < closestDistance)
                        {
                            closestDistance = childHit.Distance;
                            closestHit = childHit;
                        }
                    }
                }
            }

            return closestHit?.Entity;
        }

        private RaycastHit RaycastEntityRecursive(Ray ray, GameEntity entity)
        {
            if (!SceneRenderService.IsEditorSubtreeVisible(entity)) return null;   // pick == render

            var hit = RaycastEntity(ray, entity);

            if (entity.Children != null)
            {
                foreach (var child in entity.Children)
                {
                    var childHit = RaycastEntityRecursive(ray, child);
                    if (childHit != null && (hit == null || childHit.Distance < hit.Distance))
                    {
                        hit = childHit;
                    }
                }
            }

            return hit;
        }

        /// <summary>
        /// Raycast against all entities in a scene.
        /// </summary>
        public List<RaycastHit> RaycastAll(Ray ray, Scene scene)
        {
            var hits = new List<RaycastHit>();
            
            if (scene == null || scene.Entities == null) return hits;

            foreach (var entity in scene.Entities)
            {
                RaycastEntityRecursive(ray, entity, hits);
            }

            hits.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            return hits;
        }

        private void RaycastEntityRecursive(Ray ray, GameEntity entity, List<RaycastHit> hits)
        {
            if (!SceneRenderService.IsEditorSubtreeVisible(entity)) return;   // pick == render

            var hit = RaycastEntity(ray, entity);
            if (hit != null)
            {
                hits.Add(hit);
            }

            if (entity.Children != null)
            {
                foreach (var child in entity.Children)
                {
                    RaycastEntityRecursive(ray, child, hits);
                }
            }
        }

        private RaycastHit RaycastEntity(Ray ray, GameEntity entity)
        {
            if (entity == null || !entity.IsActive) return null;
            // A mesh that isn't drawn under the current layer presentation (hidden FP viewmodel,
            // third-person-only in game view) must not be clickable either — pick == render.
            if (!SceneRenderService.IsEditorMeshPickable(entity)) return null;

            var transform = entity.GetComponent<Transform>();
            if (transform == null) return null;

            // Pick against the entity's REAL world-space bounds (actual mesh extents + accumulated parent
            // transform), NOT a box derived from LocalScale alone. The old code assumed every mesh was a unit
            // cube, so an imported model at scale 1 got a 0.5-unit hitbox you had to click almost dead-centre —
            // that's the "hitboxes are totally wrong, I have to get ultra close" bug. SceneRenderService already
            // caches mesh bounds (GetMeshBounds) + world matrices, so reuse them for a hitbox that matches what's
            // drawn. Falls back to a scale box if bounds aren't available yet.
            Vector3f center, halfExtents;
            if (!SceneRenderService.Instance.TryGetWorldPickBounds(entity,
                    out center, out halfExtents))
            {
                var pos = transform.LocalPosition;
                var scale = transform.LocalScale;
                center = new Vector3f(pos.X, pos.Y, pos.Z);
                halfExtents = new Vector3f(
                    Math.Max(Math.Abs(scale.X) * 0.5f, 0.25f),
                    Math.Max(Math.Abs(scale.Y) * 0.5f, 0.25f),
                    Math.Max(Math.Abs(scale.Z) * 0.5f, 0.25f));
            }

            float? distance = RayAABBIntersect(ray, center, halfExtents);

            if (distance.HasValue && distance.Value > 0)
            {
                var hitPoint = ray.Origin + ray.Direction * distance.Value;
                return new RaycastHit
                {
                    Entity = entity,
                    Point = hitPoint,
                    Distance = distance.Value
                };
            }

            return null;
        }

        private Ray ScreenToRay(float screenX, float screenY, EditorCameraController camera)
        {
            // Convert screen coordinates to normalized device coordinates (-1 to 1)
            // screenX/Y are 0-1 normalized where 0,0 is top-left
            float ndcX = screenX * 2.0f - 1.0f;  // -1 (left) to +1 (right)
            float ndcY = 1.0f - screenY * 2.0f;  // +1 (top) to -1 (bottom)

            // Get camera position
            var camPos = new Vector3f(camera.PositionX, camera.PositionY, camera.PositionZ);

            // Get camera orientation - MUST match EditorCameraController.UpdateCamera() exactly!
            float yawRad = camera.Yaw * (float)(Math.PI / 180.0);
            float pitchRad = camera.Pitch * (float)(Math.PI / 180.0);

            // Forward direction - EXACTLY as in EditorCameraController
            float fx = (float)(Math.Sin(yawRad) * Math.Cos(pitchRad));
            float fy = (float)(-Math.Sin(pitchRad));
            float fz = (float)(Math.Cos(yawRad) * Math.Cos(pitchRad));
            var forward = new Vector3f(fx, fy, fz).Normalized;

            // CRITICAL: DirectX uses LEFT-HANDED coordinate system!
            // right = cross(up, forward) NOT cross(forward, up)
            // This matches XMMatrixLookAtLH behavior
            var worldUp = new Vector3f(0, 1, 0);
            var right = Cross(worldUp, forward).Normalized;
            var up = Cross(forward, right).Normalized;

            // FOV must match the editor viewport's actual render FOV (see EditorFovYDegrees).
            float fovY = EditorFovYDegrees * (float)(Math.PI / 180.0);
            float tanHalfFov = (float)Math.Tan(fovY * 0.5f);
            
            // Assume 16:9 if we don't have actual dimensions
            float aspectRatio = 16.0f / 9.0f;

            // Calculate ray direction in camera space, then transform to world
            float rayX = ndcX * tanHalfFov * aspectRatio;
            float rayY = ndcY * tanHalfFov;

            // World-space ray direction
            var rayDir = forward + right * rayX + up * rayY;
            rayDir = rayDir.Normalized;

            return new Ray
            {
                Origin = camPos,
                Direction = rayDir
            };
        }

        /// <summary>
        /// Screen to ray with actual aspect ratio from viewport.
        /// </summary>
        public Ray ScreenToRayWithAspect(float screenX, float screenY, float aspectRatioOrWidth, float heightOrUnused)
        {
            var camera = EditorCameraController.Instance;
            
            // If heightOrUnused > 1, treat as width/height, otherwise treat first param as aspect ratio
            float aspectRatio = heightOrUnused > 1.0f ? (aspectRatioOrWidth / heightOrUnused) : aspectRatioOrWidth;
            
            // Convert screen coordinates to NDC
            float ndcX = screenX * 2.0f - 1.0f;
            float ndcY = 1.0f - screenY * 2.0f;

            var camPos = new Vector3f(camera.PositionX, camera.PositionY, camera.PositionZ);

            float yawRad = camera.Yaw * (float)(Math.PI / 180.0);
            float pitchRad = camera.Pitch * (float)(Math.PI / 180.0);

            // Forward - matching EditorCameraController exactly
            float fx = (float)(Math.Sin(yawRad) * Math.Cos(pitchRad));
            float fy = (float)(-Math.Sin(pitchRad));
            float fz = (float)(Math.Cos(yawRad) * Math.Cos(pitchRad));
            var forward = new Vector3f(fx, fy, fz).Normalized;

            // CRITICAL: DirectX uses LEFT-HANDED coordinate system!
            // right = cross(up, forward) NOT cross(forward, up)
            // This matches XMMatrixLookAtLH behavior
            var worldUp = new Vector3f(0, 1, 0);
            var right = Cross(worldUp, forward).Normalized;
            var up = Cross(forward, right).Normalized;

            // FOV must match the editor viewport's actual render FOV (see EditorFovYDegrees).
            float fovY = EditorFovYDegrees * (float)(Math.PI / 180.0);
            float tanHalfFov = (float)Math.Tan(fovY * 0.5f);

            float rayX = ndcX * tanHalfFov * aspectRatio;
            float rayY = ndcY * tanHalfFov;

            var rayDir = forward + right * rayX + up * rayY;
            rayDir = rayDir.Normalized;

            return new Ray
            {
                Origin = camPos,
                Direction = rayDir
            };
        }

        /// <summary>
        /// Ray-AABB intersection (slab method).
        /// </summary>
        private float? RayAABBIntersect(Ray ray, Vector3f center, Vector3f halfExtents)
        {
            float tMin = float.MinValue;
            float tMax = float.MaxValue;

            // For each axis
            float[] origins = { ray.Origin.X, ray.Origin.Y, ray.Origin.Z };
            float[] dirs = { ray.Direction.X, ray.Direction.Y, ray.Direction.Z };
            float[] centers = { center.X, center.Y, center.Z };
            float[] halfs = { halfExtents.X, halfExtents.Y, halfExtents.Z };

            for (int i = 0; i < 3; i++)
            {
                float bmin = centers[i] - halfs[i];
                float bmax = centers[i] + halfs[i];

                if (Math.Abs(dirs[i]) < 0.0001f)
                {
                    // Ray is parallel to slab
                    if (origins[i] < bmin || origins[i] > bmax)
                        return null;
                }
                else
                {
                    float t1 = (bmin - origins[i]) / dirs[i];
                    float t2 = (bmax - origins[i]) / dirs[i];

                    if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }

                    tMin = Math.Max(tMin, t1);
                    tMax = Math.Min(tMax, t2);

                    if (tMin > tMax) return null;
                }
            }

            if (tMin > 0) return tMin;
            if (tMax > 0) return tMax;
            return null;
        }

        private float? RaySphereIntersect(Ray ray, Vector3f center, float radius)
        {
            var oc = ray.Origin - center;
            
            float a = Dot(ray.Direction, ray.Direction);
            float b = 2.0f * Dot(oc, ray.Direction);
            float c = Dot(oc, oc) - radius * radius;
            
            float discriminant = b * b - 4 * a * c;
            
            if (discriminant < 0)
            {
                return null;
            }
            
            float sqrtD = (float)Math.Sqrt(discriminant);
            float t1 = (-b - sqrtD) / (2 * a);
            float t2 = (-b + sqrtD) / (2 * a);
            
            if (t1 > 0) return t1;
            if (t2 > 0) return t2;
            
            
            return null;
        }

        private float Dot(Vector3f a, Vector3f b)
        {
            return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        }

        private Vector3f Cross(Vector3f a, Vector3f b)
        {
            return new Vector3f(
                a.Y * b.Z - a.Z * b.Y,
                a.Z * b.X - a.X * b.Z,
                a.X * b.Y - a.Y * b.X);
        }

        /// <summary>
        /// Check if a ray hits a gizmo axis.
        /// </summary>
        public GizmoAxis PickGizmoAxis(float screenX, float screenY, Vector3f gizmoCenter, float scale = 1.0f)
        {
            return PickGizmoAxis(screenX, screenY, gizmoCenter, 16.0f / 9.0f, scale);
        }

        /// <summary>Camera-distance-based gizmo scale so the handles stay a roughly CONSTANT SIZE ON SCREEN
        /// (Blender/Unreal behaviour) instead of shrinking to a few unclickable pixels when you zoom out. The SAME
        /// value must feed both the RENDER (SceneRenderService.SubmitOverlays -> VortexAPI.RenderGizmo) and the
        /// PICK (PickGizmoAxis) so the clickable boxes always sit exactly on the drawn arrows. Both callers pass the
        /// selected entity's gizmo centre, so they compute an identical scale from the shared editor camera.</summary>
        public static float ComputeGizmoScale(Vector3f gizmoCenter)
        {
            var cam = EditorCameraController.Instance;
            if (cam == null) return 1.0f;
            float dx = gizmoCenter.X - cam.PositionX;
            float dy = gizmoCenter.Y - cam.PositionY;
            float dz = gizmoCenter.Z - cam.PositionZ;
            float dist = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            // ~0.13 * distance keeps the gizmo at a fixed fraction of the vertical view (see EditorFovYDegrees).
            // Clamp so it never collapses to nothing up close nor explodes at extreme distance.
            float s = dist * 0.13f;
            if (s < 0.35f) s = 0.35f; else if (s > 60.0f) s = 60.0f;
            return s;
        }

        /// <summary>
        /// Check if a ray hits a gizmo axis with explicit aspect ratio.
        /// Supports all gizmo types: Translate, Rotate, Scale.
        /// </summary>
        public GizmoAxis PickGizmoAxis(float screenX, float screenY, Vector3f gizmoCenter, float aspectRatio, float scale)
        {
            var ray = ScreenToRayWithAspect(screenX, screenY, aspectRatio, 1.0f);

            // Get current gizmo mode from TransformGizmoService
            var gizmoMode = TransformGizmoService.Instance.CurrentMode;

            if (gizmoMode == TransformGizmoService.GizmoMode.Rotate)
            {
                // Use torus/circle picking for rotation gizmo
                return PickRotationGizmo(ray, gizmoCenter, scale);
            }
            else
            {
                // Use axis picking for translate/scale gizmo
                return PickAxisGizmo(ray, gizmoCenter, scale);
            }
        }

        /// <summary>
        /// Pick translate/scale gizmo axes (straight lines with arrows/cubes).
        /// </summary>
        private GizmoAxis PickAxisGizmo(Ray ray, Vector3f gizmoCenter, float scale)
        {
            // Match the ACTUAL rendered gizmo size (read the same constants the renderer uses, so the clickable
            // hitboxes always line up with the visible arrows even when the sizes change).
            float axisLength = Editor.DllWrapper.VortexAPI.GIZMO_LENGTH * scale;
            float axisThickness = 0.35f * scale;                                      // generous hit area for easy clicking
            float arrowTipExtra = Editor.DllWrapper.VortexAPI.GIZMO_ARROW_LENGTH * scale;

            // Test X axis (red) - extends from center to +X
            var xAxisCenter = new Vector3f(gizmoCenter.X + (axisLength + arrowTipExtra) * 0.5f, gizmoCenter.Y, gizmoCenter.Z);
            var xAxisExtents = new Vector3f((axisLength + arrowTipExtra) * 0.5f + 0.1f, axisThickness, axisThickness);
            if (RayAABBIntersect(ray, xAxisCenter, xAxisExtents).HasValue)
                return GizmoAxis.X;

            // Test Y axis (green) - extends from center to +Y
            var yAxisCenter = new Vector3f(gizmoCenter.X, gizmoCenter.Y + (axisLength + arrowTipExtra) * 0.5f, gizmoCenter.Z);
            var yAxisExtents = new Vector3f(axisThickness, (axisLength + arrowTipExtra) * 0.5f + 0.1f, axisThickness);
            if (RayAABBIntersect(ray, yAxisCenter, yAxisExtents).HasValue)
                return GizmoAxis.Y;

            // Test Z axis (blue) - extends from center to +Z
            var zAxisCenter = new Vector3f(gizmoCenter.X, gizmoCenter.Y, gizmoCenter.Z + (axisLength + arrowTipExtra) * 0.5f);
            var zAxisExtents = new Vector3f(axisThickness, axisThickness, (axisLength + arrowTipExtra) * 0.5f + 0.1f);
            if (RayAABBIntersect(ray, zAxisCenter, zAxisExtents).HasValue)
                return GizmoAxis.Z;

            return GizmoAxis.None;
        }

        /// <summary>
        /// Pick rotation gizmo circles (torus-like shapes).
        /// </summary>
        private GizmoAxis PickRotationGizmo(Ray ray, Vector3f center, float scale)
        {
            float radius = Editor.DllWrapper.VortexAPI.GIZMO_LENGTH * scale; // Circle radius (matches the rendered rings)
            float tubeRadius = 0.28f * scale; // generous pick tolerance around the ring

            // Test each circle - check if ray passes near the circle
            // X rotation circle (in YZ plane)
            float? distX = RayCircleDistance(ray, center, radius, 0);
            if (distX.HasValue && distX.Value < tubeRadius)
                return GizmoAxis.X;

            // Y rotation circle (in XZ plane)
            float? distY = RayCircleDistance(ray, center, radius, 1);
            if (distY.HasValue && distY.Value < tubeRadius)
                return GizmoAxis.Y;

            // Z rotation circle (in XY plane)
            float? distZ = RayCircleDistance(ray, center, radius, 2);
            if (distZ.HasValue && distZ.Value < tubeRadius)
                return GizmoAxis.Z;

            return GizmoAxis.None;
        }

        /// <summary>
        /// Calculate the minimum distance from a ray to a circle.
        /// axis: 0=X (YZ plane), 1=Y (XZ plane), 2=Z (XY plane)
        /// </summary>
        private float? RayCircleDistance(Ray ray, Vector3f center, float radius, int axis)
        {
            // Get the plane normal based on axis
            Vector3f planeNormal;
            switch (axis)
            {
                case 0: planeNormal = new Vector3f(1, 0, 0); break; // X axis - YZ plane
                case 1: planeNormal = new Vector3f(0, 1, 0); break; // Y axis - XZ plane
                case 2: planeNormal = new Vector3f(0, 0, 1); break; // Z axis - XY plane
                default: return null;
            }

            // Calculate ray-plane intersection
            float denom = Dot(planeNormal, ray.Direction);
            if (Math.Abs(denom) < 0.0001f)
            {
                // Ray is parallel to plane - check distance to plane
                float distToPlane = Math.Abs(Dot(planeNormal, ray.Origin - center));
                if (distToPlane > 0.2f * radius) return null;
                
                // Find closest point on ray to circle center
                // This is an approximation for nearly-parallel rays
                return 0.1f; // Return small value to allow picking
            }

            float t = Dot(planeNormal, center - ray.Origin) / denom;
            if (t < 0) return null; // Intersection behind ray

            // Point where ray intersects the plane
            var hitPoint = ray.Origin + ray.Direction * t;

            // Distance from hit point to circle center (in the plane)
            float distToCenter;
            switch (axis)
            {
                case 0: distToCenter = (float)Math.Sqrt((hitPoint.Y - center.Y) * (hitPoint.Y - center.Y) + (hitPoint.Z - center.Z) * (hitPoint.Z - center.Z)); break;
                case 1: distToCenter = (float)Math.Sqrt((hitPoint.X - center.X) * (hitPoint.X - center.X) + (hitPoint.Z - center.Z) * (hitPoint.Z - center.Z)); break;
                case 2: distToCenter = (float)Math.Sqrt((hitPoint.X - center.X) * (hitPoint.X - center.X) + (hitPoint.Y - center.Y) * (hitPoint.Y - center.Y)); break;
                default: return null;
            }

            // Distance from hit point to the circle itself
            return Math.Abs(distToCenter - radius);
        }

        /// <summary>
        /// Calculate the world-space delta for dragging along an axis.
        /// Uses proper 3D projection for accurate movement from any camera angle.
        /// </summary>
        public Vector3f CalculateAxisDragDelta(float screenX, float screenY, float lastScreenX, float lastScreenY,
                                                Vector3f entityPos, GizmoAxis axis)
        {
            var camera = EditorCameraController.Instance;
            
            // Get camera vectors
            float yawRad = camera.Yaw * (float)(Math.PI / 180.0);
            float pitchRad = camera.Pitch * (float)(Math.PI / 180.0);
            
            float fx = (float)(Math.Sin(yawRad) * Math.Cos(pitchRad));
            float fy = (float)(-Math.Sin(pitchRad));
            float fz = (float)(Math.Cos(yawRad) * Math.Cos(pitchRad));
            var forward = new Vector3f(fx, fy, fz).Normalized;
            
            var worldUp = new Vector3f(0, 1, 0);
            var right = Cross(worldUp, forward).Normalized;
            var up = Cross(forward, right).Normalized;

            // Get the axis direction in world space
            Vector3f axisDir;
            switch (axis)
            {
                case GizmoAxis.X: axisDir = new Vector3f(1, 0, 0); break;
                case GizmoAxis.Y: axisDir = new Vector3f(0, 1, 0); break;
                case GizmoAxis.Z: axisDir = new Vector3f(0, 0, 1); break;
                default: return new Vector3f(0, 0, 0);
            }

            // Project the axis onto the screen plane
            // This gives us how much the axis appears to move horizontally/vertically on screen
            float axisOnRight = Dot(axisDir, right);
            float axisOnUp = Dot(axisDir, up);
            
            // Get screen delta
            float deltaX = (screenX - lastScreenX);
            float deltaY = -(screenY - lastScreenY); // Flip Y
            
            // Calculate how much the mouse movement aligns with the projected axis
            float screenAxisLength = (float)Math.Sqrt(axisOnRight * axisOnRight + axisOnUp * axisOnUp);
            
            if (screenAxisLength < 0.001f)
            {
                // Axis is pointing directly at/away from camera - use a fallback
                // When looking straight down an axis, horizontal mouse = movement along that axis
                float dist = (entityPos - new Vector3f(camera.PositionX, camera.PositionY, camera.PositionZ)).Length;
                float fallbackDelta = deltaX * dist * 2.0f;
                return axisDir * fallbackDelta;
            }
            
            // Normalize the projected axis
            float normRight = axisOnRight / screenAxisLength;
            float normUp = axisOnUp / screenAxisLength;
            
            // Dot product of mouse delta with projected axis direction
            float movement = (deltaX * normRight + deltaY * normUp);
            
            // Scale by distance for consistent feel
            float distance = (entityPos - new Vector3f(camera.PositionX, camera.PositionY, camera.PositionZ)).Length;
            float speed = distance * 2.0f;
            
            return axisDir * (movement * speed);
        }
    }

    public enum GizmoAxis
    {
        None,
        X,
        Y,
        Z
    }

    /// <summary>
    /// Ray structure for raycasting.
    /// </summary>
    public struct Ray
    {
        public Vector3f Origin;
        public Vector3f Direction;
    }
}

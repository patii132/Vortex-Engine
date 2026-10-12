using System;
using Editor.Core.Data;

namespace Editor.Core.Services
{
    /// <summary>
    /// The editor camera's place in a scene, kept in the scene's <see cref="SceneSettings"/>: a scene opens where its author
    /// last stood when saving it, instead of at the origin — a fresh project from a template shows the world the way the
    /// template's pictures do, and Home (Reset Camera) returns there. The pose is scene data the game never reads; scenes
    /// saved before this exist simply have none and start at the classic default.
    /// </summary>
    public static class EditorViewState
    {
        /// <summary>Write the editor camera's current pose into the scene (called when the active scene is saved).</summary>
        public static void Capture(Scene scene)
        {
            if (scene == null || scene.Settings == null) return;
            var cam = EditorCameraController.Instance;
            if (cam == null) return;
            if (float.IsNaN(cam.PositionX) || float.IsNaN(cam.PositionY) || float.IsNaN(cam.PositionZ)) return;
            var s = scene.Settings;
            s.EditorViewSet = true;
            s.EditorViewX = cam.PositionX; s.EditorViewY = cam.PositionY; s.EditorViewZ = cam.PositionZ;
            s.EditorViewYaw = cam.Yaw; s.EditorViewPitch = cam.Pitch;
        }

        /// <summary>Put the editor camera where the scene was saved from. False when the scene carries no view.</summary>
        public static bool Restore(Scene scene)
        {
            var s = scene?.Settings;
            if (s == null || !s.EditorViewSet) return false;
            var cam = EditorCameraController.Instance;
            if (cam == null) return false;
            cam.SetPositionAndRotation(s.EditorViewX, s.EditorViewY, s.EditorViewZ, s.EditorViewYaw, s.EditorViewPitch);
            try { Editor.Core.Viewport.EditorViewportSession.RequestResubmit(); } catch { }
            return true;
        }

        /// <summary>Forget the saved view (the next save records the current camera again).</summary>
        public static void Clear(Scene scene)
        {
            if (scene?.Settings == null) return;
            scene.Settings.EditorViewSet = false;
        }
    }
}

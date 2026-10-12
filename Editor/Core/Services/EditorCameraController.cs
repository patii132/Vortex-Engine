using System;
using Point = Editor.Core.Input.PointD;
using Editor.DllWrapper;

namespace Editor.Core.Services
{
    /// <summary>
    /// Simple FPS-style camera controller.
    /// Click in viewport, then use WASD + mouse to move freely.
    /// Hold right-click to look around.
    /// </summary>
    public class EditorCameraController
    {
        private static EditorCameraController _instance;
        public static EditorCameraController Instance => _instance ?? (_instance = new EditorCameraController());

        // Camera position
        private float _posX = 0.0f;
        private float _posY = 2.0f;
        private float _posZ = -5.0f;

        // Camera angles (degrees)
        private float _yaw = 0.0f;
        private float _pitch = 0.0f;
        private float _roll = 0.0f;  // Added roll support for tilted camera views

        // Mouse tracking
        private Point _lastMouse;
        private bool _rightMouseDown;

        // Movement keys
        private bool _wKey, _sKey, _aKey, _dKey, _qKey, _eKey, _shiftKey;

        // Settings
        public float MoveSpeed { get; set; } = 5.0f;
        public float LookSpeed { get; set; } = 0.2f;
        public float SprintMultiplier { get; set; } = 2.5f;

        // Public properties
        public float PositionX => _posX;
        public float PositionY => _posY;
        public float PositionZ => _posZ;
        public float Yaw => _yaw;
        public float Pitch => _pitch;
        public float Roll => _roll;
        public bool IsFlyMode => _rightMouseDown;

        private EditorCameraController()
        {
            UpdateCamera();
        }

        /// <summary>Home: back to the view the active scene was saved from (<see cref="EditorViewState"/>), or the classic
        /// start pose for a scene without one.</summary>
        public void Reset()
        {
            try
            {
                var scene = Editor.Core.Data.ProjectData.Current?.ActiveScene;
                if (scene != null && scene.Settings != null && scene.Settings.EditorViewSet)
                {
                    SetPositionAndRotation(scene.Settings.EditorViewX, scene.Settings.EditorViewY, scene.Settings.EditorViewZ, scene.Settings.EditorViewYaw, scene.Settings.EditorViewPitch);
                    return;
                }
            }
            catch { }
            _posX = 0; _posY = 2; _posZ = -5;
            _yaw = 0; _pitch = 0; _roll = 0;
            UpdateCamera();
        }

        public void FocusOn(float x, float y, float z, float distance = 5)
        {
            _posX = x;
            _posY = y + 2;
            _posZ = z - distance;
            _roll = 0;  // Reset roll when focusing
            UpdateCamera();
        }
        
        /// <summary>
        /// Set camera position and rotation directly (used when switching to a game camera view).
        /// </summary>
        public void SetPositionAndRotation(float x, float y, float z, float yaw, float pitch)
        {
            _posX = x;
            _posY = y;
            _posZ = z;
            _yaw = yaw;
            _pitch = Math.Max(-89, Math.Min(89, pitch));
            _roll = 0;
            UpdateCamera();
        }
        
        /// <summary>
        /// Set camera position and full rotation including roll (for viewing through game cameras).
        /// Uses Entity Euler angles: rotX = pitch, rotY = yaw, rotZ = roll
        /// </summary>
        public void SetFromEntityTransform(float posX, float posY, float posZ, float rotX, float rotY, float rotZ)
        {
            _posX = posX;
            _posY = posY;
            _posZ = posZ;
            
            // Entity rotation: X = pitch, Y = yaw, Z = roll
            // EditorCamera convention is different, need to map correctly
            _yaw = rotY;      // Horizontal rotation (looking left/right)
            _pitch = rotX;    // Vertical rotation (looking up/down) - NOT inverted!
            _roll = rotZ;     // Tilt rotation
            
            UpdateCamera();
        }


        // Framework-neutral input entry points (the Avalonia editor and any other shell drive the camera through
        // these; the WPF viewport keeps its event-typed overloads above).
        public void OnMouseDown(bool rightButton, Point pos)
        {
            _lastMouse = pos;
            if (rightButton) _rightMouseDown = true;
        }

        public void OnMouseUp(bool rightButton)
        {
            if (rightButton)
            {
                _rightMouseDown = false;
                _wKey = _sKey = _aKey = _dKey = _qKey = _eKey = _shiftKey = false;
            }
        }

        /// <summary>Movement key by Windows virtual-key code (W/A/S/D/Q/E, shift, home). ctrlOrAlt suppresses the
        /// editor command chords exactly like the WPF path.</summary>
        public void OnKeyDown(int vk, bool ctrlOrAlt)
        {
            if (ctrlOrAlt) return;
            switch (vk)
            {
                case 0x57: _wKey = true; break;
                case 0x53: _sKey = true; break;
                case 0x41: _aKey = true; break;
                case 0x44: _dKey = true; break;
                case 0x51: _qKey = true; break;
                case 0x45: _eKey = true; break;
                case 0x10: case 0xA0: case 0xA1: _shiftKey = true; break;
                case 0x24: Reset(); break;
            }
        }

        public void OnKeyUp(int vk)
        {
            switch (vk)
            {
                case 0x57: _wKey = false; break;
                case 0x53: _sKey = false; break;
                case 0x41: _aKey = false; break;
                case 0x44: _dKey = false; break;
                case 0x51: _qKey = false; break;
                case 0x45: _eKey = false; break;
                case 0x10: case 0xA0: case 0xA1: _shiftKey = false; break;
            }
        }

        /// <summary>Physical key state from the host shell — true while the key is really held. Used only to
        /// RELEASE stale movement flags (see Update).</summary>
        private static bool IsPhysicallyDown(int vk)
        {
            try { return Editor.Core.Input.HostInput.IsKeyDown(vk); }
            catch { return false; }
        }

        public void OnMouseMove(Point pos)
        {
            if (!_rightMouseDown) 
            {
                _lastMouse = pos;
                return;
            }

            float dx = (float)(pos.X - _lastMouse.X);
            float dy = (float)(pos.Y - _lastMouse.Y);
            _lastMouse = pos;

            _yaw += dx * LookSpeed;
            _pitch += dy * LookSpeed;  // FIXED: Removed minus sign to fix inverted Y-axis
            _pitch = Math.Max(-89, Math.Min(89, _pitch));


            UpdateCamera();
        }

        public void OnMouseWheel(int delta)
        {
            // Dolly forward/backward
            float amount = delta > 0 ? 1.0f : -1.0f;
            MoveInLookDirection(amount);
            UpdateCamera();
        }


        public void Update(float dt)
        {
            // Only move when right mouse is held
            if (!_rightMouseDown) return;

            // SELF-HEAL stuck flags before applying movement. The flags are fed by WPF KeyDown/KeyUp pairs,
            // and the Up half is easy to lose: ESC calls Keyboard.ClearFocus, a panel click moves focus, a
            // Ctrl-command opens a dialog — release the key afterwards and no KeyUp ever reaches the viewport.
            // The stale flag then moved the camera on its own every time fly mode started (the "camera
            // constantly flies down" bug = a stuck Q). GetAsyncKeyState is the physical truth independent of
            // WPF focus, so a flag may only STAY on while its key is really held. (Never sets flags — typing
            // W in some other panel must not fly the camera; only the viewport's KeyDown can turn a flag on.)
            if (_wKey && !IsPhysicallyDown(0x57)) _wKey = false;         // 'W'
            if (_sKey && !IsPhysicallyDown(0x53)) _sKey = false;         // 'S'
            if (_aKey && !IsPhysicallyDown(0x41)) _aKey = false;         // 'A'
            if (_dKey && !IsPhysicallyDown(0x44)) _dKey = false;         // 'D'
            if (_qKey && !IsPhysicallyDown(0x51)) _qKey = false;         // 'Q'
            if (_eKey && !IsPhysicallyDown(0x45)) _eKey = false;         // 'E'
            if (_shiftKey && !IsPhysicallyDown(0x10)) _shiftKey = false; // VK_SHIFT

            float speed = MoveSpeed * dt;
            if (_shiftKey) speed *= SprintMultiplier;

            // W/S moves in look direction (including up/down based on pitch)
            if (_wKey) MoveInLookDirection(speed);
            if (_sKey) MoveInLookDirection(-speed);
            if (_dKey) MoveRight(speed);
            if (_aKey) MoveRight(-speed);
            // E/Q for pure vertical movement (optional)
            if (_eKey) _posY += speed;
            if (_qKey) _posY -= speed;

            UpdateCamera();
        }

        private void MoveInLookDirection(float amount)
        {
            // Move in the actual look direction (including pitch for Y movement)
            float yawRad = _yaw * (float)(Math.PI / 180);
            float pitchRad = _pitch * (float)(Math.PI / 180);

            float fx = (float)(Math.Sin(yawRad) * Math.Cos(pitchRad));
            float fy = (float)(-Math.Sin(pitchRad));
            float fz = (float)(Math.Cos(yawRad) * Math.Cos(pitchRad));

            _posX += fx * amount;
            _posY += fy * amount;
            _posZ += fz * amount;
        }

        private void MoveRight(float amount)
        {
            float yawRad = _yaw * (float)(Math.PI / 180);
            _posX += (float)Math.Cos(yawRad) * amount;
            _posZ -= (float)Math.Sin(yawRad) * amount;
        }

        private void UpdateCamera()
        {
            float yawRad = _yaw * (float)(Math.PI / 180);
            float pitchRad = _pitch * (float)(Math.PI / 180);
            float rollRad = _roll * (float)(Math.PI / 180);

            // Forward direction
            float fx = (float)(Math.Sin(yawRad) * Math.Cos(pitchRad));
            float fy = (float)(-Math.Sin(pitchRad));
            float fz = (float)(Math.Cos(yawRad) * Math.Cos(pitchRad));

            // Look target = position + forward
            float tx = _posX + fx;
            float ty = _posY + fy;
            float tz = _posZ + fz;
            
            // Calculate up vector with roll
            // Start with world up, then apply roll rotation around forward axis
            float upX, upY, upZ;
            
            if (Math.Abs(_roll) < 0.001f)
            {
                // No roll - use simple up vector
                upX = 0; upY = 1; upZ = 0;
            }
            else
            {
                // Calculate right vector (cross of forward and world up)
                float rightX = (float)(Math.Cos(yawRad));
                float rightY = 0;
                float rightZ = (float)(-Math.Sin(yawRad));
                
                // Calculate actual up from forward and right (cross product)
                float baseUpX = fy * rightZ - fz * rightY;
                float baseUpY = fz * rightX - fx * rightZ;
                float baseUpZ = fx * rightY - fy * rightX;
                
                // Normalize base up
                float upLen = (float)Math.Sqrt(baseUpX * baseUpX + baseUpY * baseUpY + baseUpZ * baseUpZ);
                if (upLen > 0.0001f)
                {
                    baseUpX /= upLen;
                    baseUpY /= upLen;
                    baseUpZ /= upLen;
                }
                else
                {
                    baseUpX = 0; baseUpY = 1; baseUpZ = 0;
                }
                
                // Apply roll rotation using Rodrigues' rotation formula
                // Rotate baseUp around forward axis by roll angle
                float cosR = (float)Math.Cos(rollRad);
                float sinR = (float)Math.Sin(rollRad);
                
                // Dot product of forward and baseUp
                float dot = fx * baseUpX + fy * baseUpY + fz * baseUpZ;
                
                // Cross product of forward and baseUp
                float crossX = fy * baseUpZ - fz * baseUpY;
                float crossY = fz * baseUpX - fx * baseUpZ;
                float crossZ = fx * baseUpY - fy * baseUpX;
                
                // Rodrigues formula: v_rot = v*cos(?) + (k�v)*sin(?) + k*(k�v)*(1-cos(?))
                upX = baseUpX * cosR + crossX * sinR + fx * dot * (1 - cosR);
                upY = baseUpY * cosR + crossY * sinR + fy * dot * (1 - cosR);
                upZ = baseUpZ * cosR + crossZ * sinR + fz * dot * (1 - cosR);
            }

            try
            {
                VortexAPI.SetViewCamera(_posX, _posY, _posZ, tx, ty, tz, upX, upY, upZ);
            }
            catch
            {
                // Ignore if renderer not ready yet
            }
        }
    }
}

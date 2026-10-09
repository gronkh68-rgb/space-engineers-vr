using System;
using VRage.Input;

namespace SeVr.Vr
{
    /// <summary>Xbox-style gamepad built from VR controller input; fed to the game as its joystick state.</summary>
    internal sealed class VirtualPad
    {
        public float LX, LY, RX, RY, LT, RT;
        public readonly bool[] Buttons = new bool[16];

        public void Reset()
        {
            LX = LY = RX = RY = LT = RT = 0f;
            Array.Clear(Buttons, 0, Buttons.Length);
        }

        /// <summary>Menus: build the pad from the VR sources (stick = navigate, A = select, B = back ...).</summary>
        public void FillFromControls()
        {
            Reset();
            LX = VrControls.MoveX; LY = VrControls.MoveY; RX = VrControls.TurnX; RY = VrControls.TurnY;
            LT = VrControls.TrigL; RT = VrControls.TrigR;
            Buttons[0] = VrControls.RawDown("jump"); Buttons[1] = VrControls.RawDown("crouch");
            Buttons[2] = VrControls.RawDown("x"); Buttons[3] = VrControls.RawDown("y");
            Buttons[4] = VrControls.GripL > 0.6f; Buttons[5] = VrControls.GripR > 0.6f;
            Buttons[7] = VrControls.RawDown("menu");
        }

        public void SetButton(string target, bool down)
        {
            if (target.Length == 3 && target[0] == 'J' && int.TryParse(target.Substring(1), out int n) && n >= 1 && n <= 16)
                Buttons[n - 1] |= down;
        }

        public void SetAxis(string target, float v)
        {
            if (target == "LT") LT = Math.Max(LT, v);
            else if (target == "RT") RT = Math.Max(RT, v);
        }

        public void SetStick(string target, float x, float y)
        {
            if (target == "LX,LY") { LX = x; LY = y; }
            else if (target == "RX,RY") { RX = x; RY = y; }
        }

        static int Axis(float v) => (int)Math.Max(0, Math.Min(65535, 32767f + v * 32767f));

        public unsafe MyJoystickState ToState()
        {
            var s = new MyJoystickState();
            s.X = Axis(LX);
            s.Y = Axis(-LY);              // stick up = low value (DirectInput convention used by the game)
            s.RotationX = Axis(RX);
            s.RotationY = Axis(-RY);
            s.Z = 32767; s.RotationZ = 32767;
            s.Sliders[0] = 32767; s.Sliders[1] = 32767;
            for (int i = 0; i < 4; i++) s.PointOfViewControllers[i] = -1;
            s.Z_Left = LT > 0.1f ? Axis(LT) : 0;
            s.Z_Right = RT > 0.1f ? Axis(RT) : 0;
            for (int i = 0; i < 16; i++) s.Buttons[i] = (byte)(Buttons[i] ? 0x80 : 0);
            return s;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SeVr.Xr;
using VRage.Input;
using VRage.Input.Keyboard;
using VRage.Utils;
using VRageMath;

namespace SeVr.Vr
{
    /// <summary>
    /// Feeds the VR control scheme into the game.
    /// Gameplay: the game's own control queries and its movement/rotation aggregators are extended (no fake gamepad).
    /// Menus: a virtual Xbox pad is attached so the existing gamepad menu navigation works.
    /// </summary>
    internal static class VrInput
    {
        static AccessTools.FieldRef<MyVRageInput, bool> connectedRef;
        static AccessTools.FieldRef<MyVRageInput, MyJoystickState> actualRef, previousRef;
        static MyJoystickState lastPad;
        static bool loggedInput;
        const float StickRotationUnits = 9f;      // same scale the engine uses for gamepad rotation (stick * 9)

        static bool Vr { get { return VrRenderer.Active && XrRuntime.Instance != null; } }
        static bool Gameplay { get { return Vr && !MenuState.IsMenuOpen(); } }
        static bool InMenu { get { return Vr && MenuState.IsMenuOpen(); } }

        public static void Install(Harmony h)
        {
            connectedRef = AccessTools.FieldRefAccess<MyVRageInput, bool>("m_joystickConnected");
            actualRef = AccessTools.FieldRefAccess<MyVRageInput, MyJoystickState>("m_actualJoystickState");
            previousRef = AccessTools.FieldRefAccess<MyVRageInput, MyJoystickState>("m_previousJoystickState");
            var self = typeof(VrInput);
            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            Func<string, HarmonyMethod> m = n => new HarmonyMethod(self.GetMethod(n, flags));
            var t = typeof(MyVRageInput);

            h.Patch(AccessTools.Method(t, "UpdateStates", Type.EmptyTypes), prefix: m(nameof(UpdateStatesPrefix)), postfix: m(nameof(UpdateStatesPostfix)));
            h.Patch(AccessTools.Method(t, "IsJoystickAxisSupported"), postfix: m(nameof(AxisSupportedPostfix)));
            h.Patch(AccessTools.Method(t, "SetConnectedJoystick"), prefix: m(nameof(SetConnectedPrefix)));

            // gameplay: VR sources answer the game's control queries
            h.Patch(AccessTools.Method(t, "IsGameControlPressed"), postfix: m(nameof(PressedPostfix)));
            h.Patch(AccessTools.Method(t, "IsNewGameControlPressed"), postfix: m(nameof(NewPressedPostfix)));
            h.Patch(AccessTools.Method(t, "IsNewGameControlReleased"), postfix: m(nameof(NewReleasedPostfix)));
            h.Patch(AccessTools.Method(t, "GetGameControlAnalogState"), postfix: m(nameof(AnalogPostfix)));

            // gameplay: some VR buttons press real keyboard keys
            h.Patch(AccessTools.Method(t, "IsKeyPress", new[] { typeof(MyKeys) }), postfix: m(nameof(KeyPressPostfix)));
            h.Patch(AccessTools.Method(t, "IsNewKeyPressed", new[] { typeof(MyKeys) }), postfix: m(nameof(KeyNewPressedPostfix)));
            h.Patch(AccessTools.Method(t, "IsNewKeyReleased", new[] { typeof(MyKeys) }), postfix: m(nameof(KeyNewReleasedPostfix)));

            // gameplay: movement, look and roll aggregators (extension methods on IMyInput)
            var ext = typeof(MyInputExtensions);
            var iface = new[] { typeof(VRage.Input.IMyInput) };
            h.Patch(AccessTools.Method(ext, "GetPositionDelta", iface), postfix: m(nameof(PositionDeltaPostfix)));
            h.Patch(AccessTools.Method(ext, "GetRotation", iface), postfix: m(nameof(RotationPostfix)));
            h.Patch(AccessTools.Method(ext, "GetRoll", iface), postfix: m(nameof(RollPostfix)));
        }

        // ------------------------------------------------------------ menus: laser pointer as the mouse

        static AccessTools.FieldRef<MyVRageInput, MyMouseState> mouseRef, mouseRawRef;
        static AccessTools.FieldRef<MyVRageInput, Vector2> mousePosRef;

        static void UpdateStatesPrefix(MyVRageInput __instance)
        {
            if (Vr)
            {
                connectedRef(__instance) = false;              // never a pad: the game must stay in its mouse and keyboard scheme
                __instance.IsJoystickLastUsed = false;
            }
        }

        static void UpdateStatesPostfix(MyVRageInput __instance)
        {
            if (!Vr) return;
            VrControls.Tick();
            __instance.IsJoystickLastUsed = false;
            if (!InMenu) return;

            // menu open: the right controller's laser is the mouse, the right trigger the left button, the left trigger the right button
            var xr = XrRuntime.Instance;
            if (mouseRef == null)
            {
                mouseRef = AccessTools.FieldRefAccess<MyVRageInput, MyMouseState>("m_actualMouseState");
                mouseRawRef = AccessTools.FieldRefAccess<MyVRageInput, MyMouseState>("m_actualMouseStateRaw");
                mousePosRef = AccessTools.FieldRefAccess<MyVRageInput, Vector2>("m_absoluteMousePosition");
            }
            if (!xr.PointerValid) return;
            // the game converts mouse pixels with the size of the *window* area, not of the VR image, so aim in that space
            Vector2 area = __instance.GetMouseAreaSize();
            float px = xr.PointerU * area.X, py = xr.PointerV * area.Y;
            mousePosRef(__instance) = new Vector2(px, py);
            var ms = mouseRef(__instance);
            ms.X = (int)px; ms.Y = (int)py;
            ms.LeftButton = VrControls.TrigR > 0.5f;
            ms.RightButton = VrControls.TrigL > 0.5f || VrControls.GripR > 0.7f;
            ms.ScrollWheelValue += (int)(VrControls.TurnY * 45f);   // right stick scrolls lists
            mouseRef(__instance) = ms;
            mouseRawRef(__instance) = ms;
            try { __instance.SetMousePosition((int)px, (int)py); } catch { }
        }

        static bool SetConnectedPrefix(string joystickInstanceName)
        {
            return true;
        }

        static void AxisSupportedPostfix(ref bool __result) { }

        // ------------------------------------------------------------ gameplay controls

        static void PressedPostfix(MyStringId controlId, ref bool __result)
        {
            if (!__result && Gameplay && VrControls.Level(controlId)) __result = true;
        }

        static void NewPressedPostfix(MyStringId controlId, ref bool __result)
        {
            if (!__result && Gameplay && VrControls.NewPressed(controlId))
            {
                __result = true;
                if (pulseLog++ < 40) Plugin.Log("VR: control " + controlId.ToString() + " pressed (onFoot=" + VrHead.OnFoot + ")");
            }
        }

        static int pulseLog;

        static void NewReleasedPostfix(MyStringId controlId, ref bool __result)
        {
            if (!__result && Gameplay && VrControls.NewReleased(controlId)) __result = true;
        }

        static void AnalogPostfix(MyStringId controlId, ref float __result)
        {
            if (Gameplay)
            {
                float a = VrControls.Analog(controlId);
                if (a > __result) __result = a;
            }
        }

        static void KeyPressPostfix(MyKeys key, ref bool __result)
        {
            if (!__result && VrControls.KeyLevel(key) && Gameplay) __result = true;
        }

        static void KeyNewPressedPostfix(MyKeys key, ref bool __result)
        {
            if (!__result && key == MyKeys.Escape && VrControls.MenuBackPressed() && InMenu) { __result = true; return; }
            if (!__result && VrControls.KeyNewPressed(key) && Gameplay) __result = true;
        }

        static void KeyNewReleasedPostfix(MyKeys key, ref bool __result)
        {
            if (!__result && VrControls.KeyNewReleased(key) && Gameplay) __result = true;
        }

        static float Clamp1(float v) { return v < -1f ? -1f : (v > 1f ? 1f : v); }

        static void PositionDeltaPostfix(ref Vector3 __result)
        {
            if (!Gameplay) return;
            if (!loggedInput && (VrControls.MoveX != 0 || VrControls.MoveY != 0))
            {
                loggedInput = true;
                Plugin.Log("VR: move input reaching the game (" + VrControls.MoveX.ToString("0.00") + "," + VrControls.MoveY.ToString("0.00") + ")");
            }
            float up = (VrControls.RawDown("jump") ? 1f : 0f) - (VrControls.RawDown("crouch") ? 1f : 0f);
            if (VrHead.OnFoot && !VrHead.Jetpack) up += VrControls.TurnY;   // on foot without jetpack: right stick up/down = up/down
            var move = new Vector3(VrControls.MoveX, 0f, -VrControls.MoveY);   // forward is -Z
            if (VrHead.OnFoot) move = Vector3.TransformNormal(move, VrHead.MoveToBody);   // relative to where you look
            __result.X = Clamp1(__result.X + move.X);
            __result.Y = Clamp1(__result.Y + up + (VrHead.OnFoot ? move.Y : 0f));
            __result.Z = Clamp1(__result.Z + move.Z);
        }

        static void RotationPostfix(ref Vector2 __result)
        {
            if (!Gameplay) return;
            if (VrHead.OnFoot)
            {
                // head steering: the closed loop in VrHead turns the engine's camera towards the head direction
                __result += VrHead.Inject;
            }
            else
            {
                // in a seat the head only looks around; the right stick flies: up = nose up, right = yaw right
                __result.X -= VrControls.TurnY * StickRotationUnits;
                __result.Y += VrControls.TurnX * StickRotationUnits;
            }
        }

        static void RollPostfix(ref float __result)
        {
            if (!Gameplay) return;
            if (VrHead.OnFoot) { __result += VrControls.RollAxis; return; }
            __result += VrControls.GripR - VrControls.GripL;
        }
    }
}

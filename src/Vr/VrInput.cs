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
        static bool KeyboardKeys { get { return InMenu && VrKeyboard.Visible; } }   // Enter / arrows of the on-screen keyboard work in menus

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
            // the game fills its text buffer in Update(bool), after UpdateStates(): typed characters of the on-screen keyboard go in afterwards
            var upd = AccessTools.Method(t, "Update", new[] { typeof(bool) });
            if (upd != null) h.Patch(upd, postfix: m(nameof(UpdatePostfix)));
            // Alt / Shift / Ctrl combinations (F10 shortcuts) ask these, not IsKeyPress
            var alt = AccessTools.Method(t, "IsAnyAltKeyPressed"); if (alt != null) h.Patch(alt, postfix: m(nameof(AltPostfix)));
            var shift = AccessTools.Method(t, "IsAnyShiftKeyPressed"); if (shift != null) h.Patch(shift, postfix: m(nameof(ShiftPostfix)));
            var ctrl = AccessTools.Method(t, "IsAnyCtrlKeyPressed"); if (ctrl != null) h.Patch(ctrl, postfix: m(nameof(CtrlPostfix)));
            // some game code (build planner, ...) asks MyControllerHelper.IsControl instead of IsNewGameControlPressed
            var isCtl = AccessTools.Method(typeof(MyControllerHelper), "IsControl", new[] { typeof(MyStringId), typeof(MyStringId), typeof(MyControlStateType), typeof(bool), typeof(bool) });
            if (isCtl != null) h.Patch(isCtl, postfix: m(nameof(IsControlPostfix)));
            else Plugin.Log("input: MyControllerHelper.IsControl not found");
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
            if (statesLog < 3 && VrKeyboard.KbTestOn) { statesLog++; Plugin.Log("input: UpdateStates postfix, vr=" + Vr + " menu=" + (Vr && MenuState.IsMenuOpen())); }
            if (!Vr) return;
            VrControls.Tick();
            __instance.IsJoystickLastUsed = false;
            if (VrKeyboard.KbTestOn) { var none0 = default(MyMouseState); VrKeyboard.Process(__instance, XrRuntime.Instance, ref none0); return; }   // test switch
            if (!InMenu) { VrKeyboard.Visible = false; return; }

            // menu open: the right controller's laser is the mouse, the right trigger the left button, the left trigger the right button
            var xr = XrRuntime.Instance;
            if (mouseRef == null)
            {
                mouseRef = AccessTools.FieldRefAccess<MyVRageInput, MyMouseState>("m_actualMouseState");
                mouseRawRef = AccessTools.FieldRefAccess<MyVRageInput, MyMouseState>("m_actualMouseStateRaw");
                mousePosRef = AccessTools.FieldRefAccess<MyVRageInput, Vector2>("m_absoluteMousePosition");
            }
            if (!xr.PointerValid) { var none = default(MyMouseState); VrKeyboard.Process(__instance, xr, ref none); return; }
            // the game converts mouse pixels with the size of the *window* area, not of the VR image, so aim in that space
            Vector2 area = __instance.GetMouseAreaSize();
            float px = xr.PointerU * area.X, py = xr.PointerV * area.Y;
            mousePosRef(__instance) = new Vector2(px, py);
            var ms = mouseRef(__instance);
            ms.X = (int)px; ms.Y = (int)py;
            ms.LeftButton = VrControls.TrigR > 0.5f;
            ms.RightButton = VrControls.TrigL > 0.5f || VrControls.GripR > 0.7f;
            ms.ScrollWheelValue += (int)(VrControls.TurnY * 45f);   // right stick scrolls lists
            VrKeyboard.Process(__instance, xr, ref ms);      // on-screen keyboard: may swallow the click, queues typed characters
            mouseRef(__instance) = ms;
            mouseRawRef(__instance) = ms;
            try { __instance.SetMousePosition((int)px, (int)py); } catch { }
        }

        static void IsControlPostfix(MyStringId controlId, MyControlStateType type, ref bool __result)
        {
            if (__result || !Gameplay) return;
            switch (type)
            {
                case MyControlStateType.NEW_PRESSED:
                case MyControlStateType.NEW_PRESSED_REPEATING: __result = VrControls.NewPressed(controlId); break;
                case MyControlStateType.PRESSED: __result = VrControls.Level(controlId); break;
                case MyControlStateType.NEW_RELEASED: __result = VrControls.NewReleased(controlId); break;
            }
        }

        static void UpdatePostfix(MyVRageInput __instance)
        {
            if (Vr) VrKeyboard.Flush(__instance);
        }

        static void AltPostfix(ref bool __result)
        {
            if (!__result && Gameplay && (VrControls.KeyLevel(MyKeys.LeftAlt) || VrControls.KeyLevel(MyKeys.RightAlt))) __result = true;
        }

        static void ShiftPostfix(ref bool __result)
        {
            if (!__result && Gameplay && (VrControls.KeyLevel(MyKeys.LeftShift) || VrControls.KeyLevel(MyKeys.RightShift))) __result = true;
        }

        static void CtrlPostfix(ref bool __result)
        {
            if (!__result && Gameplay && (VrControls.KeyLevel(MyKeys.LeftControl) || VrControls.KeyLevel(MyKeys.RightControl))) __result = true;
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

        static int pulseLog, statesLog;

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
            if (!__result && VrControls.KeyLevel(key) && (Gameplay || KeyboardKeys)) __result = true;
        }

        static void KeyNewPressedPostfix(MyKeys key, ref bool __result)
        {
            if (!__result && key == MyKeys.Escape && VrControls.MenuBackPressed() && InMenu) { __result = true; return; }
            if (!__result && VrControls.KeyNewPressed(key) && (Gameplay || KeyboardKeys)) __result = true;
        }

        static void KeyNewReleasedPostfix(MyKeys key, ref bool __result)
        {
            if (!__result && VrControls.KeyNewReleased(key) && (Gameplay || KeyboardKeys)) __result = true;
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
                var inj = VrHead.Inject;
                if (!float.IsNaN(inj.X) && !float.IsInfinity(inj.X) && !float.IsNaN(inj.Y) && !float.IsInfinity(inj.Y)) __result += inj;
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

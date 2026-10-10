using System;
using System.Collections.Generic;
using System.Text;
using Sandbox;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace SeVr
{
    /// <summary>
    /// The plugin's settings, as a screen of the game itself (Pulsar: plugin list, Space Engineers VR, settings). It is an ordinary game menu,
    /// so in VR it floats in front of you and works with the laser pointer. Changes apply at once and are saved when the screen closes.
    /// </summary>
    internal sealed class SettingsScreen : MyGuiScreenBase
    {
        public static void Open()
        {
            try { MyGuiSandbox.AddScreen(new SettingsScreen()); }
            catch (Exception e) { Plugin.Log("settings screen: " + e); }
        }

        const float RowH = 0.039f;
        Vector2 origin;

        public SettingsScreen()
            : base(new Vector2(0.5f, 0.5f), MyGuiConstants.SCREEN_BACKGROUND_COLOR, new Vector2(0.66f, 0.98f), false, null, MySandboxGame.Config.UIBkOpacity, MySandboxGame.Config.UIOpacity)
        {
            EnabledBackgroundFade = true;
            m_closeOnEsc = true;
            m_drawEvenWithoutFocus = true;
            CanHideOthers = true;
            CanBeHidden = true;
            CloseButtonEnabled = true;
            RecreateControls(true);
        }

        public override string GetFriendlyName() { return "SeVrSettingsScreen"; }

        public override void RecreateControls(bool constructor)
        {
            base.RecreateControls(constructor);
            var cfg = Plugin.Config;
            origin = m_size.Value * -0.5f + new Vector2(0.045f, 0.105f);
            AddCaption("Space Engineers VR - settings");
            var sep = new MyGuiControlSeparatorList();
            sep.AddHorizontal(new Vector2(m_size.Value.X * -0.5f + 0.03f, m_size.Value.Y * -0.5f + 0.085f), m_size.Value.X - 0.06f, 0.003f, null);
            Controls.Add(sep);

            int row = 0;
            // Enabled
            Label(row, "VR enabled (restart)");
            var enabled = new MyGuiControlCheckbox(Pos(row, 0.30f), null, "Needs a restart of the game.", cfg.Enabled, MyGuiControlCheckboxStyleEnum.Default, MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER);
            enabled.IsCheckedChanged = c => { Plugin.Config.Enabled = c.IsChecked; };
            Controls.Add(enabled); row++;

            Slider(row++, "Render scale (restart)", 0.4f, 1.5f, cfg.RenderScale, 2, false, "Resolution as a fraction of the headset's recommendation.", v => Plugin.Config.RenderScale = v);
            Combo(row++, "OpenXR runtime (restart)", new[] { "default", "steamvr" }, string.IsNullOrWhiteSpace(cfg.Runtime) ? "default" : cfg.Runtime.ToLowerInvariant() == "steamvr" ? "steamvr" : "default", "steamvr = use SteamVR for this game only.", s => Plugin.Config.Runtime = s);
            Combo(row++, "Controller layout", new[] { "auto", "touch", "frame" }, cfg.Controller == "touch" || cfg.Controller == "frame" ? cfg.Controller : "auto", "auto = detect. touch = Quest, Pico, Index (few buttons, body zones). frame = Steam Frame.", s => { Plugin.Config.Controller = s; });
            Label(row, "Body zones");
            Button("Choose zones...", Pos(row, 0.375f), b => { ZonesScreen.Open(); });
            row++;
            Combo(row++, "GPS and signal markers", new[] { "original", "custom" }, cfg.Markers == "custom" ? "custom" : "original", "custom = the plugin's own markers (recommended). original = the game's own sprites, experimental and buggy.", s => { Plugin.Config.Markers = s; });
            Combo(row++, "HUD", new[] { "own", "cut out" }, cfg.OwnHud ? "own" : "cut out", "own = status, tool bar and block info drawn by the plugin from the game's data. cut out = pieces of the game's HUD picture.", s => { Plugin.Config.OwnHud = s == "own"; });
            Combo(row++, "Mod HUDs (BuildInfo...)", new[] { "hide", "float", "game" }, cfg.ModHud, "hide = mod overlays near the camera are blocked completely. float = they hang in front of your head. game = left as the game draws them (artifacts).", s => { Plugin.Config.ModHud = s; });
            Combo(row++, "Hands at the grip", new[] { "on", "off" }, cfg.HandsAtGrip ? "on" : "off", "on = the character's hands sit where your real hands hold the controllers, off = at the front of the controllers.", s => { Plugin.Config.HandsAtGrip = s == "on"; });
            Slider(row++, "Hand rotation (deg)", -180f, 180f, cfg.HandRoll, 0, true, "Turns the character's hands about the pointing direction. 90 = palms face each other. If they turn the wrong way, use -90.", v => { Plugin.Config.HandRoll = v; Plugin.ApplyConfig(); });
            Slider(row++, "Hands back (cm)", -15f, 15f, cfg.HandBack, 0, true, "Pulls the character's hands back along the pointing direction, towards the controller. Negative = forward.", v => { Plugin.Config.HandBack = v; });
            Slider(row++, "Hands down (cm)", -15f, 15f, cfg.HandDown, 1, false, "Moves the character's hands down (negative = up).", v => { Plugin.Config.HandDown = v; });
            Slider(row++, "HUD distance (m)", 0.5f, 2.5f, cfg.HudDistance, 2, false, "How far away the HUD floats. It keeps its apparent size, so farther is easier on the eyes.", v => { Plugin.Config.HudDistance = v; });
            Slider(row++, "HUD opacity", 0.15f, 1f, cfg.HudOpacity, 2, false, "How opaque the HUD panels are. Lower = more see-through.", v => { Plugin.Config.HudOpacity = v; });
            Slider(row++, "HUD sideways (deg)", -30f, 30f, cfg.HudYaw, 1, false, "Turns the HUD and the crosshair sideways. Positive = to the left. Use it if they sit too far to one side.", v => { Plugin.Config.HudYaw = v; });
            Combo(row++, "Guide hands", new[] { "off", "on" }, cfg.GuideHands ? "on" : "off", "on = also draws the simple block hands at the controllers, to compare the character's hands with while tuning Hand lead.", s => { Plugin.Config.GuideHands = s == "on"; });
            Slider(row++, "Turn speed (deg/s)", 30f, 400f, cfg.TurnSpeed, 0, true, "Right stick at full deflection.", v => { Plugin.Config.TurnSpeed = v; Plugin.ApplyConfig(); });
            Slider(row++, "Stick dead zone", 0f, 0.5f, cfg.DeadZone, 2, false, "Raise it if the view or the character drifts.", v => { Plugin.Config.DeadZone = v; Plugin.ApplyConfig(); });
            Slider(row++, "Menu distance (m)", 0.6f, 4f, cfg.MenuDistance, 1, false, "How far away the floating menu screen is.", v => { Plugin.Config.MenuDistance = v; Plugin.ApplyConfig(); });
            Slider(row++, "Menu width (m)", 0.6f, 4f, cfg.MenuWidth, 1, false, "How wide the floating menu screen is.", v => { Plugin.Config.MenuWidth = v; Plugin.ApplyConfig(); });

            string profile = "VR not running";
            try { var xr = Xr.XrRuntime.Instance; if (xr != null) profile = xr.ActiveProfile.Length == 0 ? "not detected yet" : xr.ActiveProfile.Replace("/interaction_profiles/", ""); } catch { }
            Controls.Add(new MyGuiControlLabel(Pos(row, 0f), null, "Controller in use: " + profile, null, 0.8f, "Blue", MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER));
            row++;
            Controls.Add(new MyGuiControlLabel(Pos(row, 0f), null, "Changes apply at once and are saved when you close this screen.", null, 0.7f, "Blue", MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER));

            float by = m_size.Value.Y * 0.5f - 0.06f;
            Button("Key bindings", new Vector2(-0.24f, by), b => { BindingsScreen.Open(); });
            Button("HUD layout", new Vector2(-0.08f, by), b => { HudScreen.Open(); });
            Button("Defaults", new Vector2(0.08f, by), b => { Plugin.Config = new Plugin.Cfg(); Plugin.ApplyConfig(); Vr.VrControls.Rebuild(); RecreateControls(false); });
            Button("Save and close", new Vector2(0.24f, by), b => { CloseScreen(); });
        }

        Vector2 Pos(int row, float x) { return origin + new Vector2(x, row * RowH); }

        void Label(int row, string text)
        {
            Controls.Add(new MyGuiControlLabel(Pos(row, 0f), null, text, null, 0.8f, "Blue", MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER));
        }

        void Slider(int row, string label, float min, float max, float value, int decimals, bool whole, string tip, Action<float> set)
        {
            Label(row, label);
            var s = new MyGuiControlSlider(Pos(row, 0.30f), min, max, 0.26f, Math.Max(min, Math.Min(max, value)), null, "{0}", decimals, 0.7f, 0.05f, "Blue", tip,
                MyGuiControlSliderStyleEnum.Default, MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER, whole, true);
            s.ValueChanged = x => set(x.Value);
            Controls.Add(s);
        }

        void Combo(int row, string label, string[] items, string selected, string tip, Action<string> set)
        {
            Label(row, label);
            var c = new MyGuiControlCombobox(Pos(row, 0.30f), new Vector2(0.20f, 0.04f), null, null, items.Length, null, false, tip, MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER);
            for (int i = 0; i < items.Length; i++) c.AddItem(i, items[i]);
            c.SelectItemByIndex(Math.Max(0, Array.IndexOf(items, selected)));
            c.ItemSelected += () => { int i = c.GetSelectedIndex(); if (i >= 0 && i < items.Length) set(items[i]); };
            Controls.Add(c);
        }

        void Button(string text, Vector2 pos, Action<MyGuiControlButton> click)
        {
            Controls.Add(new MyGuiControlButton(pos, MyGuiControlButtonStyleEnum.Default, new Vector2(0.15f, 0.045f), null, MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER, null, new StringBuilder(text), 0.7f,
                MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER, MyGuiControlHighlightType.WHEN_ACTIVE, click));
        }

        public override bool CloseScreen(bool isUnloading = false)
        {
            try { Plugin.SaveConfig(); Plugin.ApplyConfig(); } catch (Exception e) { Plugin.Log("settings save: " + e.Message); }
            return base.CloseScreen(isUnloading);
        }
    }
}





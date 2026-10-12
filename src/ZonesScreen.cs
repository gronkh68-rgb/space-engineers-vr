using System;
using System.Text;
using Sandbox;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace SeVr
{
    /// <summary>Which body zones are active (a hand near the head, shoulder, chest or hip plus trigger or grip fires an action).</summary>
    internal sealed class ZonesScreen : MyGuiScreenBase
    {
        public static void Open()
        {
            try { MyGuiSandbox.AddScreen(new ZonesScreen()); }
            catch (Exception e) { Plugin.Log("zones screen: " + e); }
        }

        static readonly string[] Keys = { "rhead", "lhead", "shoulder", "lshoulder", "chest", "rhip", "lhip" };
        static readonly string[] Labels =
        {
            "Right head (light, helmet)", "Left head (GPS markers, O)", "Right shoulder", "Left shoulder (G menu)",
            "Chest (inventory)", "Right hip", "Left hip"
        };

        const float RowH = 0.05f;
        Vector2 origin;

        public ZonesScreen()
            : base(new Vector2(0.5f, 0.5f), MyGuiConstants.SCREEN_BACKGROUND_COLOR, new Vector2(0.5f, 0.64f), false, null, MySandboxGame.Config.UIBkOpacity, MySandboxGame.Config.UIOpacity)
        {
            EnabledBackgroundFade = true;
            m_closeOnEsc = true;
            m_drawEvenWithoutFocus = true;
            CanHideOthers = true;
            CanBeHidden = true;
            CloseButtonEnabled = true;
            RecreateControls(true);
        }

        public override string GetFriendlyName() { return "SeVrZonesScreen"; }

        public override void RecreateControls(bool constructor)
        {
            base.RecreateControls(constructor);
            origin = m_size.Value * -0.5f + new Vector2(0.045f, 0.115f);
            AddCaption("Body zones");
            int row = 0;
            Row(row++, "Body zones on", Plugin.Config.Zones != "off", c => { Plugin.Config.Zones = c ? "on" : "off"; });
            string off = "," + (Plugin.Config.ZonesOff ?? "") + ",";
            for (int i = 0; i < Keys.Length; i++)
            {
                string key = Keys[i];
                Row(row++, Labels[i], off.IndexOf("," + key + ",", StringComparison.Ordinal) < 0, c => Set(key, c));
            }
            Controls.Add(new MyGuiControlLabel(origin + new Vector2(0f, (row + 0.2f) * RowH), null, "Hand in a zone + trigger or grip = the zone's action.", null, 0.7f, "Blue", MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER));
            Controls.Add(new MyGuiControlButton(new Vector2(0f, m_size.Value.Y * 0.5f - 0.05f), MyGuiControlButtonStyleEnum.Default, new Vector2(0.15f, 0.045f), null, MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER, null, new StringBuilder("Close"), 0.7f,
                MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER, MyGuiControlHighlightType.WHEN_ACTIVE, b => { CloseScreen(); }));
        }

        void Row(int row, string text, bool value, Action<bool> set)
        {
            var p = origin + new Vector2(0f, row * RowH);
            Controls.Add(new MyGuiControlLabel(p, null, text, null, 0.8f, "Blue", MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER));
            var cb = new MyGuiControlCheckbox(p + new Vector2(0.33f, 0f), null, null, value, MyGuiControlCheckboxStyleEnum.Default, MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER);
            cb.IsCheckedChanged = c => set(c.IsChecked);
            Controls.Add(cb);
        }

        static void Set(string key, bool on)
        {
            var list = new System.Collections.Generic.List<string>((Plugin.Config.ZonesOff ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            list.Remove(key);
            if (!on) list.Add(key);
            Plugin.Config.ZonesOff = string.Join(",", list.ToArray());
        }

        public override bool CloseScreen(bool isUnloading = false)
        {
            try { Plugin.SaveConfig(); } catch (Exception e) { Plugin.Log("zones save: " + e.Message); }
            return base.CloseScreen(isUnloading);
        }
    }
}

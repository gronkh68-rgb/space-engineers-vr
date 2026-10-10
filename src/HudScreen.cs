using System;
using System.Text;
using Sandbox;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace SeVr
{
    /// <summary>
    /// HUD layout: where each part of the game's HUD floats. Instead of one flat window in front of the face, the parts sit on the body:
    /// next to a hand, at the belt or on the chest. "hud" leaves a part in the flat window. Changes apply at once.
    /// </summary>
    internal sealed class HudScreen : MyGuiScreenBase
    {
        public static void Open()
        {
            try { MyGuiSandbox.AddScreen(new HudScreen()); }
            catch (Exception e) { Plugin.Log("hud screen: " + e); }
        }

        const float RowH = 0.058f;
        Vector2 origin;

        static readonly string[] Names = { "Block info: name and text", "Block info: components", "Hotbar", "Status bars", "Gravity and warnings", "Chat" };
        static readonly string[] Tips =
        {
            "Icon, name, description and PCU of the selected or aimed block.",
            "Required components and integrity of the block.",
            "All the rest of the HUD (tool bar, status bars, warnings, parts of HUD mods): every piece found in the picture is shown on its own at this place.", "Not used: follows the setting above.",
            "Not used: follows the setting above.", "Chat messages."
        };
        static readonly string[] Anchors = { "bottom", "bottom2", "lhand", "rhand", "belt", "chestl", "chestr", "hud" };
        static readonly string[] AnchorLabels = { "bottom row", "bottom upper row", "left hand", "right hand", "belt", "chest left", "chest right", "flat HUD" };

        public HudScreen()
            : base(new Vector2(0.5f, 0.5f), MyGuiConstants.SCREEN_BACKGROUND_COLOR, new Vector2(0.66f, 0.78f), false, null, MySandboxGame.Config.UIBkOpacity, MySandboxGame.Config.UIOpacity)
        {
            EnabledBackgroundFade = true;
            m_closeOnEsc = true;
            m_drawEvenWithoutFocus = true;
            CanHideOthers = true;
            CanBeHidden = true;
            CloseButtonEnabled = true;
            RecreateControls(true);
        }

        public override string GetFriendlyName() { return "SeVrHudScreen"; }

        public override void RecreateControls(bool constructor)
        {
            base.RecreateControls(constructor);
            origin = m_size.Value * -0.5f + new Vector2(0.045f, 0.125f);
            AddCaption("HUD layout");
            var sep = new MyGuiControlSeparatorList();
            sep.AddHorizontal(new Vector2(m_size.Value.X * -0.5f + 0.03f, m_size.Value.Y * -0.5f + 0.085f), m_size.Value.X - 0.06f, 0.003f, null);
            Controls.Add(sep);

            for (int i = 0; i < Names.Length; i++) AddPart(i);

            var s = new MyGuiControlSlider(origin + new Vector2(0.30f, Names.Length * RowH), 0.12f, 0.6f, 0.34f, Math.Max(0.12f, Math.Min(0.6f, Plugin.Config.PanelSize)), null, "{0}", 2, 0.7f, 0.05f, "Blue", "Width of the block info panels in metres. The other parts scale with it.",
                MyGuiControlSliderStyleEnum.Default, MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER, false, true);
            s.ValueChanged = x => { Plugin.Config.PanelSize = x.Value; };
            Controls.Add(new MyGuiControlLabel(origin + new Vector2(0f, Names.Length * RowH), null, "Size of the parts (m)", null, 0.8f, "Blue", MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER));
            Controls.Add(s);

            Controls.Add(new MyGuiControlLabel(origin + new Vector2(0f, (Names.Length + 1.2f) * RowH), null, "Hand parts stack above each other when they share a hand.", null, 0.7f, "Blue", MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER));

            float by = m_size.Value.Y * 0.5f - 0.06f;
            Controls.Add(new MyGuiControlButton(new Vector2(-0.12f, by), MyGuiControlButtonStyleEnum.Default, new Vector2(0.2f, 0.045f), null, MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER, null, new StringBuilder("Defaults"), 0.75f,
                MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER, MyGuiControlHighlightType.WHEN_ACTIVE, b => { var d = new Plugin.Cfg(); Array.Copy(d.Anchors, Plugin.Config.Anchors, d.Anchors.Length); Plugin.Config.PanelSize = d.PanelSize; RecreateControls(false); }));
            Controls.Add(new MyGuiControlButton(new Vector2(0.12f, by), MyGuiControlButtonStyleEnum.Default, new Vector2(0.2f, 0.045f), null, MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER, null, new StringBuilder("Close"), 0.75f,
                MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER, MyGuiControlHighlightType.WHEN_ACTIVE, b => { CloseScreen(); }));
        }

        void AddPart(int part)
        {
            Controls.Add(new MyGuiControlLabel(origin + new Vector2(0f, part * RowH), null, Names[part], null, 0.8f, "Blue", MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER));
            var c = new MyGuiControlCombobox(origin + new Vector2(0.30f, part * RowH), new Vector2(0.26f, 0.04f), null, null, 6, null, false, Tips[part], MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER);
            for (int i = 0; i < Anchors.Length; i++) c.AddItem(i, AnchorLabels[i]);
            c.SelectItemByIndex(Math.Max(0, Array.IndexOf(Anchors, Plugin.Config.Anchors[part])));
            c.ItemSelected += () => { int i = c.GetSelectedIndex(); if (i >= 0 && i < Anchors.Length) Plugin.Config.Anchors[part] = Anchors[i]; };
            Controls.Add(c);
        }

        public override bool CloseScreen(bool isUnloading = false)
        {
            try { Plugin.SaveConfig(); } catch (Exception e) { Plugin.Log("hud save: " + e.Message); }
            return base.CloseScreen(isUnloading);
        }
    }
}

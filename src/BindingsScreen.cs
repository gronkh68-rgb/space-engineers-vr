using System;
using System.Collections.Generic;
using System.Text;
using Sandbox;
using Sandbox.Game;
using Sandbox.Graphics.GUI;
using SeVr.Generated;
using SeVr.Vr;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace SeVr
{
    /// <summary>
    /// Key bindings: which game action each VR input triggers (body zones, X/Y taps and holds, stick click, menu button).
    /// A choice is stored as Bind.&lt;input&gt;=&lt;game control&gt; in SeVr.cfg and takes effect at once.
    /// </summary>
    internal sealed class BindingsScreen : MyGuiScreenBase
    {
        public static void Open()
        {
            try { MyGuiSandbox.AddScreen(new BindingsScreen()); }
            catch (Exception e) { Plugin.Log("bindings screen: " + e); }
        }

        // VR input (source name in the control table), label
        static readonly string[][] Inputs =
        {
            new[] { "z_rhead_t", "Head right + trigger" }, new[] { "z_rhead_g", "Head right + grip" },
            new[] { "z_lhead_t", "Head left + trigger" }, new[] { "z_lhead_g", "Head left + grip" },
            new[] { "z_shoulder_t", "Shoulder + trigger" }, new[] { "z_shoulder_g", "Shoulder + grip" },
            new[] { "z_lshoulder_t", "Left shoulder + trigger" }, new[] { "z_lshoulder_g", "Left shoulder + grip" },
            new[] { "z_chest_t", "Chest + trigger" }, new[] { "z_chest_g", "Chest + grip" },
            new[] { "z_rhip_g", "Right hip + grip" }, new[] { "z_lhip_g", "Left hip + grip" },
            new[] { "x_tap", "X button, tap" }, new[] { "x_hold", "X button, hold" },
            new[] { "y_tap", "Y button, tap" }, new[] { "y_hold", "Y button, hold" },
            new[] { "jetpack", "Right stick click" }, new[] { "menu", "Menu button" },
            new[] { "fy", "Frame: Y button" }, new[] { "view", "Frame: view button" },
        };

        // game control, label (names that do not exist in this game version are left out)
        static readonly string[][] Actions =
        {
            new[] { "HEADLIGHTS", "Flashlight" }, new[] { "HELMET", "Helmet" }, new[] { "INVENTORY", "Inventory" }, new[] { "TERMINAL", "Terminal" },
            new[] { "DAMPING", "Inertial dampeners" }, new[] { "THRUSTS", "Jetpack" }, new[] { "LANDING_GEAR", "Landing gear" },
            new[] { "BROADCASTING", "Broadcasting (O)" }, new[] { "TOGGLE_SIGNALS", "GPS markers on/off" }, new[] { "BUILD_SCREEN", "Block menu (G)" }, new[] { "USE", "Use" },
            new[] { "PICK_UP", "Pick up" }, new[] { "TOOLBAR_NEXT_ITEM", "Next hotbar slot" }, new[] { "TOOLBAR_PREV_ITEM", "Previous hotbar slot" },
            new[] { "TOOLBAR_UP", "Hotbar page up" }, new[] { "TOOLBAR_DOWN", "Hotbar page down" }, new[] { "TOOLBAR_RADIAL_MENU", "Radial menu" },
            new[] { "SLOT0", "Hands free (slot 0)" }, new[] { "MAIN_MENU", "Game menu" }, new[] { "CHAT_SCREEN", "Chat" }, new[] { "TOGGLE_HUD", "Hide HUD" },
            new[] { "COLOR_PICKER", "Colour menu" }, new[] { "BUILD_PLANNER", "Build planner (MMB)" }, new[] { "CAMERA_MODE", "First/third person (V)" },
        };

        const int PerPage = 8;
        const float RowH = 0.062f;
        static int page;
        Vector2 origin;
        readonly List<string[]> actions = new List<string[]>();

        public BindingsScreen()
            : base(new Vector2(0.5f, 0.5f), MyGuiConstants.SCREEN_BACKGROUND_COLOR, new Vector2(0.66f, 0.84f), false, null, MySandboxGame.Config.UIBkOpacity, MySandboxGame.Config.UIOpacity)
        {
            EnabledBackgroundFade = true;
            m_closeOnEsc = true;
            m_drawEvenWithoutFocus = true;
            CanHideOthers = true;
            CanBeHidden = true;
            CloseButtonEnabled = true;
            foreach (var a in Actions) if (typeof(MyControlsSpace).GetField(a[0]) != null) actions.Add(a);
            RecreateControls(true);
        }

        public override string GetFriendlyName() { return "SeVrBindingsScreen"; }

        static string DefaultControl(string source)
        {
            foreach (var r in ControlMap.Rows) if (r.Source == source && !r.GameControl.StartsWith("KEY:")) return r.GameControl;
            return null;
        }

        static string Current(string source)
        {
            string b;
            if (Plugin.Config.Binds.TryGetValue(source, out b)) return b;
            return DefaultControl(source);
        }

        int Pages { get { return (Inputs.Length + PerPage - 1) / PerPage; } }

        public override void RecreateControls(bool constructor)
        {
            base.RecreateControls(constructor);
            origin = m_size.Value * -0.5f + new Vector2(0.045f, 0.125f);
            AddCaption("Key bindings");
            var sep = new MyGuiControlSeparatorList();
            sep.AddHorizontal(new Vector2(m_size.Value.X * -0.5f + 0.03f, m_size.Value.Y * -0.5f + 0.085f), m_size.Value.X - 0.06f, 0.003f, null);
            Controls.Add(sep);

            if (page >= Pages) page = 0;
            for (int i = 0; i < PerPage; i++)
            {
                int idx = page * PerPage + i;
                if (idx >= Inputs.Length) break;
                AddRow(i, Inputs[idx][0], Inputs[idx][1]);
            }

            Controls.Add(new MyGuiControlLabel(new Vector2(0f, origin.Y + PerPage * RowH + 0.01f), null, "Page " + (page + 1) + " / " + Pages + "   (changes apply at once)", null, 0.75f, "Blue", MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER));

            float by = m_size.Value.Y * 0.5f - 0.06f;
            Button("< Back", new Vector2(-0.2f, by), 0.17f, b => { page = (page + Pages - 1) % Pages; RecreateControls(false); });
            Button("Next >", new Vector2(0.0f, by), 0.17f, b => { page = (page + 1) % Pages; RecreateControls(false); });
            Button("Reset all", new Vector2(0.2f, by), 0.17f, b => { Plugin.Config.Binds.Clear(); VrControls.Rebuild(); RecreateControls(false); });
            Button("Close", new Vector2(0.0f, by - 0.058f), 0.17f, b => { CloseScreen(); });
        }

        void AddRow(int row, string source, string label)
        {
            var pos = origin + new Vector2(0f, row * RowH);
            Controls.Add(new MyGuiControlLabel(pos, null, label, null, 0.8f, "Blue", MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER));

            string def = DefaultControl(source);
            string cur = Current(source);
            var list = new List<string[]>(actions);
            if (def != null && list.Find(a => a[0] == def) == null) list.Add(new[] { def, def });
            if (cur != null && !string.Equals(cur, "none", StringComparison.OrdinalIgnoreCase) && list.Find(a => a[0] == cur) == null) list.Add(new[] { cur, cur });
            list.Add(new[] { "none", "(nothing)" });

            var c = new MyGuiControlCombobox(origin + new Vector2(0.29f, row * RowH), new Vector2(0.27f, 0.04f), null, null, 8, null, false, null, MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER);
            int sel = 0;
            for (int i = 0; i < list.Count; i++)
            {
                c.AddItem(i, list[i][1]);
                if (cur != null && string.Equals(list[i][0], cur, StringComparison.OrdinalIgnoreCase)) sel = i;
            }
            c.SelectItemByIndex(sel);
            c.ItemSelected += () =>
            {
                int i = c.GetSelectedIndex();
                if (i < 0 || i >= list.Count) return;
                string choice = list[i][0];
                if (choice == def) Plugin.Config.Binds.Remove(source); else Plugin.Config.Binds[source] = choice;
                VrControls.Rebuild();
            };
            Controls.Add(c);
        }

        void Button(string text, Vector2 pos, float width, Action<MyGuiControlButton> click)
        {
            Controls.Add(new MyGuiControlButton(pos, MyGuiControlButtonStyleEnum.Default, new Vector2(width, 0.045f), null, MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER, null, new StringBuilder(text), 0.8f,
                MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER, MyGuiControlHighlightType.WHEN_ACTIVE, click));
        }

        public override bool CloseScreen(bool isUnloading = false)
        {
            try { Plugin.SaveConfig(); } catch (Exception e) { Plugin.Log("bindings save: " + e.Message); }
            return base.CloseScreen(isUnloading);
        }
    }
}



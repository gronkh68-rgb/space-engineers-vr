using System;
using System.Collections.Generic;
using Sandbox.Game.Gui;
using Sandbox.Game.GUI;
using Sandbox.Game.Screens.Helpers;
using SharpDX.Direct3D11;

namespace SeVr.Vr
{
    /// <summary>
    /// The HUD drawn by the plugin itself from the game's data (stats, tool bar, block info) instead of cutting pieces out of the game's HUD picture.
    /// It does not depend on how a HUD mod lays things out. The game's own icons are used; the frames and text are drawn here.
    /// Parts: 3 status bars, 2 tool bar, 0 block info, 1 component list. Each is drawn into its own panel image.
    /// </summary>
    internal static class VrOwnHud
    {
        // look of the game's HUD: dark glass, blue-grey lines, pale blue text
        const int BgR = 10, BgG = 20, BgB = 30, BgA = 205;
        const int LnR = 74, LnG = 118, LnB = 160;
        const int TxR = 214, TxG = 232, TxB = 248;

        sealed class Row { public string Label; public float Ratio; public string Value; public int R, G, B; }

        static readonly List<Row> rows = new List<Row>();
        static int slotCount;
        static MyToolbar toolbar;

        static void AddStat<T>(string label, int r, int g, int b, bool percent, string unit = null) where T : class, VRage.ModAPI.IMyHudStat, new()
        {
            try
            {
                var s = MyHud.Stats.GetStat<T>();
                if (s == null) return;
                float max = s.MaxValue, min = s.MinValue, cur = s.CurrentValue;
                if (max <= min) return;
                var row = new Row { Label = label, Ratio = Math.Max(0f, Math.Min(1f, (cur - min) / (max - min))), R = r, G = g, B = b };
                row.Value = percent ? (int)Math.Round(row.Ratio * 100f) + " %" : ((int)Math.Round(cur)) + (unit ?? "");
                rows.Add(row);
            }
            catch { }
        }

        /// <summary>Size in pixels of a part's panel; false when there is nothing to show for it right now.</summary>
        static bool sample0, sample1;       // edit mode: block info and components shown as examples when no block is aimed at

        public static bool Prepare(int part, out int w, out int h)
        {
            w = h = 0;
            try
            {
                switch (part)
                {
                    case 3:
                        rows.Clear();
                        AddStat<MyStatPlayerHealth>("Health", 232, 78, 66, true);
                        AddStat<MyStatPlayerEnergy>("Energy", 250, 204, 70, true);
                        AddStat<MyStatPlayerOxygen>("Oxygen", 100, 196, 242, true);
                        AddStat<MyStatPlayerHydrogen>("Hydrogen", 120, 166, 255, true);
                        try
                        {
                            var sp = MyHud.Stats.GetStat<MyStatControlledEntitySpeed>();
                            if (sp != null && sp.CurrentValue > 0.5f) rows.Add(new Row { Label = "Speed", Ratio = -1f, Value = sp.CurrentValue.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " m/s", R = TxR, G = TxG, B = TxB });
                        }
                        catch { }
                        AddAmmo();
                        if (rows.Count == 0) return false;
                        w = 440; h = 24 + rows.Count * 46;
                        return true;
                    case 16:
                        if (Plugin.Config.HudMode != "hand") return false;
                        w = 240; h = 472;
                        return true;
                    case 15:
                        if (!Plugin.Config.Help) return false;
                        w = 1200; h = 376;
                        return true;
                    case 18:
                        if (Plugin.Config.HudMode != "hand") return false;
                        w = 112; h = 352;
                        return true;
                    case 11: w = 560; h = 112; return EditMode;
                    case 13: w = GMenuOpen ? 960 : 480; h = 100; return true;
                    case 14: w = 560; h = 96; return true;
                    case 5: return PrepareChat(out w, out h);
                    case 7: return PrepareNotes(out w, out h);
                    case 4:
                        BuildIndicators();
                        w = 440; h = 24 + 2 * 50 + 8 + lines.Count * 40;
                        return true;
                    case 22:
                        if (VrHead.SeatKey == null) return false;
                        goto case 2;
                    case 23:
                        w = 400; h = 120; return VrHead.SeatKey != null;
                    case 2:
                        toolbar = MyToolbarComponent.CurrentToolbar;
                        if (toolbar == null) return false;
                        slotCount = Math.Min(9, toolbar.SlotCount);
                        if (slotCount <= 0) return false;
                        w = 24 + (slotCount + 1) * 104; h = 196;           // the slots and the empty-hand slot (0)
                        return true;
                    case 0:
                    {
                        var bi = MyHud.BlockInfo;
                        sample0 = false;
                        if (bi == null || !bi.Visible || string.IsNullOrEmpty(bi.BlockName))
                        {
                            if (!EditMode) return false;
                            sample0 = true; w = 600; h = 148;           // in edit mode an example, so the part can be moved without aiming at a block
                            return true;
                        }
                        w = 600; h = IsWeaponBlock(bi) ? 104 : 148;
                        return true;
                    }
                    case 1:
                    {
                        var bi = MyHud.BlockInfo;
                        sample1 = false;
                        if (bi == null || !bi.Visible || bi.Components == null || bi.Components.Count == 0)
                        {
                            if (!EditMode) return false;
                            sample1 = true; w = 600; h = 28 + 3 * 52;
                            return true;
                        }
                        int n = Math.Min(6, bi.Components.Count);
                        w = 600; h = 28 + n * 52;
                        return true;
                    }
                }
            }
            catch (Exception e) { Plugin.Log("own hud prepare: " + e.Message); }
            return false;
        }

        // ---- chat and notifications

        sealed class ChatLine { public string Sender, Text; public VRageMath.Color Color; }
        sealed class NoteLine { public string Text; public string[] Icons; public int R, G, B; }
        static readonly List<ChatLine> chat = new List<ChatLine>();
        static readonly List<NoteLine> notes = new List<NoteLine>();
        static System.Reflection.FieldInfo toDrawF, ddNotif, ddText, ddClear;

        sealed class VisLine { public string Sender; public VRageMath.Color Color; public string Text; public float SenderW; }
        static readonly List<VisLine> chatVis = new List<VisLine>();
        const int ChatPx = 24, ChatLineH = 30, ChatMaxLines = 10;

        /// <summary>
        /// The chat like the game's: recent messages, long ones wrapped, the newest at the bottom; when new lines come in the old ones move up and
        /// the oldest drop off. The panel is as wide and as tall as its text.
        /// </summary>
        static bool PrepareChat(out int w, out int h)
        {
            w = h = 0;
            chatVis.Clear();
            var q = MyHud.Chat != null ? MyHud.Chat.Messages : null;
            if (q == null) return false;
            var now = DateTime.Now; var utc = DateTime.UtcNow;
            var items = new List<MyChatItem>(q);
            const float maxText = 600f;
            for (int i = Math.Max(0, items.Count - 12); i < items.Count; i++)
            {
                var m = items[i];
                double age = Math.Min(Math.Abs((now - m.Timestamp).TotalSeconds), Math.Abs((utc - m.Timestamp).TotalSeconds));
                if (age > 30) continue;
                string who = string.IsNullOrEmpty(m.Sender) ? "" : m.Sender + ":";
                float whoW = who.Length > 0 ? VrOverlay.TextWidth(who, ChatPx) + 10 : 0f;
                var words = (m.Message ?? "").Replace("\r", " ").Replace("\n", " ").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                string cur = "";
                bool first = true;
                Action flush = () =>
                {
                    chatVis.Add(new VisLine { Sender = first ? who : "", Color = m.SenderColor, Text = cur, SenderW = first ? whoW : 0f });
                    first = false; cur = "";
                };
                foreach (var word in words)
                {
                    string trial = cur.Length == 0 ? word : cur + " " + word;
                    float avail = maxText - (first ? whoW : 0f);
                    if (cur.Length > 0 && VrOverlay.TextWidth(trial, ChatPx) > avail) { flush(); trial = word; }
                    cur = trial;
                }
                if (cur.Length > 0 || first) flush();
            }
            if (chatVis.Count == 0)
            {
                if (!EditMode) return false;
                chatVis.Add(new VisLine { Sender = "Chat:", Color = new VRageMath.Color(140, 220, 150), Text = "messages appear here", SenderW = VrOverlay.TextWidth("Chat:", ChatPx) + 10 });
            }
            if (chatVis.Count > ChatMaxLines) chatVis.RemoveRange(0, chatVis.Count - ChatMaxLines);       // the oldest lines scroll off the top
            float widest = 0f;
            foreach (var l in chatVis) widest = Math.Max(widest, l.SenderW + VrOverlay.TextWidth(l.Text, ChatPx));
            w = (int)Math.Max(240f, Math.Min(660f, widest + 40f));
            h = 16 + chatVis.Count * ChatLineH;
            return true;
        }

        static bool PrepareNotes(out int w, out int h)
        {
            w = h = 0;
            notes.Clear();
            try
            {
                if (toDrawF == null) toDrawF = typeof(MyHudNotifications).GetField("m_toDraw", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var list = toDrawF.GetValue(MyHud.Notifications) as System.Collections.IList;
                if (list != null)
                {
                    for (int i = 0; i < list.Count && notes.Count < 6; i++)
                    {
                        object d = list[i];
                        if (d == null) continue;
                        if (ddText == null)
                        {
                            var dt = d.GetType();
                            ddText = dt.GetField("Text"); ddNotif = dt.GetField("Notification"); ddClear = null;
                        }
                        var sbText = ddText.GetValue(d) as System.Text.StringBuilder;
                        string text = sbText != null ? sbText.ToString().Trim() : null;
                        if (string.IsNullOrEmpty(text)) continue;
                        var nl = new NoteLine { Text = text.Replace("\r", " ").Replace("\n", "  "), R = TxR, G = TxG, B = TxB };
                        try
                        {
                            var nb = ddNotif.GetValue(d) as MyHudNotificationBase;
                            if (nb != null && nb.Level.ToString().IndexOf("Important", StringComparison.OrdinalIgnoreCase) >= 0) { nl.R = 250; nl.G = 200; nl.B = 90; }
                        }
                        catch { }
                        notes.Add(nl);
                    }
                }
            }
            catch { }
            try
            {
                var ci = MyHud.ChangedInventoryItems;
                if (ci != null && ci.Visible)
                    foreach (var it in ci.Items)
                    {
                        if (notes.Count >= 8) break;
                        string name = it.DefinitionId.SubtypeName;
                        try { var def = Sandbox.Definitions.MyDefinitionManager.Static.GetDefinition(it.DefinitionId); if (def != null) name = def.DisplayNameText; } catch { }
                        notes.Add(new NoteLine { Text = (it.Added ? "+ " : "- ") + it.ChangedAmount + "  " + name, Icons = it.Icons, R = it.Added ? 140 : 240, G = it.Added ? 225 : 150, B = it.Added ? 150 : 120 });
                    }
            }
            catch { }
            if (notes.Count == 0)
            {
                if (!EditMode) return false;
                notes.Add(new NoteLine { Text = "Notifications appear here", R = TxR, G = TxG, B = TxB });
            }
            w = 600; h = 20 + notes.Count * 44;
            return true;
        }

        static string Fit(string s, int px, float maxW)
        {
            if (VrOverlay.TextWidth(s, px) <= maxW) return s;
            while (s.Length > 4 && VrOverlay.TextWidth(s + "...", px) > maxW) s = s.Substring(0, s.Length - 1);
            return s + "...";
        }

        static void DrawChat(int w, int h)
        {
            float y = 8;
            foreach (var l in chatVis)
            {
                if (l.Sender.Length > 0) VrOverlay.DrawText(l.Sender, 18, y + 2, ChatPx, l.Color.R, l.Color.G, l.Color.B, 255);
                VrOverlay.DrawText(l.Text, 18 + l.SenderW, y + 2, ChatPx, TxR, TxG, TxB, 255);
                y += ChatLineH;
            }
        }

        static void DrawNotes(int w, int h)
        {
            float y = 10;
            foreach (var n in notes)
            {
                float x = 20;
                if (n.Icons != null) { foreach (var p in n.Icons) VrOverlay.DrawIcon(p, 16, y + 2, 38, 38, 255); x = 66; }
                VrOverlay.DrawText(Fit(n.Text, 26, w - x - 20), x, y + 6, 26, n.R, n.G, n.B, 255);
                y += 44;
            }
        }

        sealed class Chip { public string Label; public bool On; }
        sealed class Line { public string Label, Value; public int R, G, B; }
        static readonly List<Chip> chips = new List<Chip>();
        static readonly List<Line> lines = new List<Line>();

        static bool IsWeaponBlock(MyHudBlockInfo bi)
        {
            try
            {
                string t = bi.DefinitionId.TypeId.ToString();
                return t.IndexOf("Turret", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("Gun", StringComparison.OrdinalIgnoreCase) >= 0
                    || t.IndexOf("Launcher", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("Weapon", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        /// <summary>The hand weapon's magazine and spare magazines as a row of the status panel.</summary>
        static void AddAmmo()
        {
            try
            {
                var ch = Sandbox.Game.World.MySession.Static != null ? Sandbox.Game.World.MySession.Static.LocalCharacter : null;
                var wp = ch != null ? ch.CurrentWeapon : null;
                if (wp == null || !wp.Reloadable) return;
                rows.Add(new Row
                {
                    Label = wp.IsReloading ? "Reloading" : "Ammo", Ratio = -1f, R = TxR, G = TxG, B = TxB,
                    Value = wp.CurrentMagazineAmmunition + "  +  " + wp.CurrentMagazineAmount + " mags"
                });
            }
            catch { }
        }

        static float Stat(Func<float> get) { try { return get(); } catch { return 0f; } }

        static void BuildIndicators()
        {
            chips.Clear(); lines.Clear();
            var st = MyHud.Stats;
            Func<Func<float>, bool> on = g => Stat(g) > 0.5f;
            chips.Add(new Chip { Label = "Dampeners", On = on(() => st.GetStat<MyStatControlledEntityDampeners>().CurrentValue) });
            chips.Add(new Chip { Label = "Light", On = on(() => st.GetStat<MyStatPlayerFlashlight>().CurrentValue) });
            chips.Add(new Chip { Label = "Antenna", On = on(() => st.GetStat<MyStatPlayerBroadcasting>().CurrentValue) || on(() => st.GetStat<MyStatControlledEntityBroadcasting>().CurrentValue) });
            // the helmet says whether it is closed (the stat is 1 while the helmet is on)
            bool helmetOn = on(() => st.GetStat<MyStatPlayerHelmet>().CurrentValue);
            chips.Add(new Chip { Label = helmetOn ? "Helmet closed" : "Helmet open", On = helmetOn });
            chips.Add(new Chip { Label = "Jetpack", On = on(() => st.GetStat<MyStatPlayerJetpack>().CurrentValue) });
            chips.Add(new Chip { Label = "Mag boots", On = on(() => st.GetStat<MyStatPlayerMagBoots>().CurrentValue) });

            float nat = Stat(() => st.GetStat<MyStatNaturalGravity>().CurrentValue), art = Stat(() => st.GetStat<MyStatArtificialGravity>().CurrentValue);
            float g = Math.Max(nat, art);
            lines.Add(new Line { Label = "Gravity", Value = g.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " g" + (art > nat ? "  (artificial)" : nat > 0.001f ? "  (planet)" : ""), R = TxR, G = TxG, B = TxB });

            // the stat holds the outside temperature itself; the game turns it into a level (extreme cold ... extreme heat) with the weather component
            try
            {
                float temp = Stat(() => st.GetStat<MyStatEnvironmentTemperatureLevel>().CurrentValue);
                var mi = typeof(Sandbox.Game.SessionComponents.MySectorWeatherComponent).GetMethod("TemperatureToLevel", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static);
                object target = mi.IsStatic ? null : Sandbox.Game.World.MySession.Static.GetComponent<Sandbox.Game.SessionComponents.MySectorWeatherComponent>();
                int lvl = (int)(VRage.Game.MyTemperatureLevel)mi.Invoke(target, new object[] { temp });
                string[] tn = { "Extreme cold", "Cold", "Comfortable", "Hot", "Extreme heat" };
                int[][] tc = { new[] { 120, 170, 255 }, new[] { 120, 200, 255 }, new[] { 140, 220, 150 }, new[] { 250, 190, 90 }, new[] { 240, 100, 80 } };
                if (lvl >= 0 && lvl < tn.Length) lines.Add(new Line { Label = "Temperature", Value = tn[lvl], R = tc[lvl][0], G = tc[lvl][1], B = tc[lvl][2] });
            }
            catch { }

            float o2 = Stat(() => st.GetStat<MyStatEnvironmentOxygenLevel>().CurrentValue);
            lines.Add(new Line { Label = "Oxygen outside", Value = (int)Math.Round(Math.Max(0f, Math.Min(1f, o2)) * 100f) + " %", R = o2 < 0.2f ? 240 : TxR, G = o2 < 0.2f ? 120 : TxG, B = o2 < 0.2f ? 100 : TxB });

            float rad = Stat(() => st.GetStat<MyStatPlayerRadiation>().CurrentValue);
            if (rad > 0.001f) lines.Add(new Line { Label = "Radiation", Value = rad.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), R = 240, G = 190, B = 80 });
            try
            {
                string hb = st.GetStat<MyStatPlayerHydrogenBottles>().GetValueString(), ob = st.GetStat<MyStatPlayerOxygenBottles>().GetValueString();
                if (!string.IsNullOrEmpty(hb) || !string.IsNullOrEmpty(ob)) lines.Add(new Line { Label = "Bottles H2 / O2", Value = (hb ?? "-") + " / " + (ob ?? "-"), R = TxR, G = TxG, B = TxB });
            }
            catch { }
        }

        static void DrawIndicators(int w, int h)
        {
            float cw = (w - 40 - 2 * 8) / 3f;
            for (int i = 0; i < chips.Count; i++)
            {
                float x = 20 + (i % 3) * (cw + 8), y = 14 + (i / 3) * 50;
                var c = chips[i];
                VrOverlay.FillRect(x, y, cw, 42, c.On ? 30 : 18, c.On ? 72 : 28, c.On ? 50 : 38, 255);
                VrOverlay.FrameRect(x, y, cw, 42, 2, c.On ? 110 : 60, c.On ? 214 : 84, c.On ? 140 : 104, 255);
                VrOverlay.FillRect(x + 10, y + 15, 12, 12, c.On ? 120 : 70, c.On ? 230 : 90, c.On ? 150 : 100, 255);
                int lpx = 18;
                if (VrOverlay.TextWidth(c.Label, lpx) > cw - 32) lpx = 15;         // a long label gets a smaller font
                VrOverlay.DrawText(c.Label, x + 27, y + 11 + (18 - lpx) / 2, lpx, c.On ? TxR : 130, c.On ? TxG : 150, c.On ? TxB : 170, 255);
            }
            float ly = 14 + 2 * 50 + 8;
            foreach (var l in lines)
            {
                VrOverlay.DrawText(l.Label, 22, ly + 4, 26, 170, 200, 228, 255);
                VrOverlay.DrawText(l.Value, w - 20, ly + 4, 26, l.R, l.G, l.B, 255, 2);
                ly += 40;
            }
        }

        // the hand menu: opened and closed with the small button on the hand
        public static volatile bool MenuOpen = Environment.GetEnvironmentVariable("SEVR_MENUOPEN") == "1";     // test switch: start with the menu open
        public static volatile bool ButtonHover;

        // edit mode: the hand menu's buttons stay, every HUD part can be picked up with the right grip and moved (right stick up / down: size)
        public static volatile bool EditMode;
        public static int HoverButton = -1;         // which of the buttons 8 (menu), 9 (edit), 10 (reset) the right hand points at

        // The buttons on the back of the hands. Ids (also the hover ids): 8 menu, 9 edit, 10 reset, 21 chat, 15 K, 16 G, 17 I (left hand); 18 F10, 19 Alt+F10, 20 Shift+F10 (right hand).
        // Each cell is 112 x 112 pixels, 128 apart in the cluster's image.
        static void Btn(int id, int x, int y)
        {
            const int w = 112, h = 112;
            bool hov = HoverButton == id;
            bool on = id == 8 ? MenuOpen : id == 9 ? EditMode : false;
            bool edit = id == 9;
            int r = on ? (edit ? 90 : 30) : 14, g = on ? (edit ? 60 : 90) : 30, b = on ? (edit ? 20 : 60) : 44;
            VrOverlay.FillRect(x, y, w, h, r, g, b, 235);
            VrOverlay.FrameRect(x, y, w, h, hov ? 8 : 5, hov ? 255 : edit ? 255 : 150, hov ? 255 : edit ? 170 : 210, hov ? 255 : edit ? 60 : 240, 255);
            if (id == 8)
                for (int i = 0; i < 3; i++) VrOverlay.FillRect(x + 26, y + 30 + i * 22, w - 52, 9, on ? 150 : 214, on ? 255 : 232, on ? 190 : 248, 255);       // the three lines of a menu
            else if (id == 9) VrOverlay.DrawText("Edit", x + w / 2, y + 34, 40, 255, on ? 210 : 190, on ? 120 : 90, 255, 1);
            else if (id == 10) VrOverlay.DrawText("Reset", x + w / 2, y + 38, 32, 255, 130, 110, 255, 1);
            else if (id == 22) VrOverlay.DrawText("?", x + w / 2, y + 22, 64, Plugin.Config.Help ? 255 : 230, Plugin.Config.Help ? 220 : 240, Plugin.Config.Help ? 120 : 255, 255, 1);
            else if (id == 21) VrOverlay.DrawText("Chat", x + w / 2, y + 34, 36, 230, 240, 255, 255, 1);
            else if (id >= 15 && id <= 17) VrOverlay.DrawText(id == 15 ? "K" : id == 16 ? "G" : "I", x + w / 2, y + 22, 64, 230, 240, 255, 255, 1);       // keys: terminal, G menu, inventory
            else if (id == 18) VrOverlay.DrawText("F10", x + w / 2, y + 34, 40, 255, 220, 140, 255, 1);
            else
            {
                VrOverlay.DrawText(id == 19 ? "Alt" : "Shift", x + w / 2, y + 14, 34, 255, 220, 140, 255, 1);
                VrOverlay.DrawText("F10", x + w / 2, y + 56, 40, 255, 220, 140, 255, 1);
            }
        }

        static readonly string[] helpControls =
        {
            "L stick: move     R stick: turn",
            "L click: sprint    R click: jetpack",
            "Triggers: tool / second action",
            "A jump   B crouch   X use   Y dampeners",
            "Grip R + A B X Y: build planner",
            "Grip + stick up: key F",
            "Head R: trig light, grip helmet, A=V B=Esc",
            "Head L: trig signals, grip broadcast",
            "Chest trig: inventory   Shoulder grip: gear",
            "L shoulder: trig build, grip HUD  Hips: hotbar",
        };
        static readonly string[] helpHints =
        {
            "Look at your left hand: Menu, Edit, Reset,",
            "Chat, K G I (terminal, G menu, inventory)",
            "Right hand: F10, Alt+F10, Shift+F10",
            "Point with the other hand, pull the trigger",
            "Menu: status, hotbar, block info",
            "Edit: grip moves a part, R stick = size",
            "Cockpit bar: P gear, Z dampeners, L light",
            "Every cockpit type keeps its own layout",
            "The ? on the left hand shows / hides this",
        };

        static void DrawHelp(int w, int h)
        {
            Panel(w, h);
            VrOverlay.DrawText("CONTROLS", 24, 12, 30, 150, 200, 240, 255);
            VrOverlay.DrawText("HINTS", 624, 12, 30, 150, 200, 240, 255);
            for (int i = 0; i < helpControls.Length; i++) VrOverlay.DrawText(helpControls[i], 24, 52 + i * 31, 25, TxR, TxG, TxB, 255);
            for (int i = 0; i < helpHints.Length; i++) VrOverlay.DrawText(helpHints[i], 624, 52 + i * 31, 25, TxR, TxG, TxB, 255);
        }

        static void DrawLeftCluster()
        {
            Btn(8, 0, 0); Btn(9, 0, 120);
            if (EditMode) Btn(10, 0, 240);
            Btn(21, 0, 360);
            Btn(15, 128, 0); Btn(16, 128, 120); Btn(17, 128, 240); Btn(22, 128, 360);
        }

        static void DrawRightCluster() { Btn(18, 0, 0); Btn(19, 0, 120); Btn(20, 0, 240); }

        /// <summary>Which button of cluster 8 or 18 lies under a point (pixels in the cluster's image); -1 for none.</summary>
        public static int ClusterCell(int part, float px, float py)
        {
            int row = (int)(py / 120f);
            if (py < 0 || row < 0 || py - row * 120 > 112) return -1;
            if (part == 18) return px >= 0 && px <= 112 && row <= 2 ? 18 + row : -1;
            int col = px >= 0 && px <= 112 ? 0 : px >= 128 && px <= 240 ? 1 : -1;
            if (col < 0 || row > 3) return -1;
            if (col == 0) { int id = row == 0 ? 8 : row == 1 ? 9 : row == 2 ? 10 : 21; return id == 10 && !EditMode ? -1 : id; }
            return row <= 2 ? 15 + row : 22;
        }

        // ---- the strips: under a part in edit mode (size, hand, head, free), under the menu window (size, lock, keyboard, pages), above it (production multiplier)
        public static int StripHover = -1;          // edit strip: which button the right hand points at (0 size, 1 hand, 2 head, 3 free)
        public static int MenuStripHover = -1;      // menu strip: 0 size, 1 lock, 2 keyboard, 3 previous page, 5 next page
        public static int ProdHover = -1;           // multiplier strip: 0 .. 3
        public static volatile int ProdMultiplier = 1;
        public static readonly int[] ProdValues = { 1, 10, 100, 1000 };
        /// <summary>x10 holds Ctrl, x100 holds Shift, x1000 both: the production window reads them as if the keys were down.</summary>
        public static bool ForceShift { get { int m = ProdMultiplier; return m == 100 || m == 1000; } }
        public static bool ForceCtrl { get { int m = ProdMultiplier; return m == 10 || m == 1000; } }
        public static volatile bool GMenuOpen;          // the G menu (tool bar and block selection) has focus: only there the page arrows are shown
        public static volatile bool LeftAim;            // the left hand points at a HUD target: its trigger belongs to that, not to the tool

        static void Cell(int i, int n, int w, int h, bool hot, bool hov, string text, int px)
        {
            float cw = (w - 16f) / n, x = 8 + i * cw;
            VrOverlay.FillRect(x + 3, 8, cw - 6, h - 16, hot ? 28 : 18, hot ? 90 : 34, hot ? 60 : 50, 255);
            VrOverlay.FrameRect(x + 3, 8, cw - 6, h - 16, hov ? 5 : 3, hov ? 255 : hot ? 120 : LnR, hov ? 255 : hot ? 230 : LnG, hov ? 255 : hot ? 150 : LnB, 255);
            VrOverlay.DrawText(text, x + cw / 2, h / 2f - px * 0.62f, px, TxR, TxG, TxB, 255, 1);
        }

        static void DrawEditStrip(int w, int h)
        {
            int focus = -1, att = 0;
            try { var xr = SeVr.Xr.XrRuntime.Instance; if (xr != null) { focus = xr.FocusPart; att = focus >= 0 ? xr.AttachOf(focus) : 0; } } catch { }
            VrOverlay.FillRect(0, 0, w, h, BgR, BgG, BgB, 215);
            Cell(0, 4, w, h, false, StripHover == 0, "Size", 30);
            Cell(1, 4, w, h, att == 1, StripHover == 1, "Hand", 30);
            Cell(2, 4, w, h, att == 2, StripHover == 2, "Head", 30);
            Cell(3, 4, w, h, att == 0, StripHover == 3, "Free", 30);
            VrOverlay.DrawText("Size: hold the grip and move the hand sideways", w / 2, h - 22, 18, 170, 200, 228, 255, 1);
        }

        public static int MenuStripCells { get { return GMenuOpen ? 6 : 3; } }       // size, lock, keyboard (+ pages in the G menu)

        static void DrawMenuStrip(int w, int h)
        {
            VrOverlay.FillRect(0, 0, w, h, BgR, BgG, BgB, 215);
            bool locked = Plugin.Config.MenuLocked;
            int cells = MenuStripCells;
            Cell(0, cells, w, h, false, MenuStripHover == 0, "Size", 30);
            // the lock: a padlock, closed when the window cannot be moved
            {
                float cw = (w - 16f) / cells, x = 8 + 1 * cw;
                bool hov = MenuStripHover == 1;
                VrOverlay.FillRect(x + 3, 8, cw - 6, h - 16, locked ? 90 : 18, locked ? 50 : 34, locked ? 30 : 50, 255);
                VrOverlay.FrameRect(x + 3, 8, cw - 6, h - 16, hov ? 5 : 3, hov ? 255 : locked ? 255 : LnR, hov ? 255 : locked ? 170 : LnG, hov ? 255 : locked ? 80 : LnB, 255);
                float cx = x + cw / 2f, by = h / 2f - 4;
                VrOverlay.FillRect(cx - 17, by, 34, 26, TxR, TxG, TxB, 255);                                   // body
                float sy = locked ? by - 20 : by - 28;                                                          // shackle (lifted when open)
                VrOverlay.FillRect(cx - 12, sy, 5, 22, TxR, TxG, TxB, 255);
                VrOverlay.FillRect(cx + 7, sy + (locked ? 0 : 6), 5, locked ? 22 : 16, TxR, TxG, TxB, 255);
                VrOverlay.FillRect(cx - 12, sy - 4, 24, 5, TxR, TxG, TxB, 255);
            }
            Cell(2, cells, w, h, VrKeyboard.Shown, MenuStripHover == 2, "Keys", 30);
            if (cells == 6)
            {
                Cell(3, 6, w, h, false, MenuStripHover == 3, "<", 36);
                int page = 0, pages = 0;
                try { var tb = MyToolbarComponent.CurrentToolbar; if (tb != null) { page = tb.CurrentPage; pages = tb.PageCount; } } catch { }
                VrOverlay.DrawText(pages > 0 ? (page + 1) + "/" + pages : "", 8 + 4 * ((w - 16f) / 6f) + ((w - 16f) / 12f), h / 2f - 20, 30, 170, 200, 228, 255, 1);
                Cell(5, 6, w, h, false, MenuStripHover == 5, ">", 36);
            }
        }

        static void DrawProdStrip(int w, int h)
        {
            VrOverlay.FillRect(0, 0, w, h, BgR, BgG, BgB, 215);
            for (int i = 0; i < 4; i++) Cell(i, 4, w, h, ProdMultiplier == ProdValues[i], ProdHover == i, "x" + ProdValues[i], 34);
        }

        static void EditFrame(int part, int w, int h)
        {
            if (!EditMode) return;
            bool hv = false;
            try { var xr = SeVr.Xr.XrRuntime.Instance; hv = xr != null && xr.EditHoverPart == part; } catch { }
            VrOverlay.FrameRect(5, 5, w - 10, h - 10, hv ? 9 : 4, 255, hv ? 200 : 150, 40, 255);
        }

        static void Panel(int w, int h)
        {
            VrOverlay.FillRect(0, 0, w, h, BgR, BgG, BgB, BgA);
            VrOverlay.FrameRect(0, 0, w, h, 3, LnR, LnG, LnB, 255);
            VrOverlay.FillRect(0, 0, 36, 4, 150, 200, 240, 255);       // bright corner mark
        }

        /// <summary>Draw a prepared part into the panel image that is currently set up with Begin2D.</summary>
        public static void Draw(int part, int w, int h)
        {
            try
            {
                if (part == 16) { DrawLeftCluster(); return; }
                if (part == 18) { DrawRightCluster(); return; }
                if (part == 11) { DrawEditStrip(w, h); return; }
                if (part == 13) { DrawMenuStrip(w, h); return; }
                if (part == 14) { DrawProdStrip(w, h); return; }
                Panel(w, h);
                EditFrame(part, w, h);
                switch (part)
                {
                    case 3: DrawStatus(w, h); break;
                    case 4: DrawIndicators(w, h); break;
                    case 5: DrawChat(w, h); break;
                    case 7: DrawNotes(w, h); break;
                    case 2: case 22: DrawToolbar(w, h); break;
                    case 23: DrawSeatBar(w, h); break;
                    case 15: DrawHelp(w, h); break;
                    case 0: DrawInfo(w, h); break;
                    case 1: DrawComponents(w, h); break;
                }
            }
            catch (Exception e) { Plugin.Log("own hud draw " + part + ": " + e.Message); }
        }

        public static int SeatHover = -1;           // the cockpit button bar: which button a hand points at
        public static volatile bool SeatAim;        // a hand points at the cockpit HUD: the trigger belongs to that, not to the ship's weapons
        static readonly string[] seatKeys = { "P", "Z", "L" };
        static readonly string[] seatNames = { "Gear", "Damp", "Light" };
        static bool ShipDamping()
        {
            try { var sc = Sandbox.Game.World.MySession.Static.ControlledEntity; return sc != null && sc.EnabledDamping; }
            catch { return false; }
        }

        static void DrawSeatBar(int w, int h)
        {
            VrOverlay.FillRect(0, 0, w, h, BgR, BgG, BgB, 215);
            bool damp = ShipDamping();
            for (int i = 0; i < 3; i++)
            {
                float cw = (w - 16f) / 3, x = 8 + i * cw;
                bool hot = i == 1 && damp, hov = SeatHover == i;
                VrOverlay.FillRect(x + 3, 8, cw - 6, h - 16, hot ? 28 : 18, hot ? 90 : 34, hot ? 60 : 50, 255);
                VrOverlay.FrameRect(x + 3, 8, cw - 6, h - 16, hov ? 5 : 3, hov ? 255 : hot ? 120 : LnR, hov ? 255 : hot ? 230 : LnG, hov ? 255 : hot ? 150 : LnB, 255);
                VrOverlay.DrawText(seatKeys[i], x + cw / 2, 14, 48, TxR, TxG, TxB, 255, 1);
                VrOverlay.DrawText(seatNames[i], x + cw / 2, 70, 24, 170, 200, 228, 255, 1);
            }
        }

        static void DrawStatus(int w, int h)
        {
            float y = 14;
            foreach (var r in rows)
            {
                VrOverlay.DrawText(r.Label, 18, y + 2, 26, TxR, TxG, TxB, 235);
                if (r.Ratio >= 0f)
                {
                    float bx = 150, bw = w - bx - 108, by = y + 9, bh = 20;
                    VrOverlay.FillRect(bx, by, bw, bh, 28, 44, 60, 255);
                    VrOverlay.FillRect(bx, by, bw * r.Ratio, bh, r.R, r.G, r.B, 255);
                    VrOverlay.FrameRect(bx, by, bw, bh, 2, LnR, LnG, LnB, 255);
                }
                VrOverlay.DrawText(r.Value, w - 18, y + 2, 26, TxR, TxG, TxB, 255, 2);
                y += 46;
            }
        }

        // pointing at the tool bar with the right hand (wrist HUD): slot under the pointer (-1 none), pointer position over the panel (0..1), and
        // whether the right hand points at the tool bar at all (then the trigger picks a slot instead of using the tool)
        public static int HoverSlot = -1;
        public static float PointerU = -1f, PointerV = -1f;
        public static volatile bool HotbarHover;

        /// <summary>The slot at a position of the tool bar panel in pixels (-1 between slots).</summary>
        public const int PagePrevCode = 100, PageNextCode = 101;

        /// <summary>
        /// What is at a position of the tool bar panel in pixels: a slot (0 .. slots-1), the empty-hand slot (= slots), the previous / next page
        /// arrows (PagePrevCode, PageNextCode), or -1.
        /// </summary>
        public static int SlotAt(float px, float py, int w, int slots)
        {
            if (py >= 6f && py <= 56f)
            {
                if (px >= w - 134 && px <= w - 82) return PagePrevCode;
                if (px >= w - 76 && px <= w - 24) return PageNextCode;
                return -1;
            }
            if (py < 62f || py > 62f + 96f) return -1;
            for (int i = 0; i <= slots; i++)
            {
                float x = 20 + i * 104;
                if (px >= x - 4 && px <= x + 96 + 4) return i;
            }
            return -1;
        }

        public static int SlotCount { get { return slotCount; } }

        static void DrawToolbar(int w, int h)
        {
            var tb = toolbar;
            if (tb == null) return;
            int sel = -1;
            try { if (tb.SelectedSlot.HasValue) sel = tb.SelectedSlot.Value; } catch { }
            string selName = null;
            for (int i = 0; i < slotCount; i++)
            {
                float x = 20 + i * 104, y = 62, s = 96;
                MyToolbarItem it = null;
                try { it = tb.GetItemAtSlot(i); } catch { }
                bool on = i == sel;
                VrOverlay.FillRect(x, y, s, s, on ? 40 : 20, on ? 70 : 36, on ? 100 : 52, on ? 255 : 235);
                VrOverlay.FrameRect(x, y, s, s, on ? 4 : 2, on ? 150 : LnR, on ? 210 : LnG, on ? 255 : LnB, 255);
                if (it != null)
                {
                    try
                    {
                        int a = it.Enabled ? 255 : 90;
                        var ic = it.Icons;
                        if (ic != null) foreach (var p in ic) VrOverlay.DrawIcon(p, x + 6, y + 6, s - 12, s - 12, a);
                        if (on && it.DisplayName != null) selName = it.DisplayName.ToString();
                        if (it.IconText != null && it.IconText.Length > 0) VrOverlay.DrawText(it.IconText.ToString(), x + s - 6, y + s - 30, 24, TxR, TxG, TxB, 255, 2);
                    }
                    catch { }
                }
                VrOverlay.DrawText((i + 1).ToString(), x + 8, y + 2, 22, 170, 200, 228, 255);
                if (i == HoverSlot) VrOverlay.FrameRect(x - 3, y - 3, s + 6, s + 6, 5, 255, 255, 255, 255);          // the slot the right hand points at
            }
            if (PointerU >= 0f && HotbarHover)
            {
                float px = PointerU * w, py = PointerV * h;
                VrOverlay.FillRect(px - 9, py - 9, 18, 18, 0, 0, 0, 255);
                VrOverlay.FillRect(px - 6, py - 6, 12, 12, 255, 255, 255, 255);
            }
            // the empty-hand slot (key 0)
            {
                float x = 20 + slotCount * 104, y = 62, s = 96;
                bool on = sel < 0;
                VrOverlay.FillRect(x, y, s, s, on ? 40 : 20, on ? 70 : 36, on ? 100 : 52, on ? 255 : 235);
                VrOverlay.FrameRect(x, y, s, s, on ? 4 : 2, on ? 150 : LnR, on ? 210 : LnG, on ? 255 : LnB, 255);
                VrOverlay.DrawText("0", x + 8, y + 2, 22, 170, 200, 228, 255);
                VrOverlay.DrawText("Empty", x + s / 2, y + 36, 22, TxR, TxG, TxB, 255, 1);
                VrOverlay.DrawText("hand", x + s / 2, y + 58, 22, TxR, TxG, TxB, 255, 1);
                if (HoverSlot == slotCount) VrOverlay.FrameRect(x - 3, y - 3, s + 6, s + 6, 5, 255, 255, 255, 255);
            }
            string head = selName;
            int pageCount = 0, page = 0;
            try { pageCount = tb.PageCount; page = tb.CurrentPage; } catch { }
            // page arrows
            for (int a = 0; a < 2; a++)
            {
                float ax = a == 0 ? w - 134 : w - 76;
                bool hov = HoverSlot == (a == 0 ? PagePrevCode : PageNextCode);
                VrOverlay.FillRect(ax, 8, 52, 44, hov ? 50 : 22, hov ? 90 : 40, hov ? 120 : 58, 255);
                VrOverlay.FrameRect(ax, 8, 52, 44, hov ? 4 : 2, hov ? 255 : LnR, hov ? 255 : LnG, hov ? 255 : LnB, 255);
                VrOverlay.DrawText(a == 0 ? "<" : ">", ax + 26, 10, 34, TxR, TxG, TxB, 255, 1);
            }
            if (pageCount > 1) VrOverlay.DrawText((page + 1) + " / " + pageCount, w - 146, 14, 26, 170, 200, 228, 255, 2);
            if (!string.IsNullOrEmpty(head)) VrOverlay.DrawText(Fit(head, 30, w - 330), 22, 12, 30, TxR, TxG, TxB, 255);
        }

        static void DrawInfo(int w, int h)
        {
            if (sample0)
            {
                VrOverlay.DrawText("Block info (example)", 24, 16, 34, TxR, TxG, TxB, 255);
                VrOverlay.FillRect(24, 74, w - 48, 20, 28, 44, 60, 255); VrOverlay.FillRect(24, 74, (w - 48) * 0.7f, 20, 110, 210, 120, 255);
                VrOverlay.DrawText("shown when you aim at a block", 24, 104, 22, 150, 186, 220, 255);
                return;
            }
            var bi = MyHud.BlockInfo;
            float x = 18;
            var icons = bi.BlockIcons;
            if (icons != null) foreach (var p in icons) VrOverlay.DrawIcon(p, 14, 18, 96, 96, 255);
            x = 126;
            VrOverlay.DrawText(Fit(bi.BlockName, 34, w - x - 110), x, 16, 34, TxR, TxG, TxB, 255);
            if (IsWeaponBlock(bi))
            {
                // the integrity of a weapon says little: no bar, only the type and the PCU
                VrOverlay.DrawText("Weapon", x, 62, 24, 170, 200, 228, 255);
                if (bi.PCUCost > 0) VrOverlay.DrawText("PCU " + bi.PCUCost, w - 24, 62, 24, 170, 200, 228, 255, 2);
                return;
            }
            float ratio = Math.Max(0f, Math.Min(1f, bi.BlockIntegrity));
            float bx = x, bw = w - x - 24, by = 66, bh = 20;
            VrOverlay.FillRect(bx, by, bw, bh, 28, 44, 60, 255);
            int cr = ratio < 0.35f ? 232 : ratio < 0.7f ? 250 : 110, cg = ratio < 0.35f ? 78 : ratio < 0.7f ? 204 : 210, cb = ratio < 0.35f ? 66 : ratio < 0.7f ? 70 : 120;
            VrOverlay.FillRect(bx, by, bw * ratio, bh, cr, cg, cb, 255);
            VrOverlay.FrameRect(bx, by, bw, bh, 2, LnR, LnG, LnB, 255);
            // the marks the game also shows on the bar: below the first the block no longer works, below the second it can be hacked
            float fn = bi.CriticalIntegrity, hk = bi.OwnershipIntegrity;
            if (fn > 0.001f && fn < 0.999f)
            {
                float tx = bx + bw * fn;
                VrOverlay.FillRect(tx - 2, by - 6, 4, bh + 12, 250, 214, 90, 255);
                VrOverlay.DrawText("functional", tx, by + bh + 28, 18, 250, 214, 90, 255, 1);       // one line below "hacked" so the two never overlap
            }
            if (hk > 0.001f && hk < 0.999f)
            {
                float tx = bx + bw * hk;
                VrOverlay.FillRect(tx - 2, by - 6, 4, bh + 12, 240, 90, 80, 255);
                VrOverlay.DrawText("hacked", tx, by + bh + 6, 18, 240, 100, 90, 255, 1);
            }
            VrOverlay.DrawText((int)Math.Round(ratio * 100f) + " %", w - 24, 14, 26, 170, 200, 228, 255, 2);
            if (bi.PCUCost > 0) VrOverlay.DrawText("PCU " + bi.PCUCost, w - 24, by + bh + 28, 22, 150, 186, 220, 255, 2);
        }

        static void DrawComponents(int w, int h)
        {
            if (sample1)
            {
                for (int i = 0; i < 3; i++)
                {
                    VrOverlay.DrawText("Component " + (i + 1) + " (example)", 24, 20 + i * 52, 26, TxR, TxG, TxB, 255);
                    VrOverlay.DrawText((i + 1) + " / 4", w - 24, 20 + i * 52, 26, 120, 214, 140, 255, 2);
                }
                return;
            }
            var bi = MyHud.BlockInfo;
            var list = bi.Components;
            int n = Math.Min(6, list.Count);
            for (int i = 0; i < n; i++)
            {
                var c = list[i];
                float y = 14 + i * 52;
                var ic = c.Icons;
                if (ic != null) foreach (var p in ic) VrOverlay.DrawIcon(p, 14, y, 44, 44, 255);
                bool missing = c.MountedCount < c.TotalCount;
                int r = missing ? 250 : 120, g = missing ? 214 : 214, b = missing ? 90 : 140;
                VrOverlay.DrawText(c.ComponentName ?? "", 72, y + 6, 26, TxR, TxG, TxB, 255);
                VrOverlay.DrawText(c.MountedCount + " / " + c.TotalCount, w - 150, y + 6, 26, r, g, b, 255, 2);
                if (c.AvailableAmount > 0 || missing) VrOverlay.DrawText(((int)Math.Round((double)c.AvailableAmount)) + " in stock", w - 20, y + 6, 22, 150, 186, 220, 255, 2);
            }
        }
    }
}

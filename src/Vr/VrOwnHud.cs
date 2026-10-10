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
                    case 5: return PrepareChat(out w, out h);
                    case 7: return PrepareNotes(out w, out h);
                    case 4:
                        BuildIndicators();
                        w = 440; h = 24 + 2 * 50 + 8 + lines.Count * 40;
                        return true;
                    case 2:
                        toolbar = MyToolbarComponent.CurrentToolbar;
                        if (toolbar == null) return false;
                        slotCount = Math.Min(9, toolbar.SlotCount);
                        if (slotCount <= 0) return false;
                        w = 24 + slotCount * 104; h = 196;
                        return true;
                    case 0:
                    {
                        var bi = MyHud.BlockInfo;
                        if (bi == null || !bi.Visible || string.IsNullOrEmpty(bi.BlockName)) return false;
                        w = 600; h = IsWeaponBlock(bi) ? 104 : 148;
                        return true;
                    }
                    case 1:
                    {
                        var bi = MyHud.BlockInfo;
                        if (bi == null || !bi.Visible || bi.Components == null || bi.Components.Count == 0) return false;
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
            if (chatVis.Count == 0) return false;
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
            if (notes.Count == 0) return false;
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
            chips.Add(new Chip { Label = "Helmet", On = on(() => st.GetStat<MyStatPlayerHelmet>().CurrentValue) });
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
                VrOverlay.DrawText(c.Label, x + 27, y + 11, 18,c.On ? TxR : 130, c.On ? TxG : 150, c.On ? TxB : 170, 255);
            }
            float ly = 14 + 2 * 50 + 8;
            foreach (var l in lines)
            {
                VrOverlay.DrawText(l.Label, 22, ly + 4, 26, 170, 200, 228, 255);
                VrOverlay.DrawText(l.Value, w - 20, ly + 4, 26, l.R, l.G, l.B, 255, 2);
                ly += 40;
            }
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
                Panel(w, h);
                switch (part)
                {
                    case 3: DrawStatus(w, h); break;
                    case 4: DrawIndicators(w, h); break;
                    case 5: DrawChat(w, h); break;
                    case 7: DrawNotes(w, h); break;
                    case 2: DrawToolbar(w, h); break;
                    case 0: DrawInfo(w, h); break;
                    case 1: DrawComponents(w, h); break;
                }
            }
            catch (Exception e) { Plugin.Log("own hud draw " + part + ": " + e.Message); }
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
            }
            string head = selName;
            int pageCount = 0, page = 0;
            try { pageCount = tb.PageCount; page = tb.CurrentPage; } catch { }
            if (pageCount > 1) VrOverlay.DrawText("Page " + (page + 1) + " / " + pageCount, w - 20, 12, 24, 170, 200, 228, 255, 2);
            if (!string.IsNullOrEmpty(head)) VrOverlay.DrawText(head, 22, 12, 30, TxR, TxG, TxB, 255);
        }

        static void DrawInfo(int w, int h)
        {
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

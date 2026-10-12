using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using HarmonyLib;
using Sandbox.Graphics.GUI;
using SeVr.Xr;
using VRage.Input;

namespace SeVr.Vr
{
    /// <summary>
    /// On-screen keyboard for text fields in menus. It appears at the bottom of the floating menu screen when a text box has focus,
    /// the laser and the right trigger press keys. Characters go into the game's text input buffer; Enter and the arrow keys are
    /// sent as real key presses. Clicks on the keyboard are not passed to the game, so the text box keeps its focus.
    /// </summary>
    internal static class VrKeyboard
    {
        enum Kind { Char, Back, Shift, Sym, Space, Enter, Left, Right, Hide }

        sealed class Key
        {
            public Kind Kind; public float Weight; public int Index;          // Index: position in its character row (letters)
            public RectangleF Px;                                             // in bitmap pixels
        }

        public const int BmpW = 1760, BmpH = 820;
        static readonly string[] Lower = { "1234567890ß", "qwertzuiopü", "asdfghjklöä", "yxcvbnm,.-" };
        static readonly string[] Upper = { "!\"§$%&/()=?", "QWERTZUIOPÜ", "ASDFGHJKLÖÄ", "YXCVBNM;:_" };
        static readonly string[] Sym = { "1234567890ß", "@#€%&*[]{}~", "<>|/\\=+-_^`", ";:'\"!?.,()+" };

        static readonly List<List<Key>> rows = Build();
        public static volatile bool Visible;      // a text box has focus (set on the update thread, read by the renderer)
        public static int Layer;                  // 0 lower, 1 upper, 2 symbols
        static readonly bool KbTest = Environment.GetEnvironmentVariable("SEVR_KBTEST") == "1";
        public static bool KbTestOn { get { return KbTest; } }
        static bool loggedProcess, loggedDraw;
        static bool hidden; static object hiddenFor;
        static bool prevTrigger;
        static int checkCounter;
        public static RectangleF Hover = RectangleF.Empty;     // in UV (0..1) of the screen
        public static float PressedFlash;
        static AccessTools.FieldRef<VRage.Input.MyVRageInput, List<char>> textRef;
        static readonly List<char> pending = new List<char>();

        static List<List<Key>> Build()
        {
            var r = new List<List<Key>>();
            for (int row = 0; row < 4; row++)
            {
                var l = new List<Key>();
                if (row == 3) l.Add(new Key { Kind = Kind.Shift, Weight = 1.5f });
                for (int i = 0; i < Lower[row].Length; i++) l.Add(new Key { Kind = Kind.Char, Weight = 1f, Index = i });
                if (row == 0) l.Add(new Key { Kind = Kind.Back, Weight = 1.7f });
                r.Add(l);
            }
            r.Add(new List<Key> {
                new Key { Kind = Kind.Sym, Weight = 1.6f }, new Key { Kind = Kind.Space, Weight = 5.2f },
                new Key { Kind = Kind.Left, Weight = 1.1f }, new Key { Kind = Kind.Right, Weight = 1.1f },
                new Key { Kind = Kind.Enter, Weight = 2.0f }, new Key { Kind = Kind.Hide, Weight = 1.1f } });
            // key rectangles in bitmap pixels
            const float margin = 16f, gap = 9f;
            float rowH = (BmpH - 2 * margin - gap * 4) / 5f;
            for (int ri = 0; ri < r.Count; ri++)
            {
                float sum = 0f; foreach (var k in r[ri]) sum += k.Weight;
                float unit = (BmpW - 2 * margin - gap * (r[ri].Count - 1)) / sum;
                float x = margin, y = margin + ri * (rowH + gap);
                foreach (var k in r[ri]) { k.Px = new RectangleF(x, y, k.Weight * unit, rowH); x += k.Weight * unit + gap; }
            }
            return r;
        }

        static string[] Chars { get { return Layer == 0 ? Lower : Layer == 1 ? Upper : Sym; } }

        static string Label(int row, Key k)
        {
            switch (k.Kind)
            {
                case Kind.Char: return Chars[row][k.Index].ToString();
                case Kind.Back: return "⌫";
                case Kind.Shift: return "⇧";
                case Kind.Sym: return Layer == 2 ? "abc" : "123";
                case Kind.Space: return "";
                case Kind.Enter: return "Enter";
                case Kind.Left: return "←";
                case Kind.Right: return "→";
                default: return "▼";
            }
        }

        // ------------------------------------------------------------------ drawing (render thread)

        public static string LayerKey() { return "kb" + Layer; }

        public static Bitmap MakeBitmap()
        {
            var bmp = new Bitmap(BmpW, BmpH, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using (var bg = RoundRect(new RectangleF(0, 0, BmpW, BmpH), 36f)) using (var br = new SolidBrush(Color.FromArgb(235, 18, 22, 30))) g.FillPath(br, bg);
                using (var font = new Font("Segoe UI Semibold", 56f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var small = new Font("Segoe UI Semibold", 44f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    for (int ri = 0; ri < rows.Count; ri++)
                        foreach (var k in rows[ri])
                        {
                            bool special = k.Kind != Kind.Char && k.Kind != Kind.Space;
                            var col = special ? Color.FromArgb(255, 52, 62, 80) : Color.FromArgb(255, 40, 48, 62);
                            if (k.Kind == Kind.Shift && Layer == 1) col = Color.FromArgb(255, 70, 120, 190);
                            if (k.Kind == Kind.Sym && Layer == 2) col = Color.FromArgb(255, 70, 120, 190);
                            using (var path = RoundRect(k.Px, 18f)) using (var br = new SolidBrush(col)) g.FillPath(br, path);
                            string text = Label(ri, k);
                            if (text.Length > 0) g.DrawString(text, text.Length > 2 ? small : font, Brushes.White, k.Px, fmt);
                        }
                }
            }
            return bmp;
        }

        static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Bitmap MakeWhite()
        {
            var b = new Bitmap(4, 4, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(b)) g.Clear(Color.White);
            return b;
        }

        /// <summary>The keyboard's place on the screen in UV (0..1), bottom centre.</summary>
        public static RectangleF ScreenRect()
        {
            float sw = Math.Max(1, VrRenderer.ScreenW), sh = Math.Max(1, VrRenderer.ScreenH);
            float w = 0.86f, h = w * sw / sh * BmpH / BmpW;
            if (h > 0.55f) { h = 0.55f; w = h * sh / sw * BmpW / BmpH; }
            return new RectangleF(0.5f - w / 2f, 0.985f - h, w, h);
        }

        /// <summary>The keyboard is a window of its own now (shown with the strip button below the menu window), no longer part of the menu screen.</summary>
        public static void DrawOnScreen(SharpDX.Direct3D11.Device dev, SharpDX.Direct3D11.DeviceContext ctx, SharpDX.Direct3D11.RenderTargetView rtv, int width, int height)
        {
        }

        /// <summary>Shown with the keyboard button of the strip below the menu window; it floats freely and can be moved like the menu window.</summary>
        public static volatile bool Shown;

        // ------------------------------------------------------------------ input (update thread)

        static bool TextBoxFocused(out object focused)
        {
            focused = null;
            try
            {
                var s = MyScreenManager.GetScreenWithFocus();
                var c = s != null ? s.FocusedControl : null;
                if (c == null) return false;
                focused = c;
                return c is MyGuiControlTextbox || c is MyGuiControlMultilineEditableText;
            }
            catch { return false; }
        }

        /// <summary>Menu is open: update visibility, hover and clicks. May switch off the mouse buttons so a click on a key does not reach the game.</summary>
        public static void Process(VRage.Input.MyVRageInput input, XrRuntime xr, ref MyMouseState ms)
        {
            if (KbTest) Shown = true;                       // SEVR_KBTEST=1: always show it (for testing without a headset)
            Visible = Shown;
            if (!loggedProcess) { loggedProcess = true; Plugin.Log("keyboard: process running (menu)"); }
            if (PressedFlash > 0f) PressedFlash = Math.Max(0f, PressedFlash - 0.08f);
            Hover = RectangleF.Empty;
            if (!Shown) { prevTrigger = false; return; }

            // the keyboard is a window of its own: the ray of either controller on it gives the position over the key image
            float u, v; bool left = false;
            if (!xr.PointAt(12, true, out u, out v)) { left = true; if (!xr.PointAt(12, false, out u, out v)) { prevTrigger = false; return; } }
            bool trig = (left ? VrControls.TrigLRaw : VrControls.TrigRRaw) > 0.5f;
            bool rise = trig && !prevTrigger;
            prevTrigger = trig;
            ms.LeftButton = false; ms.RightButton = false;                          // the click belongs to the keyboard
            xr.SetHudLaser(xr.PartPoint(12, u, v), left);

            float bx = u * BmpW, by = v * BmpH;
            for (int ri = 0; ri < rows.Count; ri++)
                foreach (var k in rows[ri])
                    if (k.Px.Contains(bx, by))
                    {
                        Hover = new RectangleF(k.Px.X / BmpW, k.Px.Y / BmpH, k.Px.Width / BmpW, k.Px.Height / BmpH);       // over the key image (0..1)
                        if (rise) Press(ri, k);
                        return;
                    }
        }

        static void Press(int row, Key k)
        {
            PressedFlash = 1f;
            switch (k.Kind)
            {
                case Kind.Char: pending.Add(Chars[row][k.Index]); if (Layer == 1) Layer = 0; break;
                case Kind.Space: pending.Add(' '); break;
                case Kind.Back: pending.Add('\b'); break;
                case Kind.Shift: Layer = Layer == 1 ? 0 : 1; break;
                case Kind.Sym: Layer = Layer == 2 ? 0 : 2; break;
                case Kind.Enter: VrControls.ExternalPulse("kb_enter"); break;
                case Kind.Left: VrControls.ExternalPulse("kb_left"); break;
                case Kind.Right: VrControls.ExternalPulse("kb_right"); break;
                case Kind.Hide: Shown = false; Visible = false; break;
            }
        }

        /// <summary>Put the typed characters into the game's text input buffer (end of the input update, before the GUI reads it).</summary>
        public static void Flush(VRage.Input.MyVRageInput input)
        {
            if (pending.Count == 0) return;
            try
            {
                if (textRef == null) textRef = AccessTools.FieldRefAccess<VRage.Input.MyVRageInput, List<char>>("m_currentTextInput");
                var list = textRef(input);
                foreach (var c in pending) list.Add(c);
            }
            catch (Exception e) { Plugin.Log("keyboard flush: " + e.Message); }
            pending.Clear();
        }
    }
}

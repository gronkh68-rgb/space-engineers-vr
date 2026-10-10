using System;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Gui;
using Sandbox.Graphics;
using Sandbox.Graphics.GUI;
using VRageMath;

namespace SeVr.Vr
{
    /// <summary>
    /// Where the game's HUD screen puts its parts. The renderer cuts these rectangles out of the HUD image so the parts can float on the body
    /// (at a hand, the belt, the chest) instead of sitting in one flat window in front of the face.
    /// Parts: 0 block info head, 1 component list, 2 hotbar, 3 status bars, 4 gravity and warnings, 5 chat.
    /// </summary>
    internal static class VrHudLayout
    {
        // The head of the block info is drawn straight into the HUD, directly above the component list.
        const float HeaderH = 0.3225f;

        static FieldInfo toolbarRef, helpRef, chatRef;
        static Vector2 tbPos, tbSize, helpPos, helpSize, chatPos, chatSize;
        static bool helpVisible, haveLayout;
        static long stamp;
        static bool logged;
        static readonly bool logIt = Environment.GetEnvironmentVariable("SEVR_HUDLOG") == "1";

        public static void Install(Harmony h)
        {
            try
            {
                toolbarRef = AccessTools.Field(typeof(MyGuiScreenHudSpace), "m_toolbarControl");
                helpRef = AccessTools.Field(typeof(MyGuiScreenHudSpace), "m_contextHelp");
                chatRef = AccessTools.Field(typeof(MyGuiScreenHudSpace), "m_chatControl");
                var m = AccessTools.Method(typeof(MyGuiScreenHudSpace), "Draw", Type.EmptyTypes);
                if (m == null) { Plugin.Log("hud layout: MyGuiScreenHudSpace.Draw not found"); return; }
                h.Patch(m, postfix: new HarmonyMethod(typeof(VrHudLayout).GetMethod(nameof(DrawPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            }
            catch (Exception e) { Plugin.Log("hud layout install: " + e.Message); }
        }

        static void DrawPostfix(MyGuiScreenHudSpace __instance)
        {
            try
            {
                var tb = toolbarRef.GetValue(__instance) as MyGuiControlBase; var help = helpRef.GetValue(__instance) as MyGuiControlBase; var chat = chatRef.GetValue(__instance) as MyGuiControlBase;
                if (tb != null) { tbPos = tb.GetPositionAbsoluteTopLeft(); tbSize = tb.Size; }
                if (help != null) { helpVisible = help.Visible; helpPos = help.GetPositionAbsoluteTopLeft(); helpSize = help.Size; }
                if (chat != null) { chatPos = chat.GetPositionAbsoluteTopLeft(); chatSize = chat.Size; }
                haveLayout = tb != null;
                stamp = Stopwatch.GetTimestamp();
            }
            catch { }
        }

        /// <summary>The part's rectangle in pixels of the HUD image (null when the part is not shown right now).</summary>
        public static bool TryRect(int part, int imgW, int imgH, out int x, out int y, out int w, out int h)
        {
            x = y = w = h = 0;
            if (stamp == 0 || Stopwatch.GetTimestamp() - stamp > Stopwatch.Frequency * 0.30) return false;
            Vector2 tl, sz;
            switch (part)
            {
                case 0:
                    if (helpVisible) { tl = helpPos; sz = helpSize; }
                    else
                    {
                        Vector2 bt, bs;
                        if (!VrPanel.TryBlockInfo(out bt, out bs)) return false;
                        tl = new Vector2(bt.X, bt.Y - HeaderH); sz = new Vector2(bs.X, HeaderH);
                    }
                    break;
                case 1:
                    if (!VrPanel.TryBlockInfo(out tl, out sz)) return false;
                    break;
                case 2: case 3: case 4:
                {
                    if (!haveLayout) return false;
                    float x0 = tbPos.X - 0.057f, x1 = tbPos.X + tbSize.X + 0.026f, y0 = tbPos.Y - 0.021f;
                    if (part == 2) { tl = new Vector2(x0, y0); sz = new Vector2(x1 - x0, 1f - y0); }
                    else if (part == 3) { tl = new Vector2(0f, y0); sz = new Vector2(x0, 1f - y0); }
                    else { tl = new Vector2(x1, y0); sz = new Vector2(1f - x1, 1f - y0); }
                    break;
                }
                case 5:
                    if (chatSize.X < 0.01f) return false;
                    tl = chatPos; sz = chatSize;
                    break;
                case 6:
                {
                    // the crosshair: a square in the middle of the picture (stays head-locked while the rest of the HUD stays with the world)
                    int s = (int)(0.12f * imgH);
                    x = (imgW - s) / 2; y = (imgH - s) / 2; w = s; h = s;
                    return s >= 16;
                }
                default: return false;
            }
            return ToPixels(tl, sz, imgW, imgH, part, out x, out y, out w, out h);
        }

        static bool ToPixels(Vector2 tl, Vector2 sz, int imgW, int imgH, int part, out int x, out int y, out int w, out int h)
        {
            x = y = w = h = 0;
            try
            {
                var p = MyGuiManager.GetScreenCoordinateFromNormalizedCoordinate(tl, true);
                var s = MyGuiManager.GetScreenSizeFromNormalizedSize(sz, true);
                var fs = MyGuiManager.GetFullscreenRectangle();
                float fx = fs.Width > 0 ? (float)imgW / fs.Width : 1f, fy = fs.Height > 0 ? (float)imgH / fs.Height : 1f;
                int pad = part == 0 ? 2 : 3;
                int x0 = (int)Math.Floor(p.X * fx) - pad, y0 = (int)Math.Floor(p.Y * fy) - pad;
                int x1 = (int)Math.Ceiling((p.X + s.X) * fx) + pad, y1 = (int)Math.Ceiling((p.Y + s.Y) * fy) + pad;
                x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); x1 = Math.Min(imgW, x1); y1 = Math.Min(imgH, y1);
                x1 = Math.Min(x1, x0 + Xr.XrRuntime.PanelW); y1 = Math.Min(y1, y0 + Xr.XrRuntime.PanelH);
                if (x1 - x0 < 16 || y1 - y0 < 16) return false;
                x = x0; y = y0; w = x1 - x0; h = y1 - y0;
                if (logIt && !logged && part == 2) { logged = true; Plugin.Log("hud layout: hotbar " + tl + " " + sz + " -> " + x + "," + y + " " + w + "x" + h + " (image " + imgW + "x" + imgH + ")"); }
                return true;
            }
            catch { return false; }
        }
    }
}


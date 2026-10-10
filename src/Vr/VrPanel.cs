using System;
using System.Diagnostics;
using HarmonyLib;
using Sandbox.Graphics.GUI;
using VRageMath;

namespace SeVr.Vr
{
    /// <summary>
    /// The block info component list of the HUD (a control of its own: MyGuiControlBlockInfo). The game draws it into the HUD image; here its
    /// place is remembered each frame so the HUD layout can cut it out and show it on the body.
    /// </summary>
    internal static class VrPanel
    {
        static Vector2 topLeft, size;
        static long stamp;

        public static void Install(Harmony h)
        {
            var m = AccessTools.Method(typeof(MyGuiControlBlockInfo), "Draw", new[] { typeof(float), typeof(float) });
            if (m == null) { Plugin.Log("panel: MyGuiControlBlockInfo.Draw not found"); return; }
            h.Patch(m, postfix: new HarmonyMethod(typeof(VrPanel).GetMethod(nameof(DrawPostfix), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));
        }

        static void DrawPostfix(MyGuiControlBlockInfo __instance)
        {
            try
            {
                if (!__instance.Visible) return;
                topLeft = __instance.GetPositionAbsoluteTopLeft();
                size = __instance.Size;
                stamp = Stopwatch.GetTimestamp();
            }
            catch { }
        }

        /// <summary>Place and size (GUI coordinates) of the component list when it was drawn a moment ago.</summary>
        public static bool TryBlockInfo(out Vector2 tl, out Vector2 sz)
        {
            tl = topLeft; sz = size;
            long s = stamp;
            return s != 0 && Stopwatch.GetTimestamp() - s <= Stopwatch.Frequency * 0.30;
        }
    }
}

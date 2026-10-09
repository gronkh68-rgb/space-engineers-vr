using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using VRageMath;

namespace SeVr.Vr
{
    /// <summary>
    /// The game draws GPS / signal / ore markers as flat sprites on its HUD. In VR those are taken over: the HUD draw call of
    /// each marker is recorded (world position, label, colour) instead of drawn, and VrOverlay draws them at their real place in
    /// the world, inside both eye views.
    /// </summary>
    internal static class VrMarkers
    {
        public sealed class Marker { public Vector3D Pos; public string Text; public System.Drawing.Color Color; public long Seen; }

        static readonly object gate = new object();
        static readonly Dictionary<string, Marker> markers = new Dictionary<string, Marker>();
        static MethodInfo colorInfo;
        static PropertyInfo posProp, typeProp, textProp;
        static int logCount;

        public static void Install(Harmony h)
        {
            var asm = typeof(Sandbox.Game.Entities.Character.MyCharacter).Assembly;
            var poi = asm.GetType("Sandbox.Game.GUI.HudViewers.MyHudMarkerRender+PointOfInterest", true);
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            posProp = poi.GetProperty("WorldPosition", flags);
            typeProp = poi.GetProperty("POIType", flags);
            textProp = poi.GetProperty("Text", flags);
            colorInfo = poi.GetMethod("GetPOIColorAndFontInformation", flags);
            h.Patch(poi.GetMethod("Draw", flags), prefix: new HarmonyMethod(typeof(VrMarkers).GetMethod(nameof(DrawPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
        }

        // H_MARKER_DRAW: record instead of drawing while VR is showing the world
        static bool DrawPrefix(object __instance)
        {
            try
            {
                if (!VrRenderer.Active || MenuState.IsMenuOpen()) return true;
                var pos = (Vector3D)posProp.GetValue(__instance);
                string text = (textProp.GetValue(__instance) ?? "").ToString();
                string type = typeProp.GetValue(__instance).ToString();
                if (type == "Target") return false;
                var args = new object[] { null, null, null, text };
                colorInfo.Invoke(__instance, args);
                var c = (VRageMath.Color)args[0];
                string key = text + "|" + type + "|" + Math.Round(pos.X) + "," + Math.Round(pos.Y) + "," + Math.Round(pos.Z);
                lock (gate)
                {
                    Marker m;
                    if (!markers.TryGetValue(key, out m)) { m = new Marker(); markers[key] = m; }
                    m.Pos = pos; m.Text = text; m.Color = System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B); m.Seen = Stopwatch.GetTimestamp();
                }
                if (logCount++ < 5) Plugin.Log("markers: " + type + " \"" + text + "\" color " + c.A + "," + c.R + "," + c.G + "," + c.B);
                return false;
            }
            catch (Exception e) { if (logCount++ < 10) Plugin.Log("markers: " + e.Message); return true; }
        }

        /// <summary>Markers drawn by the HUD within the last third of a second.</summary>
        public static List<Marker> Snapshot()
        {
            var list = new List<Marker>();
            long now = Stopwatch.GetTimestamp();
            long maxAge = Stopwatch.Frequency / 3;
            lock (gate)
            {
                List<string> stale = null;
                foreach (var kv in markers)
                {
                    if (now - kv.Value.Seen <= maxAge) list.Add(kv.Value);
                    else if (now - kv.Value.Seen > Stopwatch.Frequency * 5) (stale ?? (stale = new List<string>())).Add(kv.Key);
                }
                if (stale != null) foreach (var k in stale) markers.Remove(k);
            }
            return list;
        }
    }
}

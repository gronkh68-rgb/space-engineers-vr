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
        public sealed class Marker
        {
            public Vector3D Pos; public string Text; public System.Drawing.Color Color; public long Seen;
            public Vector2 Ndc;          // where the game drew it in the HUD picture (-1..1)
            public bool HasSprite;       // the game drew it on screen: its sprites can be cut out of the HUD picture
        }

        /// <summary>The marker's sprites (icon, name, distance) in pixels of the HUD picture: a box below and around its anchor point.</summary>
        public static bool Rect(Marker m, int imgW, int imgH, out int x0, out int y0, out int x1, out int y1)
        {
            x0 = y0 = x1 = y1 = 0;
            if (!m.HasSprite) return false;
            float ax = (m.Ndc.X * 0.5f + 0.5f) * imgW, ay = (0.5f - m.Ndc.Y * 0.5f) * imgH;
            x0 = (int)Math.Floor(ax - 0.11f * imgH); x1 = (int)Math.Ceiling(ax + 0.11f * imgH);
            y0 = (int)Math.Floor(ay - 0.035f * imgH); y1 = (int)Math.Ceiling(ay + 0.075f * imgH);
            x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); x1 = Math.Min(imgW, x1); y1 = Math.Min(imgH, y1);
            return x1 - x0 >= 8 && y1 - y0 >= 8;
        }

        static readonly object gate = new object();
        static readonly Dictionary<string, Marker> markers = new Dictionary<string, Marker>();
        static MethodInfo colorInfo;
        static PropertyInfo posProp, typeProp, textProp;
        static int logCount, ndcLog, passSeq;
        static readonly Dictionary<string, int> passCount = new Dictionary<string, int>();

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
                bool sprites = Plugin.Config.Markers != "custom";     // original: the game draws the marker itself, it is cut out of the HUD picture afterwards
                var pos = (Vector3D)posProp.GetValue(__instance);
                string text = (textProp.GetValue(__instance) ?? "").ToString();
                string type = typeProp.GetValue(__instance).ToString();
                if (type == "Target") return sprites;
                var args = new object[] { null, null, null, text };
                colorInfo.Invoke(__instance, args);
                var c = (VRageMath.Color)args[0];
                string key;
                lock (gate)
                {
                    // identity = name + type + number within this draw pass; the position must not be part of it (a moving marker would leave a trail)
                    if (passSeq != VrHud.PassSeq) { passSeq = VrHud.PassSeq; passCount.Clear(); }
                    string baseKey = text + "|" + type;
                    int n; passCount.TryGetValue(baseKey, out n); passCount[baseKey] = n + 1;
                    key = baseKey + "|" + n;
                    Marker m;
                    if (!markers.TryGetValue(key, out m)) { m = new Marker(); markers[key] = m; }
                    m.Pos = pos; m.Text = text; m.Color = System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B); m.Seen = Stopwatch.GetTimestamp();
                    Vector2 ndc;
                    m.HasSprite = sprites && VrHud.Project(pos, out ndc) && Math.Abs(ndc.X) < 0.96f && Math.Abs(ndc.Y) < 0.96f;
                    if (m.HasSprite) { VrHud.Project(pos, out ndc); m.Ndc = ndc; }
                    if (sprites && ndcLog++ < 12) { Vector2 nd; bool okp = VrHud.Project(pos, out nd); Plugin.Log("markers: project ok=" + okp + " ndc=" + nd + " sprite=" + m.HasSprite); }
                }
                if (logCount++ < 5) Plugin.Log("markers: " + type + " \"" + text + "\" color " + c.A + "," + c.R + "," + c.G + "," + c.B + (sprites ? " (game sprites)" : ""));
                Marker cur; lock (gate) cur = markers[key];
                return sprites && cur.HasSprite;     // markers outside the picture are not drawn: the game would pin them to the edge of the flat HUD picture
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

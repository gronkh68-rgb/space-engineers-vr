using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.World;
using VRageMath;
using VRageRender;

namespace SeVr.Vr
{
    /// <summary>
    /// Overlays that scripts and mods draw with billboards (BuildInfo and its Text HUD API: text, boxes and icons a few decimetres in front of the camera,
    /// laid out for a flat screen). In stereo they sit in a fixed spot of the world at the wrong depth and show up as artifacts.
    /// Such billboards are recognised when they are added (post-processing blend, very close to the camera, small), remembered as positions relative
    /// to the camera, and every frame put in front of the head again: at a comfortable distance, so they read like a screen floating in front of you.
    /// </summary>
    internal static class VrBillboards
    {
        static readonly object gate = new object();
        static readonly Dictionary<MyBillboard, Vector3D[]> hud = new Dictionary<MyBillboard, Vector3D[]>();   // positions in head space
        static readonly HashSet<string> seen = new HashSet<string>();
        static PropertyInfo readProp;
        /// <summary>A billboard of BuildInfo came by (it draws with materials called BuildInfo_... and BI_...).</summary>
        public static volatile bool BuildInfoSeen;
        static int lines, applied;

        public static void Install(Harmony h)
        {
            try
            {
                var t = typeof(MyRenderProxy);
                var add = AccessTools.Method(t, "AddBillboard", new[] { typeof(MyBillboard) });
                if (add != null) h.Patch(add, prefix: new HarmonyMethod(typeof(VrBillboards).GetMethod(nameof(AddPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                readProp = AccessTools.Property(t, "BillboardsRead");
            }
            catch (Exception e) { Plugin.Log("billboards install: " + e.Message); }
        }

        static bool IsHudLike(MyBillboard b, double dist)
        {
            if (b.BlendType != MyBillboard.BlendTypeEnum.PostPP || b.CustomViewProjection >= 0) return false;
            if (dist > 0.6) return false;
            string m = b.Material.ToString();
            if (m.IndexOf("Laser", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("Dot", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("Gizmo", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            double w = (b.Position1 - b.Position0).Length(), hgt = (b.Position3 - b.Position0).Length();
            return Math.Max(w, hgt) < 0.6;
        }

        // false = the billboard is not added at all (mod HUDs blocked)
        static bool AddPrefix(MyBillboard billboard)
        {
            try
            {
                if (billboard == null || !VrRenderer.Active) return true;
                var cam = MySector.MainCamera;
                if (cam == null) return true;
                if (!BuildInfoSeen)
                {
                    string mat = billboard.Material.ToString();
                    if (mat.StartsWith("BuildInfo", StringComparison.OrdinalIgnoreCase) || mat.StartsWith("BI_", StringComparison.Ordinal)) BuildInfoSeen = true;
                }
                string mode = Plugin.Config.ModHud;
                if (mode == "game") return true;
                double dist = (billboard.Position0 - cam.Position).Length();
                bool flagged = IsHudLike(billboard, dist);
                if (flagged && mode == "hide")
                {
                    if (lines < 60) Probe(billboard, dist, true);
                    return false;
                }
                if (flagged)
                {
                    // camera space of the game's own (mono) camera: x right, y up, -z ahead
                    MatrixD inv = cam.ViewMatrix;
                    float dep = Plugin.Config.ModHudDistance, sc = Plugin.Config.ModHudScale;
                    var pts = new Vector3D[4];
                    Vector3D[] src = { billboard.Position0, billboard.Position1, billboard.Position2, billboard.Position3 };
                    // the depth of the whole piece is taken from its first corner, so a piece stays flat
                    double z0 = -Vector3D.Transform(src[0], inv).Z;
                    if (z0 < 0.02) { flagged = false; }
                    else
                    {
                        double k = dep / z0;
                        for (int i = 0; i < 4; i++)
                        {
                            Vector3D l = Vector3D.Transform(src[i], inv);
                            pts[i] = new Vector3D(l.X * k * sc, l.Y * k * sc, -dep);
                        }
                        lock (gate) hud[billboard] = pts;
                    }
                }
                if (!flagged) { lock (gate) { if (hud.Count > 0) hud.Remove(billboard); } }
                Probe(billboard, dist, flagged);
            }
            catch { }
            return true;
        }

        static void Probe(MyBillboard billboard, double dist, bool flagged)
        {
            if (lines > 60) return;
            string key = billboard.Material.ToString() + "|" + billboard.BlendType + "|" + (flagged ? "hud" : dist < 3.0 ? "near" : "far");
            if (!seen.Add(key)) return;
            lines++;
            Plugin.Log("billboard: " + key + " dist " + dist.ToString("0.00"));
        }

        /// <summary>Render thread, once per frame: the remembered pieces are put in front of the head as it is now.</summary>
        public static void ApplyHead()
        {
            try
            {
                if (Plugin.Config.ModHud != "float" || readProp == null) return;
                MatrixD head;
                if (!VrHud.TryHead(out head)) return;
                var list = readProp.GetValue(null) as System.Collections.IList;
                if (list == null) return;
                lock (gate)
                {
                    if (hud.Count == 0) return;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var b = list[i] as MyBillboard;
                        Vector3D[] p;
                        if (b == null || !hud.TryGetValue(b, out p)) continue;
                        b.Position0 = Vector3D.Transform(p[0], head); b.Position1 = Vector3D.Transform(p[1], head);
                        b.Position2 = Vector3D.Transform(p[2], head); b.Position3 = Vector3D.Transform(p[3], head);
                        applied++;
                    }
                }
                if (applied > 0 && lines < 100) { lines = 100; Plugin.Log("billboards: " + applied + " overlay pieces put in front of the head"); }
            }
            catch (Exception e) { if (lines < 110) { lines = 110; Plugin.Log("billboards apply: " + e.Message); } }
        }
    }
}

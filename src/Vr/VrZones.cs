using System;
using SeVr.Xr;
using VRageMath;

namespace SeVr.Vr
{
    /// <summary>
    /// Body zones for controllers with few buttons (Quest, Pico, ...): a hand near the head, chest, hip or shoulder plus trigger or grip
    /// triggers an action, like holstering at the belt in other VR games. The zones sit around the head position in a frame that
    /// follows where the head looks (yaw only), so they stay on your body whichever way you turn.
    /// </summary>
    internal static class VrZones
    {
        public enum Zone { None, RHead, LHead, Shoulder, LShoulder, Chest, RHip, LHip }

        struct Def { public Zone Z; public Vector3 C; public float R; public Def(Zone z, float x, float y, float zz, float r) { Z = z; C = new Vector3(x, y, zz); R = r; } }

        // centre relative to the head (metres): x right, y up, z behind. Checked in this order.
        static readonly Def[] defs =
        {
            new Def(Zone.RHead,    0.13f, -0.03f, -0.02f, 0.14f),
            new Def(Zone.LHead,   -0.13f, -0.03f, -0.02f, 0.14f),
            new Def(Zone.Shoulder, 0.21f, -0.10f,  0.20f, 0.15f),
            new Def(Zone.LShoulder,-0.21f, -0.10f,  0.20f, 0.15f),
            new Def(Zone.Chest,    0.00f, -0.32f, -0.01f, 0.12f),
            new Def(Zone.RHip,     0.25f, -0.64f,  0.00f, 0.20f),
            new Def(Zone.LHip,    -0.25f, -0.64f,  0.00f, 0.20f),
        };

        public static Zone ZoneL, ZoneR;
        static float yaw;

        /// <summary>Which zone each hand is in, from the current head and hand poses (tracking space).</summary>
        public static void Update(XrRuntime xr)
        {
            ZoneL = ZoneR = Zone.None;
            if (!xr.ViewsValid) return;
            var a = xr.Views[0].pose; var b = xr.Views[1].pose;
            Vector3 head = new Vector3((a.position.x + b.position.x) * 0.5f, (a.position.y + b.position.y) * 0.5f, (a.position.z + b.position.z) * 0.5f);
            var hq = xr.HeadOrientation();
            var q = new Quaternion(hq.x, hq.y, hq.z, hq.w);
            Vector3 f = Vector3.Transform(new Vector3(0f, 0f, -1f), q);
            if (f.X * f.X + f.Z * f.Z > 0.09f) yaw = (float)Math.Atan2(f.X, -f.Z);          // looking straight up or down: keep the last heading
            float sy = (float)Math.Sin(yaw), cy = (float)Math.Cos(yaw);
            Vector3 right = new Vector3(cy, 0f, sy), fwd = new Vector3(sy, 0f, -cy);
            if (xr.HandLValid) ZoneL = Find(xr.HandL.position, head, right, fwd);
            if (xr.HandRValid) ZoneR = Find(xr.HandR.position, head, right, fwd);
        }

        static Zone Find(N.Vec3 hand, Vector3 head, Vector3 right, Vector3 fwd)
        {
            Vector3 rel = new Vector3(hand.x - head.X, hand.y - head.Y, hand.z - head.Z);
            Vector3 p = new Vector3(Vector3.Dot(rel, right), rel.Y, -Vector3.Dot(rel, fwd));
            string off = "," + (Plugin.Config.ZonesOff ?? "") + ",";
            foreach (var d in defs)
                if (Vector3.DistanceSquared(p, d.C) < d.R * d.R && off.IndexOf("," + Name(d.Z) + ",", StringComparison.Ordinal) < 0) return d.Z;
            return Zone.None;
        }

        public static string Name(Zone z)
        {
            switch (z) { case Zone.RHead: return "rhead"; case Zone.LHead: return "lhead"; case Zone.Shoulder: return "shoulder"; case Zone.LShoulder: return "lshoulder"; case Zone.Chest: return "chest"; case Zone.RHip: return "rhip"; case Zone.LHip: return "lhip"; default: return ""; }
        }
    }
}



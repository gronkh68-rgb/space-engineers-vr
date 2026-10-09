using System;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using SeVr.Xr;
using VRageMath;

namespace SeVr.Vr
{
    /// <summary>
    /// Building with the hand: the block placement ray comes from the right controller instead of the head, and while the left
    /// grip is held, turning the right hand turns the block (every 40 degrees of wrist movement is one rotation step).
    /// </summary>
    internal static class VrBuild
    {
        static bool active;
        static Matrix baseline;
        static long lastStep;
        const float StepRadians = 0.70f;       // 40 degrees
        static int logCount;
        static long lastTick;
        const float ZoomSpeed = 4f;            // metres per second
        static float ZoomOffset;               // added to the game's block distance

        static void DistancePostfix(ref float __result)
        {
            if (ZoomOffset != 0f) __result = Math.Max(1f, __result + ZoomOffset);
        }

        public static void Install(Harmony h)
        {
            var dist = AccessTools.PropertyGetter(typeof(MyBlockBuilderBase), "IntersectionDistance");
            if (dist != null) h.Patch(dist, postfix: new HarmonyMethod(typeof(VrBuild).GetMethod(nameof(DistancePostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            else Plugin.Log("build: IntersectionDistance not found");
            var t = typeof(MyDefaultPlacementProvider);
            var self = typeof(VrBuild);
            var f = BindingFlags.Static | BindingFlags.NonPublic;
            h.Patch(AccessTools.PropertyGetter(t, "RayStart"), postfix: new HarmonyMethod(self.GetMethod(nameof(RayStartPostfix), f)));
            h.Patch(AccessTools.PropertyGetter(t, "RayDirection"), postfix: new HarmonyMethod(self.GetMethod(nameof(RayDirectionPostfix), f)));
        }

        static bool UseHand()
        {
            var xr = XrRuntime.Instance;
            return VrRenderer.Active && xr != null && xr.HandRValid && VrHead.OnFoot && !MenuState.IsMenuOpen();
        }

        static MatrixD HandWorld()
        {
            return VrHead.EyeWorld(VrRenderer.PoseMatrix(XrRuntime.Instance.HandR), VrRenderer.CamWorld);
        }

        // while the rotate button is held the block keeps the place it had, so the wrist can be turned freely
        static bool frozen;
        static Vector3D frozenStart, frozenDir;

        static void RayStartPostfix(ref Vector3D __result)
        {
            if (frozen) { __result = frozenStart; return; }
            if (UseHand()) __result = HandWorld().Translation;
        }

        static void RayDirectionPostfix(ref Vector3D __result)
        {
            if (frozen) { __result = frozenDir; return; }
            if (UseHand()) __result = Vector3D.Normalize(HandWorld().Forward);
        }

        internal static bool BuilderActive()
        {
            try { return MyCubeBuilder.Static != null && MyCubeBuilder.Static.BlockCreationIsActivated; }
            catch { return false; }
        }

        /// <summary>Once per render frame: wrist gestures while the left grip is held.</summary>
        public static void Update()
        {
            var xr = XrRuntime.Instance;
            bool mode = xr != null && xr.HandRValid && VrControls.GripL > 0.75f && VrHead.OnFoot && BuilderActive();
            VrControls.BuildMode = mode;
            long tnow = System.Diagnostics.Stopwatch.GetTimestamp();
            float dt = Math.Min(0.1f, (tnow - lastTick) / (float)System.Diagnostics.Stopwatch.Frequency);
            lastTick = tnow;
            if (mode && (VrControls.ZoomIn || VrControls.ZoomOut)) ZoomOffset += (VrControls.ZoomOut ? -1f : 1f) * ZoomSpeed * dt;
            if (!BuilderActive()) ZoomOffset = 0f;
            if (!mode) { active = false; frozen = false; return; }
            Matrix cur = (Matrix)HandWorld();
            cur.Translation = Vector3.Zero;
            if (!active)
            {
                var hw = HandWorld();
                frozenStart = hw.Translation; frozenDir = Vector3D.Normalize(hw.Forward); frozen = true;
                baseline = cur; active = true; return;
            }

            // rotation from the baseline hand to the current hand, expressed in the baseline hand's own axes
            Matrix rel = cur * Matrix.Transpose(baseline);
            Quaternion q = Quaternion.CreateFromRotationMatrix(rel);
            if (q.W < 0) q = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);
            float s = (float)Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z);
            if (s < 1e-4f) return;
            float angle = 2f * (float)Math.Atan2(s, q.W);
            Vector3 w = new Vector3(q.X, q.Y, q.Z) * (angle / s);      // axis * angle
            float pitch = w.X, yaw = w.Y, roll = -w.Z;                 // right, up and forward axes of the hand
            float ap = Math.Abs(pitch), ay = Math.Abs(yaw), ar = Math.Abs(roll);
            float big = Math.Max(ap, Math.Max(ay, ar));
            if (big < StepRadians) return;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now - lastStep < System.Diagnostics.Stopwatch.Frequency / 4) return;
            lastStep = now;
            // turning the hand left/right (yaw) rotates the block the opposite way to the first version
            string name = big == ar ? (roll > 0 ? "rot_roll_n" : "rot_roll_p") : big == ap ? (pitch > 0 ? "rot_pitch_p" : "rot_pitch_n") : (yaw > 0 ? "rot_yaw_p" : "rot_yaw_n");
            VrControls.ExternalPulse(name);
            if (logCount++ < 8) Plugin.Log("build: rotate " + name);
            baseline = cur;
        }
    }
}

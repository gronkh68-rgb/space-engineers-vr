using System;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.World;
using SeVr.Xr;
using VRageMath;

namespace SeVr.Vr
{
    /// <summary>
    /// The game's own HUD markers (GPS, signals, ore, ...) with all their sprites, fonts, modes and behaviour. Instead of redrawing them,
    /// the game is told to compute their screen positions with the exact VR head pose and the VR field of view (the game's camera is
    /// swapped for the duration of the marker draw). The HUD image is then shown as a large picture far away that stays where the head
    /// was when it was drawn, so the markers sit on the objects they mark.
    /// </summary>
    internal static class VrHud
    {
        /// <summary>Distance of the HUD picture in metres. Far away so that both eyes see a marker in about the same direction as the object.</summary>
        public const float Distance = XrRuntime.HudDepth;

        static readonly object gate = new object();
        static MatrixD headWorld;                // where the head is now (world), set once per frame by the renderer
        static float tanX = 1f, tanY = 1f;       // half field of view of the HUD picture (tangents)
        static bool valid;
        static MatrixD drawWorld;                // the head pose the HUD was last drawn with
        static float drawTanX = 1f, drawTanY = 1f;
        static long drawStamp;

        // saved camera while swapped
        static bool swapped;
        static MatrixD oldView, oldViewInv, oldWorld, oldViewProj, ourView;
        static Matrix oldProj, oldProjFar;

        public static float TanX { get { lock (gate) return drawTanX; } }
        public static float TanY { get { lock (gate) return drawTanY; } }

        /// <summary>Where the head is now (world matrix, x right, y up, -z ahead); false before the first frame.</summary>
        public static bool TryHead(out MatrixD w)
        {
            lock (gate) { w = headWorld; return valid; }
        }

        /// <summary>Half field of view (tangents) of the HUD picture as of now.</summary>
        public static bool TryFov(out float tx, out float ty)
        {
            lock (gate) { tx = tanX; ty = tanY; return valid; }
        }

        /// <summary>Where a world point lands in the HUD picture, as -1..1 coordinates (the same projection the game's markers were drawn with).</summary>
        public static bool Project(Vector3D world, out Vector2 ndc)
        {
            ndc = Vector2.Zero;
            MatrixD hw; float tx, ty; bool ok;
            lock (gate) { hw = swappedWorld; tx = swappedTanX; ty = swappedTanY; ok = swappedValid; }
            if (!ok) return false;
            Vector3D v = Vector3D.Transform(world, MatrixD.Invert(hw));
            if (v.Z > -0.05) return false;
            ndc = new Vector2((float)(v.X / -v.Z) / tx, (float)(v.Y / -v.Z) / ty);
            return true;
        }
        static MatrixD swappedWorld; static float swappedTanX = 1f, swappedTanY = 1f; static bool swappedValid;

        public static void Install(Harmony h)
        {
            try
            {
                var t = typeof(Sandbox.Game.Entities.Character.MyCharacter).Assembly.GetType("Sandbox.Game.GUI.HudViewers.MyHudMarkerRender", true);
                var m = AccessTools.Method(t, "Draw", Type.EmptyTypes);
                if (m == null) { Plugin.Log("hud: MyHudMarkerRender.Draw not found"); return; }
                var self = typeof(VrHud);
                var f = BindingFlags.Static | BindingFlags.NonPublic;
                h.Patch(m, prefix: new HarmonyMethod(self.GetMethod(nameof(DrawPrefix), f)), finalizer: new HarmonyMethod(self.GetMethod(nameof(DrawFinalizer), f)));
            }
            catch (Exception e) { Plugin.Log("hud install: " + e.Message); }
        }

        /// <summary>Render thread, once per frame: the head pose and the field of view the markers are projected with.</summary>
        public static void Update(XrRuntime xr, int imgW, int imgH)
        {
            try
            {
                if (xr == null || !xr.ViewsValid || imgW <= 0 || imgH <= 0) { lock (gate) valid = false; return; }
                var a = xr.Views[0].pose; var b = xr.Views[1].pose;
                var mid = new Vector3((a.position.x + b.position.x) * 0.5f, (a.position.y + b.position.y) * 0.5f, (a.position.z + b.position.z) * 0.5f);
                var hq = xr.HeadOrientation();
                var q = new Quaternion(hq.x, hq.y, hq.z, hq.w);
                Matrix local = Matrix.CreateFromQuaternion(q);
                local.Translation = mid;
                MatrixD w = VrHead.EyeWorld(local, VrRenderer.CamWorld);
                float ty = 0f;
                for (int i = 0; i < 2; i++)
                {
                    var fov = xr.Views[i].fov;
                    ty = Math.Max(ty, Math.Max((float)Math.Abs(Math.Tan(fov.up)), (float)Math.Abs(Math.Tan(fov.down))));
                }
                if (ty < 0.2f) ty = 0.2f;
                lock (gate) { headWorld = w; tanY = ty; tanX = ty * imgW / imgH; valid = true; }
            }
            catch (Exception e) { Plugin.Log("hud update: " + e.Message); }
        }

        /// <summary>The head pose (tracking space) the HUD image was drawn with, for placing the picture; false when no HUD was drawn lately.</summary>
        public static bool TryDrawPose(out Matrix local, out float tx, out float ty)
        {
            local = Matrix.Identity; tx = ty = 1f;
            MatrixD w; long stamp;
            lock (gate) { w = drawWorld; tx = drawTanX; ty = drawTanY; stamp = drawStamp; }
            if (stamp == 0 || System.Diagnostics.Stopwatch.GetTimestamp() - stamp > System.Diagnostics.Stopwatch.Frequency / 4) return false;
            local = VrHead.LocalFromWorld(w, VrRenderer.CamWorld);
            return true;
        }

        /// <summary>Counts the marker draw passes of the HUD (a marker that moves keeps its identity inside one pass).</summary>
        public static int PassSeq;

        static void DrawPrefix()
        {
            PassSeq++;
            swapped = false;
            try
            {
                if (Plugin.Config.Markers == "custom" || !VrRenderer.Active || MenuState.IsMenuOpen()) return;
                MatrixD hw; float tx, ty; bool ok;
                lock (gate) { hw = headWorld; tx = tanX; ty = tanY; ok = valid; }
                var cam = MySector.MainCamera;
                if (!ok || cam == null) return;
                oldView = cam.ViewMatrix; oldViewInv = cam.ViewMatrixInverse; oldWorld = cam.WorldMatrix; oldViewProj = cam.ViewProjectionMatrix;
                oldProj = cam.ProjectionMatrix; oldProjFar = cam.ProjectionMatrixFar;
                Matrix p = oldProj;
                p.M11 = 1f / tx; p.M22 = 1f / ty; p.M31 = 0f; p.M32 = 0f;
                MatrixD view = MatrixD.Invert(hw);
                cam.ViewMatrix = view; cam.ViewMatrixInverse = hw; cam.WorldMatrix = hw;
                cam.ProjectionMatrix = p; cam.ProjectionMatrixFar = p;
                cam.ViewProjectionMatrix = view * (MatrixD)p;
                ourView = view;
                swapped = true;
                lock (gate)
                {
                    drawWorld = hw; drawTanX = tx; drawTanY = ty; drawStamp = System.Diagnostics.Stopwatch.GetTimestamp();
                    swappedWorld = hw; swappedTanX = tx; swappedTanY = ty; swappedValid = true;
                }
            }
            catch (Exception e) { Plugin.Log("hud prefix: " + e.Message); }
        }

        static Exception DrawFinalizer(Exception __exception)
        {
            lock (gate) swappedValid = false;
            if (swapped)
            {
                swapped = false;
                try
                {
                    var cam = MySector.MainCamera;
                    // only put the old camera back if the game did not set a new one in the meantime (the HUD can be drawn on another thread)
                    if (cam != null && cam.ViewMatrix.Equals(ourView))
                    {
                        cam.ViewMatrix = oldView; cam.ViewMatrixInverse = oldViewInv; cam.WorldMatrix = oldWorld; cam.ViewProjectionMatrix = oldViewProj;
                        cam.ProjectionMatrix = oldProj; cam.ProjectionMatrixFar = oldProjFar;
                    }
                }
                catch (Exception e) { Plugin.Log("hud restore: " + e.Message); }
            }
            return __exception;
        }
    }
}


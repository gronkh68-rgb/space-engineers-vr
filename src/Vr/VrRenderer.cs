using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using SeVr.Xr;
using SharpDX.Direct3D11;
using VRageMath;
using VRageRender;

namespace SeVr.Vr
{
    /// <summary>
    /// Stereo rendering by drawing the whole frame twice with the engine's normal (mono) pipeline, once per eye.
    /// (The engine's built-in side-by-side stereo path leaves the right eye unlit, so it is not used.)
    /// Each pass uses that eye's OpenXR pose and asymmetric FOV; the finished backbuffer is copied into the
    /// left or right half of one OpenXR swapchain image. HUD sprites are replayed into both passes.
    /// </summary>
    internal static class VrRenderer
    {
        static Type tRender11, tEnvMatrices, tMessage;
        static MethodInfo setupInternal, drawMethod, executeCommandList, memberwiseClone;
        static PropertyInfo deviceInstance, backbufferProp, rcProp, deviceSettingsProp;
        static FieldInfo mainSpritesFc;
        static object lastCameraMsg;          // cloned MyRenderMessageSetCameraViewMatrix
        static bool sessionTried, sizeRequested, sizeApplied, dbgApplied;
        public static bool Active;            // true while a VR frame is being produced
        public static float MenuDistance = 1.4f;
        public static float MenuWidth = 1.6f;
        public static float ScreenW = 2160f, ScreenH = 2160f;   // size of the game's image (the mouse position is in these pixels)
        static readonly Dictionary<IntPtr, Texture2D> wrappers = new Dictionary<IntPtr, Texture2D>();
        static int logCount, waitFrames;
        static long lastFrameTicks;
        static MatrixD[] eyeWorldCache = new MatrixD[2]; static Matrix[] projCache = new Matrix[2]; static MatrixD camWorldCache;
        static MethodInfo clearState;

        // per-frame state
        static int currentEye;
        static bool inSecondPass, ranSecondPass, imageAcquired;
        static Texture2D dstTexture;          // current swapchain image (or the fake composite)
        static Texture2D fakeComposite;
        static CommandList spriteStash;

        const BindingFlags All = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static void Install(Harmony h)
        {
            var r11 = Assembly.Load("VRage.Render11");
            tRender11 = r11.GetType("VRageRender.MyRender11", true);
            tEnvMatrices = r11.GetType("VRageRender.MyEnvironmentMatrices", true);
            tMessage = Assembly.Load("VRage.Render").GetType("VRageRender.Messages.MyRenderMessageSetCameraViewMatrix", true);
            setupInternal = tRender11.GetMethod("SetupCameraMatricesInternal", All);
            deviceInstance = tRender11.GetProperty("DeviceInstance", All);
            backbufferProp = tRender11.GetProperty("Backbuffer", All);
            rcProp = tRender11.GetProperty("RC", All);
            deviceSettingsProp = tRender11.GetProperty("DeviceSettings", All);
            mainSpritesFc = tRender11.GetField("m_mainSpritesFC", All);
            drawMethod = tRender11.GetMethod("Draw", All);
            memberwiseClone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
            executeCommandList = rcProp.PropertyType.GetMethod("ExecuteCommandList", All);

            var self = typeof(VrRenderer);
            Func<string, HarmonyMethod> m = n => new HarmonyMethod(self.GetMethod(n, BindingFlags.Static | BindingFlags.NonPublic));
            h.Patch(tRender11.GetMethod("CreateDevice", All), prefix: m(nameof(CreateDevicePrefix)));
            h.Patch(tRender11.GetMethod("SetupCameraMatrices", All), postfix: m(nameof(SetCameraPostfix)));
            h.Patch(drawMethod, prefix: m(nameof(DrawPrefix)), postfix: m(nameof(DrawPostfix)));
            h.Patch(tRender11.GetMethod("Present", All), prefix: m(nameof(PresentPrefix)));
            h.Patch(tRender11.GetMethod("ConsumeMainSprites", All), prefix: m(nameof(ConsumeSpritesPrefix)));
            h.Patch(tRender11.GetMethod("ApplySettings", All), postfix: m(nameof(ApplySettingsPostfix)));
        }

        // -------------------------------------------------------------------------------- sizing

        static MyRenderDeviceSettings CurrentSettings() { return (MyRenderDeviceSettings)deviceSettingsProp.GetValue(null); }

        static void ApplySize(ref MyRenderDeviceSettings s)
        {
            float scale = Plugin.Config.RenderScale;
            int w = (int)(XrRuntime.Instance.EyeWidth * scale);
            int h = (int)(XrRuntime.Instance.EyeHeight * scale);
            s.UseStereoRendering = false;
            s.BackBufferWidth = Math.Min(((w + 15) / 16) * 16, 4096);
            s.BackBufferHeight = Math.Min(((h + 15) / 16) * 16, 4096);
            s.VSync = 0;
        }

        // H_CREATE_DEVICE: keep the VR backbuffer size if the device is ever recreated while VR is active
        static void CreateDevicePrefix(ref MyRenderDeviceSettings? settingsToTry)
        {
            try
            {
                if (!settingsToTry.HasValue || XrRuntime.Instance == null) return;
                var s = settingsToTry.Value;
                ApplySize(ref s);
                settingsToTry = s;
            }
            catch (Exception e) { Plugin.Log("CreateDevicePrefix: " + e); }
        }

        static void ApplySettingsPostfix(MyRenderDeviceSettings settings)
        {
            Plugin.Log("VR: engine ApplySettings " + settings.BackBufferWidth + "x" + settings.BackBufferHeight);
        }

        // -------------------------------------------------------------------------------- camera

        // H_SET_CAMERA
        static void SetCameraPostfix(object message)
        {
            try
            {
                lastCameraMsg = memberwiseClone.Invoke(message, null);
                camStamp = System.Diagnostics.Stopwatch.GetTimestamp();
                if (Active) ApplyEye(currentEye);
            }
            catch (Exception e) { LogOnce("SetCameraPostfix: " + e); }
        }

        static long camStamp;
        public static Matrix PoseMatrix(N.Pose p) { return FromPose(p); }
        public static MatrixD CamWorld { get { return camWorldCache; } }

        static Matrix FromPose(in N.Pose p)
        {
            var q = new Quaternion(p.orientation.x, p.orientation.y, p.orientation.z, p.orientation.w);
            var m = Matrix.CreateFromQuaternion(q);
            m.Translation = new Vector3(p.position.x, p.position.y, p.position.z);
            return m;
        }

        // Off-centre infinite reversed-Z projection in the engine's convention (row vectors, RH).
        static Matrix Projection(in N.Fov f, float near, out float offX, out float offY)
        {
            float L = (float)Math.Tan(f.left), R = (float)Math.Tan(f.right), U = (float)Math.Tan(f.up), D = (float)Math.Tan(f.down);
            var m = new Matrix();
            m.M11 = 2f / (R - L);
            m.M22 = 2f / (U - D);
            m.M34 = -1f;
            m.M43 = near;
            offX = (R + L) / (R - L);
            offY = (U + D) / (U - D);
            return m;
        }

        /// <summary>Write one eye's camera into the engine's main (mono) environment.</summary>
        static void ApplyEye(int eye)
        {
            var xr = XrRuntime.Instance;
            if (lastCameraMsg == null || xr == null || !xr.ViewsValid) return;
            var mt = tMessage;
            MatrixD viewGame = (MatrixD)mt.GetField("ViewMatrix").GetValue(lastCameraMsg);
            float near = (float)mt.GetField("NearPlane").GetValue(lastCameraMsg);
            MatrixD camWorld = MatrixD.Invert(viewGame);
            MatrixD eyeWorld = VrHead.EyeWorld(FromPose(xr.Views[eye].pose), camWorld);

            var msg = memberwiseClone.Invoke(lastCameraMsg, null);
            var fov = xr.Views[eye].fov;
            var P = Projection(fov, near, out float ox, out float oy);
            var Pfull = P; Pfull.M31 = ox; Pfull.M32 = oy;
            eyeWorldCache[eye] = eyeWorld; projCache[eye] = Pfull; camWorldCache = camWorld;
            mt.GetField("ViewMatrix").SetValue(msg, MatrixD.Invert(eyeWorld));
            mt.GetField("ProjectionMatrix").SetValue(msg, P);
            mt.GetField("ProjectionFarMatrix").SetValue(msg, P);
            mt.GetField("FOV").SetValue(msg, 0f);
            mt.GetField("ProjectionOffsetX").SetValue(msg, ox);
            mt.GetField("ProjectionOffsetY").SetValue(msg, oy);
            mt.GetField("CameraPosition").SetValue(msg, eyeWorld.Translation);

            var envMain = tRender11.GetField("Environment", All).GetValue(null);
            var matrices = envMain.GetType().GetField("Matrices", All).GetValue(envMain);
            setupInternal.Invoke(null, new object[] { msg, matrices, Enum.ToObject(Assembly.Load("VRage.Render11").GetType("VRageRender.MyStereoRegion"), 0) });

            // FOV 0 was passed so the engine would take our projection; restore the real FOV values it uses for LOD.
            var res = (Vector2I)tRender11.GetProperty("ResolutionI", All).GetValue(null);
            float vfov = fov.up - fov.down;
            var et = matrices.GetType();
            et.GetField("FovH", All).SetValue(matrices, vfov);
            et.GetField("FovV", All).SetValue(matrices, (float)(2.0 * Math.Atan(Math.Tan(vfov / 2.0) * res.Y / res.X)));
        }

        // -------------------------------------------------------------------------------- frame loop

        // H_DRAW (pass 1: left eye, plus all per-frame OpenXR work)
        static void DrawPrefix(bool draw)
        {
            try
            {
                if (inSecondPass) return;
                var xr = XrRuntime.Instance;
                if (xr == null || xr.Failed) { Active = false; return; }

                if (!sizeApplied)
                {
                    if (!sizeRequested)
                    {
                        var ds = CurrentSettings();
                        ApplySize(ref ds);
                        sizeRequested = true;
                        Plugin.Log("VR: resizing renderer to " + ds.BackBufferWidth + "x" + ds.BackBufferHeight + " per eye");
                        MyRenderProxy.RenderThread.SwitchSettings(ds); // already on the render thread; applied at the next frame start
                        return;
                    }
                    var cur = CurrentSettings();
                    var want = cur; ApplySize(ref want);
                    if (cur.BackBufferWidth != want.BackBufferWidth || cur.BackBufferHeight != want.BackBufferHeight)
                    {
                        if (++waitFrames % 300 == 1) Plugin.Log("VR: waiting for resize, frame " + waitFrames + " now " + cur.BackBufferWidth + "x" + cur.BackBufferHeight);
                        return;
                    }
                    sizeApplied = true;
                    Plugin.Log("VR: renderer ready");
                }
                ApplyDebugOverrides();

                if (!xr.Fake)
                {
                    if (!sessionTried)
                    {
                        var dev = deviceInstance.GetValue(null) as SharpDX.Direct3D11.Device1;
                        if (dev == null) return;
                        sessionTried = true;
                        if (!xr.CreateSession(dev.NativePointer, out string err))
                        {
                            Plugin.Log("VR: session failed: " + err);
                            xr.Shutdown();
                            return;
                        }
                        Plugin.Log("VR: OpenXR session created");
                    }
                    xr.MenuMode = MenuState.IsMenuOpen();
                    Active = xr.BeginFrame();
                }
                else Active = xr.BeginFrame();

                currentEye = 0;
                ranSecondPass = false;
                imageAcquired = false;
                if (xr.Fake == false) xr.HudReady = false; else xr.HudReady = false;
                if (Active)
                {
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    float dt = lastFrameTicks == 0 ? 0.011f : Math.Min(0.1f, (float)(now - lastFrameTicks) / System.Diagnostics.Stopwatch.Frequency);
                    lastFrameTicks = now;
                    if (lastCameraMsg != null)
                    {
                        MatrixD cw = MatrixD.Invert((MatrixD)tMessage.GetField("ViewMatrix").GetValue(lastCameraMsg));
                        Vector3 mid = (FromPose(xr.Views[0].pose).Translation + FromPose(xr.Views[1].pose).Translation) * 0.5f;
                        var hq = xr.HeadOrientation();
                        Matrix hl = Matrix.CreateFromQuaternion(new Quaternion(hq.x, hq.y, hq.z, hq.w));     // the head, not one eye: displays may be turned outwards
                        hl.Translation = mid;
                        VrHead.CamAge = Math.Max(0f, Math.Min(0.05f, (float)(now - camStamp) / System.Diagnostics.Stopwatch.Frequency));
                        VrHead.BeginFrame(cw, hl, mid, dt, VrControls.TurnX, VrControls.TurnY);
                    }
                    VrBuild.Update();
                    ApplyEye(0);
                    VrHud.Update(xr, (int)ScreenW, (int)ScreenH);
                    VrBillboards.ApplyHead();
                }
            }
            catch (Exception e) { LogOnce("DrawPrefix: " + e); }
        }

        /// <summary>World markers and hands, drawn into the eye that was just rendered.</summary>
        static void OverlayEye(int eye)
        {
            try
            {
                var xr = XrRuntime.Instance;
                if (xr == null) return;
                var dev = deviceInstance.GetValue(null) as SharpDX.Direct3D11.Device;
                var bb = backbufferProp.GetValue(null);
                var rtv = bb.GetType().GetProperty("Rtv").GetValue(bb) as RenderTargetView;
                var d = Backbuffer().Description;
                MatrixD? hl = null, hr = null;
                if (xr.HandLValid) hl = VrHead.EyeWorld(FromPose(xr.BodyHandL), camWorldCache);
                if (xr.HandRValid) hr = VrHead.EyeWorld(FromPose(xr.BodyHandR), camWorldCache);
                var marks = VrMarkers.Snapshot();
                if (Environment.GetEnvironmentVariable("SEVR_NOMARK") == "1") marks.Clear();
                if (xr.Fake)   // test markers: one 6 m ahead, one far to the right
                {
                    marks.Add(new VrMarkers.Marker { Pos = camWorldCache.Translation + camWorldCache.Forward * 6.0 + camWorldCache.Up * 0.3, Text = "Test marker", Color = System.Drawing.Color.FromArgb(255, 117, 201, 241) });
                    marks.Add(new VrMarkers.Marker { Pos = camWorldCache.Translation + camWorldCache.Forward * 30.0 + camWorldCache.Right * 25.0, Text = "Far GPS", Color = System.Drawing.Color.FromArgb(255, 255, 200, 60) });
                }
                VrOverlay.DrawEye(dev, Context(), rtv, d.Width, d.Height, eyeWorldCache[eye], projCache[eye], marks, !VrHands.Driving || Plugin.Config.GuideHands || Environment.GetEnvironmentVariable("SEVR_BLOCKHANDS") == "1", hl, hr, VrControls.TrigL, VrControls.TrigR);
                if (clearState == null) clearState = rcProp.PropertyType.GetMethod("ClearState", All);
                if (clearState != null) clearState.Invoke(rcProp.GetValue(null), null);
            }
            catch (Exception e) { LogOnce("OverlayEye: " + e.Message); }
        }
        static bool laserFilled;
        /// <summary>One-time: a tiny swapchain image filled with the laser colour (used as a thin quad from the hand to the menu).</summary>
        static void EnsureLaser()
        {
            var xr = XrRuntime.Instance;
            if (xr == null || xr.Fake || laserFilled) return;
            var d = Backbuffer().Description;
            if (!xr.EnsureLaserSwapchain((int)d.Format, out string err)) { laserFilled = true; LogOnce("VR: laser: " + err); return; }
            IntPtr p = xr.AcquireLaser();
            if (p == IntPtr.Zero) return;
            Texture2D tex;
            if (!wrappers.TryGetValue(p, out tex)) { Marshal.AddRef(p); tex = new Texture2D(p); wrappers[p] = tex; }
            var px = new byte[4 * 4 * 4];
            for (int i = 0; i < 16; i++) { px[i * 4] = 70; px[i * 4 + 1] = 210; px[i * 4 + 2] = 255; px[i * 4 + 3] = 235; }
            Context().UpdateSubresource(px, tex, 0, 16);
            xr.ReleaseLaser();
            laserFilled = true;
            Plugin.Log("VR: laser ready");
        }
        /// <summary>The markers are drawn in the eyes by the overlay now; take their sprites out of the flat HUD picture.</summary>
        static void ClearMarkerRects(DeviceContext ctx, RenderTargetView rtv, Texture2DDescription d)
        {
            if (Plugin.Config.Markers == "custom" || VrOverlay.HudSrv == null) return;
            var rects = new List<SharpDX.Mathematics.Interop.RawRectangle>();
            foreach (var m in VrMarkers.Snapshot())
            {
                int x0, y0, x1, y1;
                if (VrMarkers.Rect(m, d.Width, d.Height, out x0, out y0, out x1, out y1)) rects.Add(new SharpDX.Mathematics.Interop.RawRectangle(x0, y0, x1, y1));
            }
            if (rects.Count == 0) return;
            using (var c1 = ctx.QueryInterface<DeviceContext1>())
                c1.ClearView(rtv, new SharpDX.Mathematics.Interop.RawColor4(0f, 0f, 0f, 0f), rects.ToArray(), rects.Count);
        }
        static Texture2D hudTex; static ShaderResourceView hudSrv;

        /// <summary>
        /// The game's marker sprites need to be looked at before the eye image is finished: the HUD command list is run once into a texture
        /// (the eye image already rendered is kept aside and put back), so the markers can be cut out of it and drawn at their real place.
        /// </summary>
        static void RenderHudTexture()
        {
            VrOverlay.HudSrv = null;
            try
            {
                if (Plugin.Config.Markers == "custom" || spriteStash == null || MenuState.IsMenuOpen()) return;
                var src = Backbuffer();
                var d = src.Description;
                if (hudTex == null || hudTex.Description.Width != d.Width || hudTex.Description.Height != d.Height)
                {
                    if (hudSrv != null) hudSrv.Dispose();
                    if (hudTex != null) hudTex.Dispose();
                    var hd = d; hd.BindFlags = BindFlags.ShaderResource; hd.Usage = ResourceUsage.Default;
                    hudTex = new Texture2D(src.Device, hd);
                    hudSrv = new ShaderResourceView(src.Device, hudTex);
                }
                var bb = backbufferProp.GetValue(null);
                var rtv = bb.GetType().GetProperty("Rtv").GetValue(bb) as RenderTargetView;
                var ctx = Context();
                SaveMirror();                                                   // the eye image as the engine rendered it
                ctx.ClearRenderTargetView(rtv, new SharpDX.Mathematics.Interop.RawColor4(0f, 0f, 0f, 0f));
                var rc = rcProp.GetValue(null);
                var rawFalse = Activator.CreateInstance(executeCommandList.GetParameters()[1].ParameterType, false);
                executeCommandList.Invoke(rc, new object[] { spriteStash, rawFalse });
                ctx.CopyResource(src, hudTex);
                ctx.CopyResource(mirrorTex, src);                               // eye image back
                VrOverlay.HudSrv = hudSrv; VrOverlay.HudW = d.Width; VrOverlay.HudH = d.Height;
                VrOverlay.HudTanX = VrHud.TanX; VrOverlay.HudTanY = VrHud.TanY;
            }
            catch (Exception e) { VrOverlay.HudSrv = null; LogOnce("RenderHudTexture: " + e.Message); }
        }
        static Texture2D mirrorTex;
        static void SaveMirror()
        {
            var src = Backbuffer();
            var d = src.Description;
            if (mirrorTex == null || mirrorTex.Description.Width != d.Width || mirrorTex.Description.Height != d.Height)
            {
                if (mirrorTex != null) mirrorTex.Dispose();
                var md = d; md.BindFlags = BindFlags.ShaderResource; md.Usage = ResourceUsage.Default;
                mirrorTex = new Texture2D(src.Device, md);
            }
            Context().CopyResource(src, mirrorTex);
        }

        static void RestoreMirror()
        {
            if (mirrorTex == null || spriteStash == null) return;
            Context().CopyResource(mirrorTex, Backbuffer());
            var rc = rcProp.GetValue(null);
            var rawFalse = Activator.CreateInstance(executeCommandList.GetParameters()[1].ParameterType, false);
            executeCommandList.Invoke(rc, new object[] { spriteStash, rawFalse });
        }

        // H_DRAW (after pass 1: copy the left eye, then draw pass 2 for the right eye, then the HUD alone)
        static void DrawPostfix(bool draw)
        {
            try
            {
                if (inSecondPass || !Active || !draw) return;
                if (MenuState.IsMenuOpen()) EnsureLaser();
                RenderHudTexture();
                OverlayEye(0);
                if (!CopyEye(0)) return;
                SaveMirror();                // keep the left eye for the desktop window

                inSecondPass = true;
                currentEye = 1;
                ApplyEye(1);
                try { drawMethod.Invoke(null, new object[] { true }); }
                finally { inSecondPass = false; }
                ranSecondPass = true;
                OverlayEye(1);
                CopyEye(1);
                RenderHud();
                RestoreMirror();             // desktop window: left eye with the normal HUD on top
                if (spriteStash != null) { spriteStash.Dispose(); spriteStash = null; }
            }
            catch (Exception e) { inSecondPass = false; LogOnce("DrawPostfix: " + e); }
        }

        // H_CONSUME_SPRITES: in gameplay the HUD/GUI is kept out of both eye views. Its recorded command list is taken
        // over here and drawn on its own afterwards (RenderHud), onto a separate quad.
        static bool ConsumeSpritesPrefix()
        {
            try
            {
                if (!Active) return true;

                // The engine starts a parallel sprite task in every Draw and waits for it here. The second (right-eye) pass must wait
                // for its task too and throw the result away; otherwise it keeps running into the next frame's task and the two race
                // on the sprite renderer's lists and buffers (crash while loading, corrupted icons).
                var taskField = tRender11.GetField("m_mainSpritesTask", All);
                object task = taskField.GetValue(null);
                var validF = task.GetType().GetField("valid");
                if (!(bool)validF.GetValue(task)) return false;
                var wait = task.GetType().GetMethod("Wait", All);
                wait.Invoke(task, wait.GetParameters().Length == 1 ? new object[] { true } : new object[0]);
                object fc = mainSpritesFc.GetValue(null);
                var cl = fc.GetType().GetField("CommandList").GetValue(fc) as CommandList;
                if (inSecondPass)
                {
                    if (cl != null) cl.Dispose();     // right-eye pass: the HUD of pass 1 is the one that is used
                }
                else if (spriteStash != null) { spriteStash.Dispose(); spriteStash = null; }
                if (cl != null && !inSecondPass)
                {
                    Marshal.AddRef(cl.NativePointer);
                    spriteStash = new CommandList(cl.NativePointer);
                    cl.Dispose();     // the engine's own reference; ours keeps it alive
                }
                validF.SetValue(task, false);
                taskField.SetValue(null, task);
                mainSpritesFc.SetValue(null, Activator.CreateInstance(fc.GetType()));
                return false;
            }
            catch (Exception e) { LogOnce("ConsumeSpritesPrefix: " + e); }
            return true;
        }

        static readonly Texture2D[] partFake = new Texture2D[XrRuntime.PartCount];

        // widths in metres of the floating parts (scaled with the panel size setting), in the order of the parts
        static readonly float[] PartWidths = { 0.26f, 0.26f, 0.44f, 0.20f, 0.15f, 0.32f, 0.1f };

        /// <summary>Cut the HUD parts (block info, hotbar, status bars, ...) out of the HUD image into their own images, shown on the body.</summary>
        static void ExtractParts(XrRuntime xr, Texture2D src, RenderTargetView rtv, Texture2DDescription d)
        {
            var cfg = Plugin.Config;
            var ctx = Context();
            float scale = cfg.PanelSize / 0.26f;
            // 1. the parts the game lays out itself: block info, components, chat, crosshair
            int[] fixedParts = { 0, 1, 5, 6 };
            var rects = new List<int[]>();       // part, x, y, w, h
            bool own = cfg.OwnHud;
            foreach (int part in fixedParts)
            {
                int x, y, w, h;
                if (cfg.Anchors[part] == "hud" || !VrHudLayout.TryRect(part, d.Width, d.Height, out x, out y, out w, out h)) continue;
                if (own && part != 6) { rects.Add(new[] { part, x, y, w, h }); continue; }     // the plugin draws these itself: the game's own are only wiped from the picture
                float widthM = part == 6 ? 2f * XrRuntime.HudDepth * VrHud.TanX * w / d.Width : PartWidths[part] * scale;     // the crosshair keeps its size in the picture
                if (CopyPart(xr, src, d, ctx, part, x, y, w, h, cfg.Anchors[part], widthM)) rects.Add(new[] { part, x, y, w, h });
            }
            if (own)
            {
                // tool bar and status bars of the game: wiped from the picture, replaced by the plugin's own
                int x, y, w, h;
                foreach (int part in new[] { 2, 3 })
                    if (cfg.Anchors[2] != "hud" && VrHudLayout.TryRect(part, d.Width, d.Height, out x, out y, out w, out h)) rects.Add(new[] { part, x, y, w, h });
                float mpp = cfg.PanelSize / 560f;
                foreach (int part in new[] { 3, 2, 4, 5, 7, 0, 1 })
                    RenderOwnPart(xr, src, d, ctx, part, part >= 2 ? cfg.Anchors[2] : cfg.Anchors[part], mpp);
            }
            ClearRects(ctx, rtv, rects);
            // 2. everything else that the HUD draws (whatever its layout or mods): found in the picture itself, every piece shown on its own
            rects.Clear();
            if (!own && cfg.Anchors[2] != "hud")      // with the own HUD nothing of the game's HUD is shown any more: everything it shows is drawn by the plugin
            {
                int[] slots = XrRuntime.ClusterSlots;
                var found = FindClusters(src, d, ctx, slots.Length);
                for (int i = 0; i < found.Count && i < slots.Length; i++)
                {
                    int part = slots[i];
                    int[] r = found[i];
                    float widthM = Math.Min(0.8f, r[2] * cfg.PanelSize / 560f);
                    if (CopyPart(xr, src, d, ctx, part, r[0], r[1], r[2], r[3], cfg.Anchors[2], widthM)) rects.Add(new[] { part, r[0], r[1], r[2], r[3] });
                }
                ClearRects(ctx, rtv, rects);
            }
        }

        static Texture2D ownScratch; static RenderTargetView ownScratchRtv;

        /// <summary>Draw one of the plugin's own HUD panels (status, tool bar, block info, components) straight into the part's image.</summary>
        static void RenderOwnPart(XrRuntime xr, Texture2D src, Texture2DDescription d, DeviceContext ctx, int part, string anchor, float mpp)
        {
            if (anchor == "hud") return;
            int w, h;
            if (!VrOwnHud.Prepare(part, out w, out h)) return;
            w = Math.Min(w, XrRuntime.PanelW); h = Math.Min(h, XrRuntime.PanelH);
            Texture2D dst;
            bool real = !xr.Fake;
            if (real)
            {
                string err;
                if (!xr.EnsurePanelSwapchain(part, (int)d.Format, out err)) { LogOnce("VR: " + err); return; }
                IntPtr p = xr.AcquirePanel(part);
                if (p == IntPtr.Zero) return;
                if (!wrappers.TryGetValue(p, out dst)) { Marshal.AddRef(p); dst = new Texture2D(p); wrappers[p] = dst; }
            }
            else
            {
                if (partFake[part] == null) { var pd = d; pd.Width = XrRuntime.PanelW; pd.Height = XrRuntime.PanelH; pd.BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget; pd.Usage = ResourceUsage.Default; partFake[part] = new Texture2D(src.Device, pd); }
                dst = partFake[part];
            }
            try
            {
                // the headset runtime's images are not necessarily usable as render targets: draw into an image of our own, then copy
                if (ownScratch == null || ownScratch.Description.Format != d.Format)
                {
                    if (ownScratch != null) { ownScratchRtv.Dispose(); ownScratch.Dispose(); }
                    var sd = d;
                    sd.Width = XrRuntime.PanelW; sd.Height = XrRuntime.PanelH; sd.MipLevels = 1; sd.ArraySize = 1;
                    sd.SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0);
                    sd.BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource; sd.Usage = ResourceUsage.Default; sd.CpuAccessFlags = CpuAccessFlags.None; sd.OptionFlags = ResourceOptionFlags.None;
                    ownScratch = new Texture2D(src.Device, sd);
                    ownScratchRtv = new RenderTargetView(src.Device, ownScratch);
                }
                bool srgb = d.Format.ToString().Contains("SRgb");
                if (VrOverlay.Begin2D(src.Device, ctx, ownScratchRtv, w, h, srgb))
                {
                    VrOwnHud.Draw(part, w, h);
                    VrOverlay.End2D();
                    ctx.CopySubresourceRegion(ownScratch, 0, new ResourceRegion(0, 0, 0, w, h, 1), dst, 0, 0, 0, 0);
                }
                if (clearState == null) clearState = rcProp.PropertyType.GetMethod("ClearState", All);
                if (clearState != null) clearState.Invoke(rcProp.GetValue(null), null);
            }
            catch (Exception e) { LogOnce("own hud: " + e.Message); }
            if (real) xr.ReleasePanel(part);
            float fs = Math.Max(0.5f, Plugin.Config.HudDistance) / 0.72f;       // farther away = proportionally larger, so the apparent size stays
            xr.SetPanel(part, w, h, anchor, Math.Min(0.9f * fs, w * mpp * fs), true);
        }

        static bool CopyPart(XrRuntime xr, Texture2D src, Texture2DDescription d, DeviceContext ctx, int part, int x, int y, int w, int h, string anchor, float widthM)
        {
            Texture2D dst;
            bool real = !xr.Fake;
            if (real)
            {
                string err;
                if (!xr.EnsurePanelSwapchain(part, (int)d.Format, out err)) { LogOnce("VR: " + err); return false; }
                IntPtr p = xr.AcquirePanel(part);
                if (p == IntPtr.Zero) return false;
                if (!wrappers.TryGetValue(p, out dst)) { Marshal.AddRef(p); dst = new Texture2D(p); wrappers[p] = dst; }
            }
            else
            {
                if (partFake[part] == null) { var pd = d; pd.Width = XrRuntime.PanelW; pd.Height = XrRuntime.PanelH; pd.BindFlags = BindFlags.ShaderResource; pd.Usage = ResourceUsage.Default; partFake[part] = new Texture2D(src.Device, pd); }
                dst = partFake[part];
            }
            ctx.CopySubresourceRegion(src, 0, new ResourceRegion(x, y, 0, x + w, y + h, 1), dst, 0, 0, 0, 0);
            if (real) xr.ReleasePanel(part);
            xr.SetPanel(part, w, h, anchor, widthM);
            return true;
        }

        /// <summary>The flat HUD no longer shows what floats on the body now.</summary>
        static void ClearRects(DeviceContext ctx, RenderTargetView rtv, List<int[]> rects)
        {
            if (rects.Count == 0) return;
            using (var c1 = ctx.QueryInterface<DeviceContext1>())
                foreach (var r in rects)
                    c1.ClearView(rtv, new SharpDX.Mathematics.Interop.RawColor4(0f, 0f, 0f, 0f), new[] { new SharpDX.Mathematics.Interop.RawRectangle(r[1], r[2], r[1] + r[3], r[2] + r[4]) }, 1);
        }

        static Texture2D clusterStaging;
        static long clusterStamp;
        static List<int[]> clusterCache = new List<int[]>();
        const int Cell = 16;

        /// <summary>
        /// Looks at what is left in the HUD picture and finds its separate pieces (rectangles x, y, w, h, left to right). Works for any HUD layout,
        /// including mods that draw their own bars. About ten times a second; in between the last result is used.
        /// </summary>
        static int clusterMax;
        static unsafe List<int[]> FindClusters(Texture2D src, Texture2DDescription d, DeviceContext ctx, int maxPieces)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (maxPieces != clusterMax) { clusterMax = maxPieces; clusterStamp = 0; }
            if (clusterStamp != 0 && now - clusterStamp < System.Diagnostics.Stopwatch.Frequency / 10) return clusterCache;
            clusterStamp = now;
            try
            {
                var f = d.Format;
                if (f != SharpDX.DXGI.Format.R8G8B8A8_UNorm && f != SharpDX.DXGI.Format.R8G8B8A8_UNorm_SRgb && f != SharpDX.DXGI.Format.B8G8R8A8_UNorm && f != SharpDX.DXGI.Format.B8G8R8A8_UNorm_SRgb) return clusterCache;
                if (clusterStaging == null || clusterStaging.Description.Width != d.Width || clusterStaging.Description.Height != d.Height)
                {
                    if (clusterStaging != null) clusterStaging.Dispose();
                    var sd = d; sd.Usage = ResourceUsage.Staging; sd.BindFlags = BindFlags.None; sd.CpuAccessFlags = CpuAccessFlags.Read; sd.OptionFlags = ResourceOptionFlags.None;
                    sd.SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0); sd.MipLevels = 1; sd.ArraySize = 1;
                    clusterStaging = new Texture2D(src.Device, sd);
                }
                ctx.CopyResource(src, clusterStaging);
                int w = d.Width, h = d.Height, cw = (w + Cell - 1) / Cell, ch = (h + Cell - 1) / Cell;
                var occ = new bool[cw * ch];
                var box = ctx.MapSubresource(clusterStaging, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
                try
                {
                    byte* basePtr = (byte*)box.DataPointer;
                    for (int y = 0; y < h; y += 2)
                    {
                        byte* row = basePtr + (long)y * box.RowPitch;
                        int cy = (y / Cell) * cw;
                        for (int x = 0; x < w; x += 2)
                            if (row[x * 4 + 3] > 12) occ[cy + x / Cell] = true;
                    }
                }
                finally { ctx.UnmapSubresource(clusterStaging, 0); }

                // connect cells that are at most two cells apart
                var seen = new bool[cw * ch];
                var list = new List<int[]>();       // x0, y0, x1, y1 in pixels
                var stack = new Stack<int>();
                for (int s = 0; s < occ.Length; s++)
                {
                    if (!occ[s] || seen[s]) continue;
                    int minx = cw, miny = ch, maxx = -1, maxy = -1;
                    seen[s] = true; stack.Push(s);
                    while (stack.Count > 0)
                    {
                        int c = stack.Pop(); int cx = c % cw, cyy = c / cw;
                        if (cx < minx) minx = cx; if (cx > maxx) maxx = cx; if (cyy < miny) miny = cyy; if (cyy > maxy) maxy = cyy;
                        for (int dy = -2; dy <= 2; dy++)
                            for (int dx = -2; dx <= 2; dx++)
                            {
                                int nx = cx + dx, ny = cyy + dy;
                                if (nx < 0 || ny < 0 || nx >= cw || ny >= ch) continue;
                                int ni = ny * cw + nx;
                                if (occ[ni] && !seen[ni]) { seen[ni] = true; stack.Push(ni); }
                            }
                    }
                    if ((maxx - minx + 1) * Cell < 24 && (maxy - miny + 1) * Cell < 24) continue;       // specks
                    list.Add(new[] { minx * Cell, miny * Cell, (maxx + 1) * Cell, (maxy + 1) * Cell });
                }
                // more pieces than slots: join the closest ones
                while (list.Count > maxPieces)
                {
                    int bi = 0, bj = 1; long best = long.MaxValue;
                    for (int i = 0; i < list.Count; i++)
                        for (int j = i + 1; j < list.Count; j++)
                        {
                            var a = list[i]; var b = list[j];
                            long gx = Math.Max(0, Math.Max(a[0], b[0]) - Math.Min(a[2], b[2])), gy = Math.Max(0, Math.Max(a[1], b[1]) - Math.Min(a[3], b[3]));
                            if (gx + gy < best) { best = gx + gy; bi = i; bj = j; }
                        }
                    var m = new[] { Math.Min(list[bi][0], list[bj][0]), Math.Min(list[bi][1], list[bj][1]), Math.Max(list[bi][2], list[bj][2]), Math.Max(list[bi][3], list[bj][3]) };
                    list.RemoveAt(bj); list[bi] = m;
                }
                list.Sort((a, b) => (a[0] + a[2]).CompareTo(b[0] + b[2]));
                var result = new List<int[]>();
                foreach (var r in list)
                {
                    int x0 = Math.Max(0, r[0] - 6), y0 = Math.Max(0, r[1] - 6), x1 = Math.Min(w, r[2] + 6), y1 = Math.Min(h, r[3] + 6);
                    x1 = Math.Min(x1, x0 + XrRuntime.PanelW); y1 = Math.Min(y1, y0 + XrRuntime.PanelH);
                    if (x1 - x0 >= 16 && y1 - y0 >= 16) result.Add(new[] { x0, y0, x1 - x0, y1 - y0 });
                }
                clusterCache = result;
            }
            catch (Exception e) { LogOnce("clusters: " + e.Message); }
            return clusterCache;
        }

        static Texture2D hudFake;
        /// <summary>Draw the HUD alone on a cleared backbuffer and copy it to the HUD image.</summary>
        static void RenderHud()
        {
            var xr = XrRuntime.Instance;
            if (spriteStash == null) return;
            var bb = backbufferProp.GetValue(null);
            var src = bb.GetType().GetProperty("Resource").GetValue(bb) as Texture2D;
            var rtv = bb.GetType().GetProperty("Rtv").GetValue(bb) as RenderTargetView;
            var d = src.Description;
            var ctx = Context();
            ctx.ClearRenderTargetView(rtv, new SharpDX.Mathematics.Interop.RawColor4(0f, 0f, 0f, 0f));
            var rc = rcProp.GetValue(null);
            var rawFalse = Activator.CreateInstance(executeCommandList.GetParameters()[1].ParameterType, false);
            executeCommandList.Invoke(rc, new object[] { spriteStash, rawFalse });
            VrKeyboard.DrawOnScreen(src.Device, ctx, rtv, d.Width, d.Height);     // on-screen keyboard on top of the menu
            if (!MenuState.IsMenuOpen())
            {
                ClearMarkerRects(Context(), rtv, d); ExtractParts(xr, src, rtv, d);
                // own HUD: what was found became panels, what is left is faint remains of the game's HUD (mod glows, shadows): wipe the flat picture
                if (Plugin.Config.OwnHud && Plugin.Config.Anchors[2] != "hud") Context().ClearRenderTargetView(rtv, new SharpDX.Mathematics.Interop.RawColor4(0f, 0f, 0f, 0f));
            }

            Texture2D dst;
            if (xr.Fake)
            {
                if (hudFake == null || hudFake.Description.Width != d.Width)
                {
                    if (hudFake != null) hudFake.Dispose();
                    var hd = d; hd.BindFlags = BindFlags.ShaderResource; hd.Usage = ResourceUsage.Default;
                    hudFake = new Texture2D(src.Device, hd);
                }
                dst = hudFake;
            }
            else
            {
                if (!xr.EnsureHudSwapchain((uint)d.Width, (uint)d.Height, (int)d.Format, out string err)) { LogOnce("VR: " + err); return; }
                IntPtr p = xr.AcquireHud();
                if (p == IntPtr.Zero) return;
                if (!wrappers.TryGetValue(p, out dst)) { Marshal.AddRef(p); dst = new Texture2D(p); wrappers[p] = dst; }
            }
            ctx.CopyResource(src, dst);
            if (!xr.Fake) xr.ReleaseHud();

            xr.HudReady = true;
        }

        static Texture2D Backbuffer()
        {
            var bb = backbufferProp.GetValue(null);
            return bb.GetType().GetProperty("Resource").GetValue(bb) as Texture2D;
        }

        static DeviceContext Context()
        {
            var rc = rcProp.GetValue(null);
            return rc.GetType().GetProperty("DeviceContext").GetValue(rc) as DeviceContext;
        }

        /// <summary>Copy the finished backbuffer into the left (0) or right (1) half of the eye image.</summary>
        static bool CopyEye(int eye)
        {
            var xr = XrRuntime.Instance;
            var src = Backbuffer();
            if (src == null) return false;
            var d = src.Description;
            ScreenW = d.Width; ScreenH = d.Height;
            if (!imageAcquired)
            {
                if (xr.Fake)
                {
                    if (fakeComposite == null || fakeComposite.Description.Width != d.Width * 2)
                    {
                        if (fakeComposite != null) fakeComposite.Dispose();
                        var cd = d; cd.Width = d.Width * 2; cd.BindFlags = BindFlags.ShaderResource; cd.Usage = ResourceUsage.Default;
                        fakeComposite = new Texture2D(src.Device, cd);
                    }
                    dstTexture = fakeComposite;
                }
                else
                {
                    if (!xr.SessionRunning) return false;
                    if (!xr.EnsureSwapchain((uint)d.Width * 2, (uint)d.Height, (int)d.Format, out string err)) { LogOnce("VR: " + err); return false; }
                    IntPtr p = xr.AcquireImage();
                    if (p == IntPtr.Zero) return false;
                    if (!wrappers.TryGetValue(p, out dstTexture))
                    {
                        Marshal.AddRef(p);
                        dstTexture = new Texture2D(p);
                        wrappers[p] = dstTexture;
                    }
                }
                imageAcquired = true;
            }
            Context().CopySubresourceRegion(src, 0, null, dstTexture, 0, eye * d.Width, 0, 0);
            return true;
        }

        // H_PRESENT (after both passes: hand the frame to OpenXR)
        static void PresentPrefix()
        {
            try
            {
                var xr = XrRuntime.Instance;
                if (xr == null || !Active || !imageAcquired) return;
                imageAcquired = false;
                xr.HudLocked = false;
                float htx, hty;
                if (!MenuState.IsMenuOpen() && Plugin.Config.Markers != "custom" && VrHud.TryFov(out htx, out hty))
                {
                    xr.HudTanX = htx; xr.HudTanY = hty; xr.HudLocked = true;       // the HUD picture covers the field of view the markers were drawn for
                }
                if (xr.Fake) { DebugCapture(); return; }
                xr.ReleaseImage();
                xr.EndFrame(ranSecondPass, MenuDistance, MenuWidth);
            }
            catch (Exception e) { LogOnce("PresentPrefix: " + e); }
        }

        // -------------------------------------------------------------------------------- debug (fake-eye test mode)

        static void ApplyDebugOverrides()
        {
            if (dbgApplied) return;
            dbgApplied = true;
            var dbg = Environment.GetEnvironmentVariable("SEVR_DBG") ?? "";
            if (dbg.Length == 0) return;
            var ov = tRender11.GetProperty("DebugOverrides", All).GetValue(null);
            foreach (var name in dbg.Split(','))
            {
                var fi = ov.GetType().GetField(name.Trim());
                if (fi != null) { fi.SetValue(ov, false); Plugin.Log("VR: debug override " + name + "=false"); }
            }
        }

        static int capFrames; static bool captured;
        static readonly int capTarget = int.TryParse(Environment.GetEnvironmentVariable("SEVR_CAPFRAMES"), out int cf) ? cf : 900;
        static void DebugCapture()
        {
            if (captured || !ranSecondPass || ++capFrames < capTarget) return;
            captured = true;
            SaveTex(fakeComposite, "SeVr_capture.png", false);
            if (hudFake != null) SaveTex(hudFake, "SeVr_hud.png", true);
            for (int pi = 0; pi < partFake.Length; pi++) if (partFake[pi] != null) SaveTex(partFake[pi], "SeVr_part" + pi + ".png", true);
        }

        static void SaveTex(Texture2D res, string fileName, bool alpha)
        {
            var d = res.Description;
            var sd = d; sd.Usage = ResourceUsage.Staging; sd.BindFlags = BindFlags.None; sd.CpuAccessFlags = CpuAccessFlags.Read; sd.OptionFlags = ResourceOptionFlags.None;
            using (var st = new Texture2D(res.Device, sd))
            {
                var ctx = Context();
                ctx.CopyResource(res, st);
                var map = ctx.MapSubresource(st, 0, MapMode.Read, MapFlags.None);
                bool bgra = d.Format.ToString().StartsWith("B8G8R8A8");
                using (var bmp = new System.Drawing.Bitmap(d.Width, d.Height, (alpha ? System.Drawing.Imaging.PixelFormat.Format32bppArgb : System.Drawing.Imaging.PixelFormat.Format32bppRgb)))
                {
                    var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, d.Width, d.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, (alpha ? System.Drawing.Imaging.PixelFormat.Format32bppArgb : System.Drawing.Imaging.PixelFormat.Format32bppRgb));
                    var row = new byte[d.Width * 4];
                    for (int y = 0; y < d.Height; y++)
                    {
                        Marshal.Copy(map.DataPointer + y * map.RowPitch, row, 0, row.Length);
                        if (!bgra) for (int x = 0; x < row.Length; x += 4) { byte t = row[x]; row[x] = row[x + 2]; row[x + 2] = t; }
                        Marshal.Copy(row, 0, bd.Scan0 + y * bd.Stride, row.Length);
                    }
                    bmp.UnlockBits(bd);
                    var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceEngineers", fileName);
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    Plugin.Log("VR: debug capture saved " + path + " (" + d.Format + " " + d.Width + "x" + d.Height + ")");
                }
                ctx.UnmapSubresource(st, 0);
            }
        }

        static void LogOnce(string s)
        {
            if (logCount++ < 20) Plugin.Log(s);
        }
    }
}







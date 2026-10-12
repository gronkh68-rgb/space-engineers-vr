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
        static MyRenderDeviceSettings? origSettings;
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
                if (!settingsToTry.HasValue || XrRuntime.Instance == null || XrRuntime.Instance.Failed) return;
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
        /// <summary>
        /// Playing online as a client, the server decides where shots go from the character's head direction. While the right trigger is held
        /// (and the hands are driven) the head steering aims at the right controller's direction; null otherwise.
        /// </summary>
        static Vector3? OnlineAimDirection(XrRuntime xr)
        {
            try
            {
                if (!Plugin.Config.HandAimOnline || !xr.HandRValid || !VrHands.Driving) return null;
                if (!Sandbox.Game.Multiplayer.Sync.MultiplayerActive || Sandbox.Game.Multiplayer.Sync.IsServer) return null;
                if (VrControls.TrigRRaw < 0.4f) return null;
                var q = xr.HandR.orientation;
                return Vector3.Transform(new Vector3(0f, 0f, -1f), new Quaternion(q.x, q.y, q.z, q.w));
            }
            catch { return null; }
        }

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
                        origSettings = ds;
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
                            Active = false;
                            // give the window its normal size and VSync back (the eye-sized window would stay otherwise)
                            if (origSettings.HasValue) { try { MyRenderProxy.RenderThread.SwitchSettings(origSettings.Value); } catch (Exception e2) { Plugin.Log("VR: restoring settings failed: " + e2.Message); } }
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
                        VrHead.AimOverride = OnlineAimDirection(xr);
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

        // the desktop window can show what you see in VR: the left eye with the plugin's HUD panels where they are in the world.
        // Every panel is also kept as a small picture of its own (copied from the scratch image while it is painted).
        static readonly Texture2D[] mirrorPart = new Texture2D[XrRuntime.PartCount];
        static readonly ShaderResourceView[] mirrorSrv = new ShaderResourceView[XrRuntime.PartCount];
        static readonly int[] mirrorFrame = new int[XrRuntime.PartCount];
        static int mirrorTick;

        static void CopyToMirror(DeviceContext ctx, Texture2DDescription d, SharpDX.Direct3D11.Device dev, int part, int w, int h)
        {
            if (part < 0 || part >= mirrorPart.Length) return;
            var cur = mirrorPart[part];
            if (cur == null || cur.Description.Width != w || cur.Description.Height != h || cur.Description.Format != d.Format)
            {
                if (mirrorSrv[part] != null) mirrorSrv[part].Dispose();
                if (cur != null) cur.Dispose();
                var md = d; md.Width = w; md.Height = h; md.MipLevels = 1; md.ArraySize = 1;
                md.SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0);
                md.BindFlags = BindFlags.ShaderResource; md.Usage = ResourceUsage.Default; md.CpuAccessFlags = CpuAccessFlags.None; md.OptionFlags = ResourceOptionFlags.None;
                mirrorPart[part] = new Texture2D(dev, md);
                mirrorSrv[part] = new ShaderResourceView(dev, mirrorPart[part]);
            }
            ctx.CopySubresourceRegion(ownScratch, 0, new ResourceRegion(0, 0, 0, w, h, 1), mirrorPart[part], 0, 0, 0, 0);
            mirrorFrame[part] = mirrorTick;
        }

        /// <summary>The panels of the last frames, drawn over the saved left eye in the window. False when the desktop shows the game's flat HUD instead.</summary>
        static bool DrawMirrorPanels()
        {
            if (Plugin.Config.Mirror != "vr" || MenuState.IsMenuOpen()) return false;
            var xr = XrRuntime.Instance;
            if (xr == null) return false;
            mirrorTick++;
            var quads = new List<VrOverlay.MirrorQuad>();
            var eye0 = eyeWorldCache[0];
            for (int part = 0; part < mirrorPart.Length; part++)
            {
                if (mirrorSrv[part] == null || mirrorTick - mirrorFrame[part] > 4) continue;
                N.Pose pp; float pw, ph;
                if (!xr.TryGetShownPart(part, out pp, out pw, out ph)) continue;
                MatrixD wm = VrHead.EyeWorld(FromPose(pp), camWorldCache);
                Vector3D c = wm.Translation, rx = wm.Right * (pw * 0.5), uy = wm.Up * (ph * 0.5);
                var q = new VrOverlay.MirrorQuad { View = mirrorSrv[part], Dist = (c - eye0.Translation).Length() };
                q.Corners[0] = c - rx + uy; q.Corners[1] = c + rx + uy; q.Corners[2] = c + rx - uy; q.Corners[3] = c - rx - uy;
                quads.Add(q);
            }
            var bb = backbufferProp.GetValue(null);
            var rtv = bb.GetType().GetProperty("Rtv").GetValue(bb) as RenderTargetView;
            var back = Backbuffer();
            var dd = back.Description;
            VrOverlay.DrawPanelQuads(back.Device, Context(), rtv, dd.Width, dd.Height, eye0, projCache[0], quads);
            return true;
        }

        static void RestoreMirror()
        {
            if (mirrorTex == null || spriteStash == null) return;
            Context().CopyResource(mirrorTex, Backbuffer());
            if (DrawMirrorPanels()) return;           // the desktop shows the VR view: no flat game HUD on top
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
                EnsureLaser();
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
            VrOwnHud.LeftAim = false;
            // 1. the parts the game lays out itself: block info, components, chat, crosshair
            int[] fixedParts = { 0, 1, 5, 6 };
            var rects = new List<int[]>();       // part, x, y, w, h
            bool own = cfg.OwnHud;
            foreach (int part in fixedParts)
            {
                int x, y, w, h;
                if (cfg.Anchors[part] == "hud" || !VrHudLayout.TryRect(part, d.Width, d.Height, out x, out y, out w, out h)) continue;
                if (own && part != 6) { rects.Add(new[] { part, x, y, w, h }); continue; }
                if (part == 6 && !cfg.Crosshair) { rects.Add(new[] { part, x, y, w, h }); continue; }       // no crosshair: wiped from the picture, not shown     // the plugin draws these itself: the game's own are only wiped from the picture
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
                foreach (int part in new[] { 3, 2, 4, 5, 7, 0, 1, 8, 18, 15, 11, 22, 23 })
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

        /// <summary>The cockpit HUD (22 tool bar, 23 buttons P, Z, L, J): only while sitting in a seat or cockpit, saved per block type.</summary>
        static void RenderSeatPart(XrRuntime xr, Texture2D src, Texture2DDescription d, DeviceContext ctx, int part, float mpp)
        {
            if (VrHead.SeatKey == null)
            {
                if (part == 22) { VrOwnHud.HotbarHover = false; VrOwnHud.HoverSlot = -1; VrOwnHud.PointerU = -1f; }
                else { VrOwnHud.SeatHover = -1; }
                return;
            }
            int w, h;
            if (!VrOwnHud.Prepare(part, out w, out h)) { if (part == 22) VrOwnHud.HotbarHover = false; return; }
            w = Math.Min(w, XrRuntime.PanelW); h = Math.Min(h, XrRuntime.PanelH);
            if (part == 22)
            {
                if (!VrOwnHud.EditMode) PointAtToolbar(xr, w, h, 22);
                else { VrOwnHud.HotbarHover = false; VrOwnHud.HoverSlot = -1; VrOwnHud.PointerU = -1f; }
            }
            else PressSeatBar(xr, w, h);
            if (!PaintPanel(xr, src, d, ctx, part, w, h, () => VrOwnHud.Draw(part, w, h))) return;
            float widthM = part == 22 ? Math.Min(0.50f, w * mpp * 0.5f) : 0.23f;
            xr.SetPanel(part, w, h, "seat", widthM, true);
        }

        static readonly string[] seatPulses = { "seat_p", "seat_z", "seat_l" };
        static bool prevSeatTrig;
        static void PressSeatBar(XrRuntime xr, int w, int h)
        {
            float u, v; bool leftHand;
            bool hit = !VrOwnHud.EditMode && AimAt(xr, 23, out u, out v, out leftHand);
            if (!hit) { VrOwnHud.SeatHover = -1; prevSeatTrig = false; return; }
            AimAt(xr, 23, out u, out v, out leftHand);
            xr.SetHudLaser(xr.PartPoint(23, u, v), leftHand);
            int cell = Math.Max(0, Math.Min(2, (int)(u * 3f)));
            if (cell != VrOwnHud.SeatHover) xr.Haptic(leftHand, 0.02f, 0.35f);
            VrOwnHud.SeatHover = cell;
            bool trig = TrigDown(leftHand);
            if (trig && !prevSeatTrig) { xr.Haptic(leftHand, 0.06f, 0.8f); VrControls.ExternalPulse(seatPulses[cell]); }
            prevSeatTrig = trig;
        }

        static Texture2D ownScratch; static RenderTargetView ownScratchRtv;

        /// <summary>Draw one of the plugin's own HUD panels (status, tool bar, block info, components) straight into the part's image.</summary>
        static void RenderOwnPart(XrRuntime xr, Texture2D src, Texture2DDescription d, DeviceContext ctx, int part, string anchor, float mpp)
        {
            if (anchor == "hud") return;
            if (part == 22 || part == 23) { RenderSeatPart(xr, src, d, ctx, part, mpp); return; }
            if (part == 15)
            {
                // the help window (controls and hints), on by default; the ? button switches it
                int hw, hh;
                if (!Plugin.Config.Help || !VrOwnHud.Prepare(15, out hw, out hh)) return;
                if (PaintPanel(xr, src, d, ctx, 15, hw, hh, () => VrOwnHud.Draw(15, hw, hh))) xr.SetPanel(15, hw, hh, "help", 0.75f, true);
                return;
            }
            if (part == 2 && VrHead.SeatKey != null) { VrOwnHud.HotbarHover = false; VrOwnHud.HoverSlot = -1; VrOwnHud.PointerU = -1f; return; }        // seated: the cockpit tool bar (22) replaces it
            string mode = Plugin.Config.HudMode;
            bool wrist = mode == "wrist" && (part == 2 || part == 3 || part == 4);
            bool hand = mode == "hand" && (part == 0 || part == 1 || part == 2 || part == 3 || part == 4 || part == 5 && false || XrRuntime.IsHandButton(part));
            bool buttonPart = XrRuntime.IsHandButton(part);
            float gaze = 1f;
            if (wrist) { anchor = "lhand"; gaze = xr.GazeFactor(true); }       // wrist HUD: on the left hand, only there when you look at it
            if (hand) anchor = "handmenu";                                      // hand menu: one block on the left hand, opened with the small button
            if (buttonPart && !hand) return;
            if (part == 2 && !wrist && !hand) { VrOwnHud.HotbarHover = false; VrOwnHud.HoverSlot = -1; VrOwnHud.PointerU = -1f; }
            if (hand && !buttonPart && !VrOwnHud.MenuOpen && !VrOwnHud.EditMode) { if (part == 2) { VrOwnHud.HotbarHover = false; VrOwnHud.HoverSlot = -1; VrOwnHud.PointerU = -1f; } return; }
            if (buttonPart)
            {
                // the buttons on the back of the hand are only there while you look at the hand
                gaze = xr.GazeFactor(part != 18);
                if (gaze < 0.03f) { if (part == 8) { VrOwnHud.HoverButton = -1; VrOwnHud.ButtonHover = false; } else if (VrOwnHud.HoverButton >= 18 && VrOwnHud.HoverButton <= 20) VrOwnHud.HoverButton = -1; return; }
            }
            // A panel is repainted about 14 times a second, or at once when something it shows about the pointing changes; in between the last image is
            // shown again (the headset runtime keeps the last image of a swapchain), which saves most of the drawing and the game-data reading.
            long nowT = System.Diagnostics.Stopwatch.GetTimestamp();
            int key = PaintKey(xr, part, gaze);
            bool fresh = paintW[part] > 0 && key == paintKey[part] && nowT - paintTick[part] < (long)(RepaintSeconds * System.Diagnostics.Stopwatch.Frequency);
            int w, h;
            if (fresh) { w = paintW[part]; h = paintH[part]; }
            else
            {
                if (!VrOwnHud.Prepare(part, out w, out h)) { paintW[part] = 0; if (part == 2) VrOwnHud.HotbarHover = false; if (part == 11) VrOwnHud.StripHover = -1; return; }
                w = Math.Min(w, XrRuntime.PanelW); h = Math.Min(h, XrRuntime.PanelH);
            }
            if (wrist && gaze < 0.03f) { if (part == 2) { VrOwnHud.HotbarHover = false; VrOwnHud.HoverSlot = -1; VrOwnHud.PointerU = -1f; } return; }
            if ((wrist || hand) && part == 2 && !VrOwnHud.EditMode) PointAtToolbar(xr, w, h);
            if (part == 2 && VrOwnHud.EditMode) { VrOwnHud.HotbarHover = false; VrOwnHud.HoverSlot = -1; VrOwnHud.PointerU = -1f; }
            if (buttonPart) PressCluster(xr, part, w, h);
            if (part == 11) PressEditStrip(xr);
            if (fresh)
            {
                xr.SetPanel(part, w, h, anchor, paintWidthM[part], true);
                if (part < mirrorFrame.Length) mirrorFrame[part] = mirrorTick;
                return;
            }
            VrOverlay.OpacityMul = wrist || buttonPart ? gaze : 1f;
            if (!PaintPanel(xr, src, d, ctx, part, w, h, () => VrOwnHud.Draw(part, w, h))) { VrOverlay.OpacityMul = 1f; return; }
            VrOverlay.OpacityMul = 1f;
            float fs = Math.Max(0.5f, Plugin.Config.HudDistance) / 0.72f;       // farther away = proportionally larger, so the apparent size stays
            float widthM = buttonPart ? (part == 8 ? 0.0964f : 0.045f) : part == 11 ? 0.30f : (wrist || hand) ? Math.Min(0.9f, w * mpp * Plugin.Config.WristScale) : Math.Min(0.9f * fs, w * mpp * fs);
            xr.SetPanel(part, w, h, anchor, widthM, true);
            paintTick[part] = nowT; paintKey[part] = key; paintW[part] = w; paintH[part] = h; paintWidthM[part] = widthM;
        }

        const double RepaintSeconds = 0.07;
        static readonly long[] paintTick = new long[XrRuntime.PartCount];
        static readonly int[] paintKey = new int[XrRuntime.PartCount], paintW = new int[XrRuntime.PartCount], paintH = new int[XrRuntime.PartCount];
        static readonly float[] paintWidthM = new float[XrRuntime.PartCount];

        /// <summary>What a panel shows about the pointing and the modes: when this changes the panel is repainted at once.</summary>
        static int PaintKey(XrRuntime xr, int part, float gaze)
        {
            unchecked
            {
                int k = VrOwnHud.HoverSlot * 31 + VrOwnHud.HoverButton * 17 + VrOwnHud.StripHover * 13 + VrOwnHud.ProdMultiplier;
                k = k * 31 + (VrOwnHud.MenuOpen ? 1 : 0) + (VrOwnHud.EditMode ? 2 : 0) + (int)(gaze * 8f) * 4;
                k = k * 31 + (int)(VrOwnHud.PointerU * 40f) * 7 + (int)(VrOwnHud.PointerV * 40f);
                k = k * 31 + (xr.EditHoverPart == part ? 1 : 0) + (part == 11 ? (xr.FocusPart + 1) * 5 + xr.AttachOf(xr.FocusPart < 0 ? 0 : xr.FocusPart) : 0);
                k = k * 31 + (int)(Plugin.Config.HudOpacity * 20f);
                return k;
            }
        }

        /// <summary>
        /// Draw a panel with the given painter: into an image of our own first (the headset runtime's images are not necessarily usable as render
        /// targets), then copied into the part's image. False when the part's image could not be had.
        /// </summary>
        static bool PaintPanel(XrRuntime xr, Texture2D src, Texture2DDescription d, DeviceContext ctx, int part, int w, int h, Action paint)
        {
            Texture2D dst;
            bool real = !xr.Fake;
            int dx = 0, dy = 0;
            bool atlas = real && XrRuntime.AtlasPart(part);
            if (atlas)
            {
                string err;
                if (!xr.EnsurePanelSwapchain(part, (int)d.Format, out err)) { LogOnce("VR: " + err); return false; }
                if (atlasTex == null)
                {
                    IntPtr p0 = xr.AtlasFirstImage;
                    if (p0 == IntPtr.Zero) return false;
                    Texture2D im;
                    if (!wrappers.TryGetValue(p0, out im)) { Marshal.AddRef(p0); im = new Texture2D(p0); wrappers[p0] = im; }
                    var ad = im.Description;
                    ad.MipLevels = 1; ad.ArraySize = 1; ad.SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0);
                    ad.Usage = ResourceUsage.Default; ad.BindFlags = BindFlags.ShaderResource; ad.CpuAccessFlags = CpuAccessFlags.None; ad.OptionFlags = ResourceOptionFlags.None;
                    atlasTex = new Texture2D(src.Device, ad);
                }
                dst = atlasTex;
                XrRuntime.AtlasPos(part, out dx, out dy);
            }
            else if (real)
            {
                string err;
                if (!xr.EnsurePanelSwapchain(part, (int)d.Format, out err)) { LogOnce("VR: " + err); return false; }
                IntPtr p = xr.AcquirePanel(part);
                if (p == IntPtr.Zero) return false;
                if (!wrappers.TryGetValue(p, out dst)) { Marshal.AddRef(p); dst = new Texture2D(p); wrappers[p] = dst; }
            }
            else
            {
                if (partFake[part] == null) { var pd = d; pd.Width = XrRuntime.PanelW; pd.Height = XrRuntime.PanelH; pd.BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget; pd.Usage = ResourceUsage.Default; partFake[part] = new Texture2D(src.Device, pd); }
                dst = partFake[part];
            }
            try
            {
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
                    paint();
                    VrOverlay.End2D();
                    ctx.CopySubresourceRegion(ownScratch, 0, new ResourceRegion(0, 0, 0, w, h, 1), dst, 0, dx, dy, 0);
                    if (Plugin.Config.Mirror == "vr") CopyToMirror(ctx, d, src.Device, part, w, h);
                }
                if (clearState == null) clearState = rcProp.PropertyType.GetMethod("ClearState", All);
                if (clearState != null) clearState.Invoke(rcProp.GetValue(null), null);
            }
            catch (Exception e) { LogOnce("own hud: " + e.Message); }
            if (atlas) atlasDirty = true;
            else if (real) xr.ReleasePanel(part);
            return true;
        }

        static Texture2D atlasTex; static bool atlasDirty;
        /// <summary>Copy the atlas of the small parts into the swapchain image (once per frame, when something was painted).</summary>
        static void FlushAtlas(XrRuntime xr, DeviceContext ctx)
        {
            if (!atlasDirty || atlasTex == null) return;
            atlasDirty = false;
            IntPtr p = xr.AcquireAtlas();
            if (p == IntPtr.Zero) return;
            Texture2D im;
            if (!wrappers.TryGetValue(p, out im)) { Marshal.AddRef(p); im = new Texture2D(p); wrappers[p] = im; }
            ctx.CopyResource(atlasTex, im);
            xr.ReleaseAtlas();
        }

        /// <summary>The strip below the part that was pointed at in edit mode: size (grip, handled in the runtime), tie to the left hand, tie to the head, free.</summary>
        static void PressEditStrip(XrRuntime xr)
        {
            float u, v; bool leftHand;
            bool hit = AimAt(xr, 11, out u, out v, out leftHand);
            int btn = hit ? Math.Min(3, (int)(u * 4f)) : -1;
            if (hit && btn != VrOwnHud.StripHover) xr.Haptic(leftHand, 0.02f, 0.35f);
            VrOwnHud.StripHover = btn;
            if (hit) xr.SetHudLaser(xr.PartPoint(11, u, v), leftHand);
            bool trig = TrigDown(leftHand);
            if (hit && btn >= 1 && trig && !prevBtn[11])
            {
                int f = xr.FocusPart;
                if (f >= 0) xr.SetAttach(f, btn == 1 ? 1 : btn == 2 ? 2 : 0);
                xr.Haptic(leftHand, 0.06f, 0.8f);
            }
            prevBtn[11] = trig;
        }

        // which hand points at a part: the right one if its ray hits it, else the left one (its trigger is then the click)
        static bool AimAt(XrRuntime xr, int part, out float u, out float v, out bool left)
        {
            left = false;
            if (xr.PointAt(part, true, out u, out v)) return true;
            left = true;
            if (xr.PointAt(part, false, out u, out v)) { VrOwnHud.LeftAim = true; return true; }
            return false;
        }
        static bool AimNear(XrRuntime xr, int part, float radius, out bool left)
        {
            left = false;
            if (xr.PointNear(part, true, radius)) return true;
            left = true;
            if (xr.PointNear(part, false, radius)) { VrOwnHud.LeftAim = true; return true; }
            return false;
        }
        static bool TrigDown(bool left) { return (left ? VrControls.TrigLRaw : VrControls.TrigRRaw) > 0.6f; }

        static System.Reflection.FieldInfo termTabs; static int prodPage = -2, lastTermPage = -9; static string lastScreenName = "";

        /// <summary>The terminal (K) with its Production tab showing.</summary>
        static bool TerminalProductionOpen()
        {
            try
            {
                var s = Sandbox.Graphics.GUI.MyScreenManager.GetScreenWithFocus();
                string sn = s != null ? s.GetType().Name : "";
                if (sn != lastScreenName) { lastScreenName = sn; Plugin.Log("menu: screen with focus " + sn); }
                if (s == null || sn.IndexOf("Terminal", StringComparison.OrdinalIgnoreCase) < 0 || sn.IndexOf("Screen", StringComparison.OrdinalIgnoreCase) < 0) return false;
                if (termTabs == null)
                {
                    var tt = s.GetType();
                    termTabs = tt.GetField("m_terminalTabs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var ei = tt.GetField("m_initialPage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    try { prodPage = (int)Enum.Parse(ei.FieldType, "Production"); } catch { prodPage = -1; Plugin.Log("menu: terminal page 'Production' not found"); }
                }
                var tabs = termTabs != null ? termTabs.GetValue(s) as Sandbox.Graphics.GUI.MyGuiControlTabControl : null;
                if (tabs != null && tabs.SelectedPage != lastTermPage) { lastTermPage = tabs.SelectedPage; Plugin.Log("menu: terminal page " + lastTermPage + " (production = " + prodPage + ")"); }
                return tabs != null && prodPage >= 0 && tabs.SelectedPage == prodPage;
            }
            catch { return false; }
        }

        /// <summary>The G menu: the screen with the tool bar configuration (and block selection).</summary>
        static bool GMenuOpen()
        {
            try
            {
                var s = Sandbox.Graphics.GUI.MyScreenManager.GetScreenWithFocus();
                for (var t = s != null ? s.GetType() : null; t != null; t = t.BaseType)
                    if (t.Name.IndexOf("ToolbarConfig", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// In a menu (G, K ...): the strip below the window (size, lock, keyboard, tool bar pages), the free-floating keyboard and, in the terminal,
        /// the production multiplier (x1, x10, x100, x1000) above it.
        /// </summary>
        static void RenderMenuExtras(XrRuntime xr, Texture2D src, Texture2DDescription d, DeviceContext ctx)
        {
            try
            {
                VrOwnHud.LeftAim = false;
                VrOwnHud.GMenuOpen = GMenuOpen();
                // the strip below the window (the page arrows only in the G menu)
                {
                    int w, h; VrOwnHud.Prepare(13, out w, out h);
                    int cells = VrOwnHud.MenuStripCells;
                    float u, v; bool leftHand; bool hit = AimAt(xr, 13, out u, out v, out leftHand);
                    int cell = hit ? Math.Min(cells - 1, (int)(u * cells)) : -1;
                    if (cells == 6 && cell == 4) cell = -1;
                    if (cell >= 0 && cell != VrOwnHud.MenuStripHover) xr.Haptic(leftHand, 0.02f, 0.35f);
                    VrOwnHud.MenuStripHover = cell;
                    if (hit) xr.SetHudLaser(xr.PartPoint(13, u, v), leftHand);
                    bool trig = TrigDown(leftHand);
                    if (cell >= 0 && trig && !prevBtn[13])
                    {
                        xr.Haptic(leftHand, 0.06f, 0.8f);
                        if (cell == 1) { Plugin.Config.MenuLocked = !Plugin.Config.MenuLocked; try { Plugin.SaveConfig(); } catch { } }
                        else if (cell == 2) VrKeyboard.Shown = !VrKeyboard.Shown;
                        else if (cell == 3 || cell == 5)
                        {
                            bool next = cell == 5;
                            Sandbox.MySandboxGame.Static.Invoke(() =>
                            {
                                try { var tb = Sandbox.Game.Screens.Helpers.MyToolbarComponent.CurrentToolbar; if (tb != null) { if (next) tb.PageUp(); else tb.PageDown(); } }
                                catch (Exception e) { Plugin.Log("menu page: " + e.Message); }
                            }, "SeVr");
                        }
                    }
                    prevBtn[13] = trig;
                    if (PaintPanel(xr, src, d, ctx, 13, w, h, () => VrOwnHud.Draw(13, w, h))) xr.SetPanel(13, w, h, "menu", w * 0.000625f, true);
                }
                // the production multiplier, in the terminal's Production tab only
                if (TerminalProductionOpen())
                {
                    int w, h; VrOwnHud.Prepare(14, out w, out h);
                    float u, v; bool leftHand; bool hit = AimAt(xr, 14, out u, out v, out leftHand);
                    int cell = hit ? Math.Min(3, (int)(u * 4f)) : -1;
                    if (cell >= 0 && cell != VrOwnHud.ProdHover) xr.Haptic(leftHand, 0.02f, 0.35f);
                    VrOwnHud.ProdHover = cell;
                    if (hit) xr.SetHudLaser(xr.PartPoint(14, u, v), leftHand);
                    bool trig = TrigDown(leftHand);
                    if (cell >= 0 && trig && !prevBtn[14])
                    {
                        xr.Haptic(leftHand, 0.06f, 0.8f);
                        int val = VrOwnHud.ProdValues[cell];
                        VrOwnHud.ProdMultiplier = VrOwnHud.ProdMultiplier == val ? 1 : val;       // one at a time; the same again = back to x1
                    }
                    prevBtn[14] = trig;
                    if (PaintPanel(xr, src, d, ctx, 14, w, h, () => VrOwnHud.Draw(14, w, h))) xr.SetPanel(14, w, h, "menu", 0.40f, true);
                }
                else { VrOwnHud.ProdMultiplier = 1; VrOwnHud.ProdHover = -1; }
                // the keyboard
                if (VrKeyboard.Shown)
                {
                    int w = 1408, h = (int)Math.Round(1408.0 * VrKeyboard.BmpH / VrKeyboard.BmpW);
                    if (PaintPanel(xr, src, d, ctx, 12, w, h, () => VrOverlay.DrawKeyboard(src.Device, ctx, ownScratchRtv, w, h, new System.Drawing.RectangleF(0, 0, 1, 1), VrKeyboard.Hover, VrKeyboard.PressedFlash)))
                        xr.SetPanel(12, w, h, "menu", 0.62f, true);
                }
            }
            catch (Exception e) { LogOnce("menu extras: " + e.Message); }
        }

        static bool prevHotbarTrigger;

        /// <summary>
        /// The small buttons on the back of the left hand (8 menu, 9 edit, 10 reset): point at one with the right hand and pull the trigger.
        /// The menu button opens and closes the hand menu, the edit button switches edit mode (and keeps the layout when it is switched off),
        /// the reset button puts all moved parts back.
        /// </summary>
        static void PressCluster(XrRuntime xr, int part, int w, int h)
        {
            // the left cluster is pressed by the right hand (the left ray runs right past it all the time), the right cluster by the left hand
            bool leftHand = part == 18;
            float u, v;
            int id = -1;
            if (xr.PointAt(part, !leftHand, out u, out v)) id = VrOwnHud.ClusterCell(part, u * w, v * h);
            if (id >= 0)
            {
                if (leftHand) VrOwnHud.LeftAim = true;
                xr.SetHudLaser(xr.PartPoint(part, u, v), leftHand);
                if (VrOwnHud.HoverButton != id) xr.Haptic(leftHand, 0.02f, 0.35f);
                VrOwnHud.HoverButton = id;
            }
            else if (VrOwnHud.HoverButton >= 0 && (VrOwnHud.HoverButton >= 18 && VrOwnHud.HoverButton <= 20) == leftHand) VrOwnHud.HoverButton = -1;
            VrOwnHud.ButtonHover = VrOwnHud.HoverButton >= 0 && !(VrOwnHud.HoverButton >= 18 && VrOwnHud.HoverButton <= 20);
            bool trig = TrigDown(leftHand);
            if (id >= 0 && trig && !prevBtn[part])
            {
                xr.Haptic(leftHand, 0.06f, 0.8f);
                if (id == 8) VrOwnHud.MenuOpen = !VrOwnHud.MenuOpen;
                else if (id == 9)
                {
                    VrOwnHud.EditMode = !VrOwnHud.EditMode;
                    if (VrOwnHud.EditMode) VrOwnHud.MenuOpen = true;
                    else { try { Plugin.SaveConfig(); } catch { } }
                }
                else if (id == 10) xr.ResetLayout();
                else if (id == 22) { Plugin.Config.Help = !Plugin.Config.Help; try { Plugin.SaveConfig(); } catch { } }
                else VrControls.ExternalPulse(id == 15 ? "btn_k" : id == 16 ? "btn_g" : id == 17 ? "btn_i" : id == 21 ? "btn_chat" : id == 18 ? "btn_f10" : id == 19 ? "btn_af10" : "btn_sf10");
            }
            prevBtn[part] = trig;
        }
        static readonly bool[] prevBtn = new bool[XrRuntime.PartCount];

        /// <summary>The right hand points at the wrist tool bar: highlight the slot under the ray; the trigger picks it (instead of using the tool).</summary>
        static void PointAtToolbar(XrRuntime xr, int w, int h, int part = 2)
        {
            float u, v; bool leftHand;
            bool hit = AimAt(xr, part, out u, out v, out leftHand);
            if (hit) xr.SetHudLaser(xr.PartPoint(part, u, v), leftHand);
            int slot = hit ? VrOwnHud.SlotAt(u * w, v * h, w, VrOwnHud.SlotCount) : -1;
            if (slot >= 0 && slot != VrOwnHud.HoverSlot) xr.Haptic(leftHand, 0.02f, 0.35f);
            VrOwnHud.HoverSlot = slot;
            VrOwnHud.PointerU = hit ? u : -1f; VrOwnHud.PointerV = hit ? v : -1f;
            VrOwnHud.HotbarHover = hit;
            bool trig = TrigDown(leftHand);
            if (hit && slot >= 0 && trig && !prevHotbarTrigger)
            {
                int s = slot;
                xr.Haptic(leftHand, 0.05f, 0.8f);
                Sandbox.MySandboxGame.Static.Invoke(() =>
                {
                    try
                    {
                        var tb = Sandbox.Game.Screens.Helpers.MyToolbarComponent.CurrentToolbar;
                        if (tb == null) return;
                        if (s == VrOwnHud.PagePrevCode) tb.PageDown();               // previous tool bar page
                        else if (s == VrOwnHud.PageNextCode) tb.PageUp();            // next tool bar page
                        else if (s == VrOwnHud.SlotCount) tb.Unselect(true);         // the empty-hand slot (key 0)
                        else tb.ActivateItemAtSlot(s, false, true, true);
                    }
                    catch (Exception e) { Plugin.Log("hotbar pick: " + e.Message); }
                }, "SeVr");
            }
            prevHotbarTrigger = trig;
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
            if (MenuState.IsMenuOpen()) { RenderMenuExtras(xr, src, d, ctx); FlushAtlas(xr, ctx); }
            else { VrOwnHud.ProdMultiplier = 1; VrOwnHud.MenuStripHover = -1; VrOwnHud.ProdHover = -1; }      // closing the window ends the production multiplier
            if (!MenuState.IsMenuOpen())
            {
                ClearMarkerRects(Context(), rtv, d); ExtractParts(xr, src, rtv, d); FlushAtlas(xr, ctx);
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
            SaveTex(fakeComposite, Plugin.Tag + "_capture.png", false);
            if (hudFake != null) SaveTex(hudFake, Plugin.Tag + "_hud.png", true);
            for (int pi = 0; pi < partFake.Length; pi++) if (partFake[pi] != null) SaveTex(partFake[pi], Plugin.Tag + "_part" + pi + ".png", true);
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
                    var path = System.IO.Path.Combine(Plugin.DataDir(), fileName);
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







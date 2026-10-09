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
                if (Active) ApplyEye(currentEye);
            }
            catch (Exception e) { LogOnce("SetCameraPostfix: " + e); }
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
                        Matrix hl = FromPose(xr.Views[0].pose);
                        Vector3 mid = (FromPose(xr.Views[0].pose).Translation + FromPose(xr.Views[1].pose).Translation) * 0.5f;
                        VrHead.BeginFrame(cw, hl, mid, dt, VrControls.TurnX, VrControls.TurnY);
                    }
                    VrBuild.Update();
                    ApplyEye(0);
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
                if (xr.HandLValid) hl = VrHead.EyeWorld(FromPose(xr.HandL), camWorldCache);
                if (xr.HandRValid) hr = VrHead.EyeWorld(FromPose(xr.HandR), camWorldCache);
                var marks = VrMarkers.Snapshot();
                if (Environment.GetEnvironmentVariable("SEVR_NOMARK") == "1") marks.Clear();
                if (xr.Fake)   // test markers: one 6 m ahead, one far to the right
                {
                    marks.Add(new VrMarkers.Marker { Pos = camWorldCache.Translation + camWorldCache.Forward * 6.0 + camWorldCache.Up * 0.3, Text = "Test marker", Color = System.Drawing.Color.FromArgb(255, 117, 201, 241) });
                    marks.Add(new VrMarkers.Marker { Pos = camWorldCache.Translation + camWorldCache.Forward * 30.0 + camWorldCache.Right * 25.0, Text = "Far GPS", Color = System.Drawing.Color.FromArgb(255, 255, 200, 60) });
                }
                VrOverlay.DrawEye(dev, Context(), rtv, d.Width, d.Height, eyeWorldCache[eye], projCache[eye], marks, !VrHands.Driving || Environment.GetEnvironmentVariable("SEVR_BLOCKHANDS") == "1", hl, hr, VrControls.TrigL, VrControls.TrigR);
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
                if (inSecondPass) return false;

                var taskField = tRender11.GetField("m_mainSpritesTask", All);
                object task = taskField.GetValue(null);
                var validF = task.GetType().GetField("valid");
                if (!(bool)validF.GetValue(task)) return false;
                var wait = task.GetType().GetMethod("Wait", All);
                wait.Invoke(task, wait.GetParameters().Length == 1 ? new object[] { true } : new object[0]);
                object fc = mainSpritesFc.GetValue(null);
                var cl = fc.GetType().GetField("CommandList").GetValue(fc) as CommandList;
                if (spriteStash != null) { spriteStash.Dispose(); spriteStash = null; }
                if (cl != null)
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
        static void DebugCapture()
        {
            if (captured || !ranSecondPass || ++capFrames < 900) return;
            captured = true;
            SaveTex(fakeComposite, "SeVr_capture.png", false);
            if (hudFake != null) SaveTex(hudFake, "SeVr_hud.png", true);
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

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SeVr.Generated;
using SeVr.Vr;

namespace SeVr.Xr
{
    /// <summary>Everything OpenXR: instance, D3D11 session, swapchain, frame loop, controller actions.</summary>
    internal sealed unsafe class XrRuntime
    {
        public static XrRuntime Instance { get; private set; }

        ulong instance, systemId, session, localSpace, viewSpace, swapchain;
        
        int sessionState;
        public bool SessionRunning { get; private set; }
        public bool Failed { get; private set; }
        int xrLog; bool loggedFirstFrame; int endLog;
        public string FailReason = "";

        public uint EyeWidth, EyeHeight;
        public N.View[] Views = new N.View[2];
        public bool ViewsValid;
        public long DisplayTime;
        bool frameBegun;
        N.FrameState frameState;

        // swapchain
        IntPtr[] scImages = new IntPtr[0];
        public long ScFormat;
        public uint ScWidth, ScHeight;

        // actions
        ulong actionSet;
        readonly Dictionary<string, ulong> actions = new Dictionary<string, ulong>();
        readonly Dictionary<string, string> actionTypes = new Dictionary<string, string>();
        ulong handSpaceL, handSpaceR, aimActionL, aimActionR;
        public N.Pose HandL, HandR;
        public bool HandLValid, HandRValid;
        public readonly VirtualPad Pad = new VirtualPad();

        long lastInputTicks; float fakeT;
        public float FrameDt = 0.011f;
        public bool FrameProfile;         // the Steam Frame controller profile extension is enabled
        public bool Fake;                 // SEVR_FAKE=1: no headset, synthetic eyes, used to test the stereo renderer

        public static void CreateFake()
        {
            var x = new XrRuntime { Fake = true, EyeWidth = 1440, EyeHeight = 1600 };
            for (int i = 0; i < 2; i++)
            {
                x.Views[i].pose.orientation.w = 1f;
                x.Views[i].pose.position.x = i == 0 ? -0.032f : 0.032f;
                x.Views[i].fov = i == 0 ? new N.Fov { left = -0.9f, right = 0.8f, up = 0.9f, down = -0.9f }
                                        : new N.Fov { left = -0.8f, right = 0.9f, up = 0.9f, down = -0.9f };
            }
            x.ViewsValid = true;
            Instance = x;
        }

        public static bool TryCreateInstance(out string error)
        {
            error = null;
            try
            {
                var x = new XrRuntime();
                if (!x.CreateInstanceAndSystem(out error)) return false;
                Instance = x;
                return true;
            }
            catch (Exception e) { error = e.ToString(); return false; }
        }

        bool CreateInstanceAndSystem(out string error)
        {
            error = null;
            var info = new N.InstanceCreateInfo { type = N.T.InstanceCreateInfo };
            N.Str(info.applicationInfo.applicationName, 128, "Space Engineers VR");
            N.Str(info.applicationInfo.engineName, 128, "VRage");
            info.applicationInfo.applicationVersion = 1;
            info.applicationInfo.engineVersion = 1;
            info.applicationInfo.apiVersion = 1UL << 48; // XR_MAKE_VERSION(1,0,0)

            // XR_VALVE_frame_controller_interaction gives the Steam Frame controllers their own profile (D-pad, bumpers, view button);
            // runtimes without it get the plain D3D11 extension only.
            byte** extPtr = (byte**)Marshal.AllocHGlobal(IntPtr.Size * 2);
            extPtr[0] = (byte*)Marshal.StringToHGlobalAnsi("XR_KHR_D3D11_enable");
            extPtr[1] = (byte*)Marshal.StringToHGlobalAnsi("XR_VALVE_frame_controller_interaction");
            info.enabledExtensionCount = 2;
            info.enabledExtensionNames = extPtr;
            int r = N.xrCreateInstance(ref info, out instance);
            if (r == -9) { info.enabledExtensionCount = 1; r = N.xrCreateInstance(ref info, out instance); }   // XR_ERROR_EXTENSION_NOT_PRESENT
            FrameProfile = r == N.XR_SUCCESS && info.enabledExtensionCount == 2;
            if (r != N.XR_SUCCESS) { error = "xrCreateInstance failed (" + r + "). Is an OpenXR runtime (SteamVR, Oculus, Virtual Desktop, WMR) active?"; return false; }

            var sys = new N.SystemGetInfo { type = N.T.SystemGetInfo, formFactor = 1 };
            r = N.xrGetSystem(instance, ref sys, out systemId);
            if (r != N.XR_SUCCESS) { error = "xrGetSystem failed (" + r + "). Is the headset connected / streaming?"; return false; }

            N.ViewConfigurationView* v = stackalloc N.ViewConfigurationView[2];
            v[0].type = N.T.ViewConfigurationView; v[1].type = N.T.ViewConfigurationView;
            r = N.xrEnumerateViewConfigurationViews(instance, systemId, N.ViewConfigPrimaryStereo, 2, out uint n, v);
            if (r != N.XR_SUCCESS || n < 2) { error = "No stereo view configuration (" + r + ")"; return false; }
            EyeWidth = v[0].recommendedImageRectWidth;
            EyeHeight = v[0].recommendedImageRectHeight;
            return true;
        }

        /// <summary>Create the D3D11 session. Must be called on the render thread with the game's ID3D11Device.</summary>
        public bool CreateSession(IntPtr d3d11Device, out string error)
        {
            error = null;
            if (!CallRequirements(out error)) return false;
            var bind = new N.GraphicsBindingD3D11 { type = N.T.GraphicsBindingD3D11, device = d3d11Device };
            var ci = new N.SessionCreateInfo { type = N.T.SessionCreateInfo, next = &bind, systemId = systemId };
            int r = N.xrCreateSession(instance, ref ci, out session);
            if (r != N.XR_SUCCESS) { error = "xrCreateSession failed (" + r + ")"; return false; }

            var rs = new N.ReferenceSpaceCreateInfo { type = N.T.ReferenceSpaceCreateInfo, referenceSpaceType = N.SpaceLocal };
            rs.poseInReferenceSpace.orientation.w = 1f;
            r = N.xrCreateReferenceSpace(session, ref rs, out localSpace);
            if (r != N.XR_SUCCESS) { error = "xrCreateReferenceSpace failed (" + r + ")"; return false; }

            var vsi = new N.ReferenceSpaceCreateInfo { type = N.T.ReferenceSpaceCreateInfo, referenceSpaceType = N.SpaceView };
            vsi.poseInReferenceSpace.orientation.w = 1f;
            N.xrCreateReferenceSpace(session, ref vsi, out viewSpace);

            if (!CreateActions(out error)) return false;
            return true;
        }

        bool CallRequirements(out string error)
        {
            error = null;
            int r = N.xrGetInstanceProcAddr(instance, "xrGetD3D11GraphicsRequirementsKHR", out IntPtr fn);
            if (r != N.XR_SUCCESS || fn == IntPtr.Zero) { error = "Runtime lacks XR_KHR_D3D11_enable"; return false; }
            var del = (N.GetD3D11Requirements)Marshal.GetDelegateForFunctionPointer(fn, typeof(N.GetD3D11Requirements));
            var req = new N.GraphicsRequirementsD3D11 { type = N.T.GraphicsRequirementsD3D11 };
            r = del(instance, systemId, ref req);
            if (r != N.XR_SUCCESS) { error = "xrGetD3D11GraphicsRequirementsKHR failed (" + r + ")"; return false; }
            return true;
        }

        ulong Path(string s) { N.xrStringToPath(instance, s, out ulong p); return p; }

        bool CreateActions(out string error)
        {
            error = null;
            var si = new N.ActionSetCreateInfo { type = N.T.ActionSetCreateInfo, priority = 0 };
            N.Str(si.actionSetName, 64, "gameplay");
            N.Str(si.localizedActionSetName, 128, "Gameplay");
            int r = N.xrCreateActionSet(instance, ref si, out actionSet);
            if (r != N.XR_SUCCESS) { error = "xrCreateActionSet failed (" + r + ")"; return false; }

            // one action per sheet row; its binding path is translated for every controller family
            var rowActions = new List<KeyValuePair<InputRow, ulong>>();
            foreach (var row in InputMap.Rows)
            {
                if (string.IsNullOrEmpty(row.Path) && (string.IsNullOrEmpty(row.FramePath) || row.FramePath == "-")) continue;
                int type = row.Type == "bool" ? 1 : row.Type == "float" ? 2 : 3;
                ulong a = NewAction(row.XrAction, type, row.XrAction);
                if (a == 0) { error = "xrCreateAction " + row.XrAction; return false; }
                actions[row.XrAction] = a;
                actionTypes[row.XrAction] = row.Type;
                rowActions.Add(new KeyValuePair<InputRow, ulong>(row, a));
            }
            aimActionL = NewAction("aim_left", 4, "Aim left");
            aimActionR = NewAction("aim_right", 4, "Aim right");

            string[] profiles = {
                "oculus/touch_controller", "meta/touch_controller_plus", "meta/touch_controller_pro", "bytedance/pico4_controller",
                "valve/index_controller", "microsoft/motion_controller", "hp/mixed_reality_controller", "khr/simple_controller", "valve/frame_controller_valve" };
            foreach (var profile in profiles)
            {
                if (profile == "valve/frame_controller_valve" && !FrameProfile) continue;
                var list = new List<N.ActionSuggestedBinding>();
                foreach (var kv in rowActions)
                {
                    string p = MapPath(profile, kv.Key);
                    if (p != null) list.Add(new N.ActionSuggestedBinding { action = kv.Value, binding = Path(p) });
                }
                list.Add(new N.ActionSuggestedBinding { action = aimActionL, binding = Path("/user/hand/left/input/aim/pose") });
                list.Add(new N.ActionSuggestedBinding { action = aimActionR, binding = Path("/user/hand/right/input/aim/pose") });
                Suggest("/interaction_profiles/" + profile, list);
            }

            ulong set = actionSet;
            var att = new N.SessionActionSetsAttachInfo { type = N.T.SessionActionSetsAttachInfo, countActionSets = 1, actionSets = &set };
            r = N.xrAttachSessionActionSets(session, ref att);
            if (r != N.XR_SUCCESS) { error = "xrAttachSessionActionSets failed (" + r + ")"; return false; }

            var sp = new N.ActionSpaceCreateInfo { type = N.T.ActionSpaceCreateInfo, action = aimActionL };
            sp.poseInActionSpace.orientation.w = 1f;
            N.xrCreateActionSpace(session, ref sp, out handSpaceL);
            sp.action = aimActionR;
            N.xrCreateActionSpace(session, ref sp, out handSpaceR);
            return true;
        }

        /// <summary>Translate a sheet row's Touch-layout path to another controller family's path, or null when it has no such control.</summary>
        static string MapPath(string profile, InputRow row)
        {
            if (profile == "valve/frame_controller_valve")
            {
                if (row.FramePath == "-") return null;
                return string.IsNullOrEmpty(row.FramePath) ? (string.IsNullOrEmpty(row.Path) ? null : row.Path) : row.FramePath;
            }
            if (string.IsNullOrEmpty(row.Path)) return null;
            string p = row.Path;
            bool left = row.Hand == "left";
            switch (profile)
            {
                case "oculus/touch_controller":
                case "meta/touch_controller_plus":
                case "meta/touch_controller_pro":
                case "bytedance/pico4_controller":
                    return p;
                case "valve/index_controller":
                    if (p.Contains("/menu/")) return null;
                    if (left) return p.Replace("/input/x/", "/input/a/").Replace("/input/y/", "/input/b/");
                    return p;
                case "microsoft/motion_controller":
                case "hp/mixed_reality_controller":
                    if (p.Contains("/a/") || p.Contains("/b/") || p.Contains("/x/") || p.Contains("/y/")) return null;
                    return p.Replace("/squeeze/value", "/squeeze/click");
                case "khr/simple_controller":
                    if (p.EndsWith("/trigger/value")) return p.Replace("/trigger/value", "/select/click");
                    if (p.EndsWith("/menu/click")) return p;
                    return null;
            }
            return null;
        }

        ulong NewAction(string name, int type, string localized)
        {
            var ai = new N.ActionCreateInfo { type = N.T.ActionCreateInfo, actionType = type };
            N.Str(ai.actionName, 64, name);
            N.Str(ai.localizedActionName, 128, localized);
            return N.xrCreateAction(actionSet, ref ai, out ulong a) == N.XR_SUCCESS ? a : 0;
        }

        void Suggest(string profile, List<N.ActionSuggestedBinding> b)
        {
            if (b.Count == 0) return;
            var arr = b.ToArray();
            fixed (N.ActionSuggestedBinding* p = arr)
            {
                var s = new N.InteractionProfileSuggestedBinding
                {
                    type = N.T.InteractionProfileSuggestedBinding,
                    interactionProfile = Path(profile),
                    countSuggestedBindings = (uint)arr.Length,
                    suggestedBindings = p
                };
                N.xrSuggestInteractionProfileBindings(instance, ref s);
            }
        }

        void PollEvents()
        {
            while (true)
            {
                var ev = new N.EventDataBuffer { type = N.T.EventDataBuffer };
                if (N.xrPollEvent(instance, ref ev) != N.XR_SUCCESS) break;
                if ((int)ev.type == (int)N.T.EventDataSessionStateChanged)
                {
                    var sc = *(N.EventDataSessionStateChanged*)&ev;
                    sessionState = sc.state;
                    Plugin.Log("XR: session state " + sc.state);
                    if (sessionState == N.SessionReady && !SessionRunning)
                    {
                        var bi = new N.SessionBeginInfo { type = N.T.SessionBeginInfo, primaryViewConfigurationType = N.ViewConfigPrimaryStereo };
                        if (N.xrBeginSession(session, ref bi) == N.XR_SUCCESS) SessionRunning = true;
                    }
                    else if (sessionState == N.SessionStopping && SessionRunning)
                    {
                        N.xrEndSession(session); SessionRunning = false;
                    }
                    else if (sessionState == N.SessionExiting || sessionState == N.SessionLossPending)
                    {
                        SessionRunning = false; Failed = true; FailReason = "OpenXR session ended (" + sessionState + ")";
                    }
                }
            }
        }

        /// <summary>Start of a render frame: wait/begin, locate eyes, read controllers.</summary>
        public bool BeginFrame()
        {
            if (Fake) { FakeMotion(); return true; }
            if (Failed || session == 0) return false;
            PollEvents();
            if (!SessionRunning) return false;
            if (frameBegun) return ViewsValid; // Present never ran for last frame; keep going

            var wi = new N.FrameWaitInfo { type = N.T.FrameWaitInfo };
            frameState = new N.FrameState { type = N.T.FrameState };
            int rw = N.xrWaitFrame(session, ref wi, ref frameState);
            if (rw != N.XR_SUCCESS) { if (xrLog++ < 10) Plugin.Log("XR: xrWaitFrame " + rw); return false; }
            var bi = new N.FrameBeginInfo { type = N.T.FrameBeginInfo };
            int rb = N.xrBeginFrame(session, ref bi);
            if (rb < 0) { if (xrLog++ < 10) Plugin.Log("XR: xrBeginFrame " + rb); return false; }
            frameBegun = true;
            DisplayTime = frameState.predictedDisplayTime;

            var li = new N.ViewLocateInfo { type = N.T.ViewLocateInfo, viewConfigurationType = N.ViewConfigPrimaryStereo, displayTime = DisplayTime, space = localSpace };
            var vs = new N.ViewState { type = N.T.ViewState };
            N.View* v = stackalloc N.View[2];
            v[0].type = N.T.View; v[1].type = N.T.View;
            int r = N.xrLocateViews(session, ref li, ref vs, 2, out uint cnt, v);
            ViewsValid = r == N.XR_SUCCESS && cnt == 2 && (vs.viewStateFlags & 3) == 3;
            if (!ViewsValid && xrLog++ < 10) Plugin.Log("XR: xrLocateViews result " + r + " count " + cnt + " flags " + vs.viewStateFlags);
            if (ViewsValid && !loggedFirstFrame) { loggedFirstFrame = true; Plugin.Log("XR: first frame, shouldRender=" + frameState.shouldRender + " fovL=" + Views[0].fov.left + "/" + Views[0].fov.right + " pos " + v[0].pose.position.x + "," + v[0].pose.position.y + "," + v[0].pose.position.z); }
            if (ViewsValid) { Views[0] = v[0]; Views[1] = v[1]; }
            SyncInput();
            if (ViewsValid) UpdatePointer();
            return ViewsValid;
        }

        // SEVR_FAKE: synthetic head motion so the head-steering loop can be tested without a headset (SEVR_FAKEMOVE=1)
        void FakeMotion()
        {
            fakeT += 0.011f;
            HandL = new N.Pose { orientation = new N.Quat { w = 1f }, position = new N.Vec3 { x = -0.22f, y = -0.28f, z = (Environment.GetEnvironmentVariable("SEVR_FAKEREACH") == "1") ? -0.80f : -0.45f } }; HandLValid = true;
            HandR = new N.Pose { orientation = new N.Quat { w = 1f }, position = new N.Vec3 { x = 0.22f, y = -0.28f, z = (Environment.GetEnvironmentVariable("SEVR_FAKEREACH") == "1") ? -0.80f : -0.45f } }; HandRValid = true;
            if (Environment.GetEnvironmentVariable("SEVR_FAKEMOVE") != "1") return;
            float yaw = 0.7f * (float)Math.Sin(fakeT * 0.6f), pitch = 0.3f * (float)Math.Sin(fakeT * 0.45f);
            float sy = (float)Math.Sin(yaw / 2), cy = (float)Math.Cos(yaw / 2), sp = (float)Math.Sin(pitch / 2), cp = (float)Math.Cos(pitch / 2);
            // q = qyaw * qpitch
            var q = new N.Quat { x = cy * sp, y = sy * cp, z = -sy * sp, w = cy * cp };
            for (int i = 0; i < 2; i++) Views[i].pose.orientation = q;
        }

        void SyncInput()
        {
            ulong set = actionSet;
            var act = new N.ActiveActionSet { actionSet = set };
            var si = new N.ActionsSyncInfo { type = N.T.ActionsSyncInfo, countActiveActionSets = 1, activeActionSets = &act };
            if (N.xrSyncActions(session, ref si) < 0) return;

            VrControls.BeginFrameInput();
            foreach (var row in InputMap.Rows)
            {
                if (!actions.TryGetValue(row.XrAction, out ulong a)) continue;
                var gi = new N.ActionStateGetInfo { type = N.T.ActionStateGetInfo, action = a };
                if (row.Type == "bool")
                {
                    var s = new N.ActionStateBoolean { type = N.T.ActionStateBoolean };
                    if (N.xrGetActionStateBoolean(session, ref gi, ref s) == 0 && s.isActive != 0) VrControls.SetBool(row.Target, s.currentState != 0);
                }
                else if (row.Type == "float")
                {
                    var s = new N.ActionStateFloat { type = N.T.ActionStateFloat };
                    if (N.xrGetActionStateFloat(session, ref gi, ref s) == 0 && s.isActive != 0) VrControls.SetFloat(row.Target, s.currentState);
                }
                else
                {
                    var s = new N.ActionStateVector2f { type = N.T.ActionStateVector2f };
                    if (N.xrGetActionStateVector2f(session, ref gi, ref s) == 0 && s.isActive != 0) VrControls.SetStick(row.Target, s.x, s.y);
                }
            }

            long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            float dtInput = lastInputTicks == 0 ? 0.011f : (float)(nowTicks - lastInputTicks) / System.Diagnostics.Stopwatch.Frequency;
            lastInputTicks = nowTicks;
            VrControls.EndFrameInput(Math.Min(dtInput, 0.1f));

            var loc = new N.SpaceLocation { type = N.T.SpaceLocation };
            if (handSpaceL != 0 && N.xrLocateSpace(handSpaceL, localSpace, DisplayTime, ref loc) == 0 && (loc.locationFlags & 3) == 3) { HandL = loc.pose; HandLValid = true; } else HandLValid = false;
            loc = new N.SpaceLocation { type = N.T.SpaceLocation };
            if (handSpaceR != 0 && N.xrLocateSpace(handSpaceR, localSpace, DisplayTime, ref loc) == 0 && (loc.locationFlags & 3) == 3) { HandR = loc.pose; HandRValid = true; } else HandRValid = false;
        }


        // ---------------------------------------------------------------- swapchains / submission

        // eye image: two eye views side by side. HUD image: the game's HUD alone, shown on a quad.
        ulong hudSwapchain; IntPtr[] hudImages = new IntPtr[0]; uint hudW, hudH;
        public float HudWidthDeg = 62f, HudYOffset = 0f;
        float HudSizeMeters() { return 2f * HudDistance * (float)Math.Tan(HudWidthDeg * 0.5f * 0.017453292f); }
        public bool HudReady;                 // set by the renderer when this frame has a HUD image
        public float HudWidthMeters, HudHeightMeters, HudDistance = 1.5f;

        long PickFormat(int backbufferDxgiFormat)
        {
            N.xrEnumerateSwapchainFormats(session, 0, out uint fc, null);
            long[] fmts = new long[fc];
            fixed (long* p = fmts) N.xrEnumerateSwapchainFormats(session, fc, out fc, p);
            foreach (long f in fmts) if (f == backbufferDxgiFormat) return f;
            foreach (long f in fmts) foreach (long g in Family(backbufferDxgiFormat)) if (f == g) return f;
            return 0;
        }

        bool CreateSc(uint width, uint height, long format, out ulong handle, out IntPtr[] images, out string error)
        {
            error = null; images = new IntPtr[0];
            var sci = new N.SwapchainCreateInfo
            {
                type = N.T.SwapchainCreateInfo, usageFlags = 1 | 0x10 | 0x20, format = format,
                sampleCount = 1, width = width, height = height, faceCount = 1, arraySize = 1, mipCount = 1
            };
            int r = N.xrCreateSwapchain(session, ref sci, out handle);
            if (r != N.XR_SUCCESS) { error = "xrCreateSwapchain failed (" + r + ")"; return false; }
            N.xrEnumerateSwapchainImages(handle, 0, out uint ic, null);
            var imgs = new N.SwapchainImageD3D11[ic];
            for (int i = 0; i < ic; i++) imgs[i].type = N.T.SwapchainImageD3D11;
            fixed (N.SwapchainImageD3D11* p = imgs) N.xrEnumerateSwapchainImages(handle, ic, out ic, p);
            images = new IntPtr[ic];
            for (int i = 0; i < ic; i++) images[i] = imgs[i].texture;
            return true;
        }

        public bool EnsureSwapchain(uint width, uint height, int backbufferDxgiFormat, out string error)
        {
            error = null;
            if (swapchain != 0 && ScWidth == width && ScHeight == height) return true;
            if (swapchain != 0) { N.xrDestroySwapchain(swapchain); swapchain = 0; }
            long chosen = PickFormat(backbufferDxgiFormat);
            if (chosen == 0) { error = "No compatible swapchain format for backbuffer DXGI " + backbufferDxgiFormat; return false; }
            if (!CreateSc(width, height, chosen, out swapchain, out scImages, out error)) return false;
            ScFormat = chosen; ScWidth = width; ScHeight = height;
            Plugin.Log("XR: eye swapchain " + width + "x" + height + " format " + chosen);
            return true;
        }

        public bool EnsureHudSwapchain(uint width, uint height, int backbufferDxgiFormat, out string error)
        {
            error = null;
            if (hudSwapchain != 0 && hudW == width && hudH == height) return true;
            if (hudSwapchain != 0) { N.xrDestroySwapchain(hudSwapchain); hudSwapchain = 0; }
            long chosen = PickFormat(backbufferDxgiFormat);
            if (chosen == 0) { error = "No compatible HUD swapchain format"; return false; }
            if (!CreateSc(width, height, chosen, out hudSwapchain, out hudImages, out error)) return false;
            hudW = width; hudH = height;
            Plugin.Log("XR: HUD swapchain " + width + "x" + height + " format " + chosen);
            return true;
        }

        static long[] Family(long f)
        {
            if (f == 28 || f == 29 || f == 27) return new long[] { 29, 28 };
            if (f == 87 || f == 91 || f == 90) return new long[] { 91, 87 };
            return new long[0];
        }

        IntPtr Acquire(ulong sc, IntPtr[] images)
        {
            var ai = new N.SwapchainImageAcquireInfo { type = N.T.SwapchainImageAcquireInfo };
            if (N.xrAcquireSwapchainImage(sc, ref ai, out uint idx) < 0) return IntPtr.Zero;
            var wi = new N.SwapchainImageWaitInfo { type = N.T.SwapchainImageWaitInfo, timeout = long.MaxValue };
            if (N.xrWaitSwapchainImage(sc, ref wi) < 0) return IntPtr.Zero;
            return images[idx];
        }

        void Release(ulong sc)
        {
            var ri = new N.SwapchainImageReleaseInfo { type = N.T.SwapchainImageReleaseInfo };
            N.xrReleaseSwapchainImage(sc, ref ri);
        }

        public IntPtr AcquireImage() { return Acquire(swapchain, scImages); }
        public void ReleaseImage() { Release(swapchain); }
        public IntPtr AcquireHud() { return Acquire(hudSwapchain, hudImages); }
        public void ReleaseHud() { Release(hudSwapchain); }

        static N.SwapchainSubImage Sub(ulong sc, int x, int w, int h)
        {
            return new N.SwapchainSubImage
            {
                swapchain = sc,
                imageRect = new N.Rect2Di { offset = new N.Offset2Di { x = x, y = 0 }, extent = new N.Extent2Di { width = w, height = h } }
            };
        }

        /// <summary>Submit the frame. stereoWorld: both eye views (plus the HUD quad); otherwise the left image as a floating flat screen (menus).</summary>
        public void EndFrame(bool stereoWorld, float menuDistance, float menuWidthMeters)
        {
            if (!frameBegun) return;
            frameBegun = false;
            var fe = new N.FrameEndInfo { type = N.T.FrameEndInfo, displayTime = DisplayTime, environmentBlendMode = 1 };

            if (frameState.shouldRender == 0 || !ViewsValid || swapchain == 0)
            {
                LogEnd(N.xrEndFrame(session, ref fe));
                return;
            }

            void** layers = stackalloc void*[3];
            N.CompositionLayerProjectionView* pv = stackalloc N.CompositionLayerProjectionView[2];
            N.CompositionLayerProjection proj = new N.CompositionLayerProjection();
            N.CompositionLayerQuad quad = new N.CompositionLayerQuad();
            N.CompositionLayerQuad hud = new N.CompositionLayerQuad();
            N.CompositionLayerQuad laserQuad = new N.CompositionLayerQuad();
            int half = (int)(ScWidth / 2);
            uint n = 0;

            if (stereoWorld)
            {
                for (int i = 0; i < 2; i++)
                    pv[i] = new N.CompositionLayerProjectionView
                    {
                        type = N.T.CompositionLayerProjectionView, pose = Views[i].pose, fov = Views[i].fov,
                        subImage = Sub(swapchain, i == 0 ? 0 : half, half, (int)ScHeight)
                    };
                proj = new N.CompositionLayerProjection { type = N.T.CompositionLayerProjection, space = localSpace, viewCount = 2, views = pv };
                layers[n++] = &proj;

                if (HudReady && hudSwapchain != 0)
                {
                    if (MenuMode)
                    {
                        // a menu is open: the game's GUI becomes a floating screen in the world (the world stays visible around it)
                        menuW = menuWidthMeters; menuH = menuWidthMeters * hudH / hudW;
                        if (!menuLocked) LockMenu(menuDistance);
                        hud = new N.CompositionLayerQuad
                        {
                            type = N.T.CompositionLayerQuad, layerFlags = 2 | 4, space = localSpace, eyeVisibility = 0,
                            subImage = Sub(hudSwapchain, 0, (int)hudW, (int)hudH),
                            pose = menuPose, size = new N.Extent2Df { width = menuW, height = menuH }
                        };
                        layers[n++] = &hud;
                        if (BuildLaser(out laserQuad)) layers[n++] = &laserQuad;
                    }
                    else
                    {
                        // head-locked HUD (crosshair in the middle, bars at the bottom); GPS markers are drawn in the world instead
                        hud = new N.CompositionLayerQuad
                        {
                            type = N.T.CompositionLayerQuad, layerFlags = 2 | 4, space = viewSpace != 0 ? viewSpace : localSpace, eyeVisibility = 0,
                            subImage = Sub(hudSwapchain, 0, (int)hudW, (int)hudH),
                            pose = new N.Pose { orientation = new N.Quat { w = 1f }, position = new N.Vec3 { x = 0, y = HudYOffset, z = -HudDistance } },
                            size = new N.Extent2Df { width = HudSizeMeters(), height = HudSizeMeters() }
                        };
                        layers[n++] = &hud;
                    }
                }
                fe.layerCount = n; fe.layers = layers;
                LogEnd(N.xrEndFrame(session, ref fe));
            }
            else
            {
                // Menu: the left image on a floating screen that stays where it opened, so it can be pointed at with the laser.
                float aspect = (float)half / ScHeight;
                menuW = menuWidthMeters; menuH = menuWidthMeters / aspect;
                if (!menuLocked) LockMenu(menuDistance);
                quad = new N.CompositionLayerQuad
                {
                    type = N.T.CompositionLayerQuad, space = localSpace, eyeVisibility = 0,
                    subImage = Sub(swapchain, 0, half, (int)ScHeight),
                    pose = menuPose,
                    size = new N.Extent2Df { width = menuW, height = menuH }
                };
                layers[n++] = &quad;

                if (PointerValid && laserSwapchain != 0 && laserReady)
                {
                    // thin quad from the hand to the point on the screen, turned towards the viewer
                    N.Vec3 a = LaserFrom, b = LaserTo;
                    N.Vec3 dir = Sub3(b, a); float len = Len3(dir);
                    if (len > 0.05f)
                    {
                        dir = Mul3(dir, 1f / len);
                        N.Vec3 mid2 = Mul3(Add3(a, b), 0.5f);
                        N.Vec3 eye = Mul3(Add3(Views[0].pose.position, Views[1].pose.position), 0.5f);
                        N.Vec3 z = Sub3(eye, mid2); z = Sub3(z, Mul3(dir, Dot3(z, dir)));
                        if (Len3(z) < 1e-4f) z = new N.Vec3 { z = 1f };
                        z = Mul3(z, 1f / Len3(z));
                        N.Vec3 x = Cross3(dir, z);
                        laserQuad = new N.CompositionLayerQuad
                        {
                            type = N.T.CompositionLayerQuad, layerFlags = 2 | 4, space = localSpace, eyeVisibility = 0,
                            subImage = Sub(laserSwapchain, 0, 4, 4),
                            pose = new N.Pose { orientation = FromBasis(x, dir, z), position = mid2 },
                            size = new N.Extent2Df { width = 0.006f, height = len }
                        };
                        layers[n++] = &laserQuad;
                    }
                }
                fe.layerCount = n; fe.layers = layers;
                LogEnd(N.xrEndFrame(session, ref fe));
            }
        }

        // ---------------------------------------------------------------- menu screen, laser pointer

        public bool MenuMode;                  // set by the renderer: a menu has focus
        public bool PointerValid;              // the right controller's ray hits the menu screen
        public float PointerU, PointerV;       // hit point on the screen, 0..1 from the top left
        public N.Vec3 LaserFrom, LaserTo;
        N.Pose menuPose; bool menuLocked; float menuW = 1.8f, menuH = 1.8f;
        ulong laserSwapchain; IntPtr[] laserImages = new IntPtr[0]; public bool laserReady;

        static N.Vec3 Add3(N.Vec3 a, N.Vec3 b) { return new N.Vec3 { x = a.x + b.x, y = a.y + b.y, z = a.z + b.z }; }
        static N.Vec3 Sub3(N.Vec3 a, N.Vec3 b) { return new N.Vec3 { x = a.x - b.x, y = a.y - b.y, z = a.z - b.z }; }
        static N.Vec3 Mul3(N.Vec3 a, float s) { return new N.Vec3 { x = a.x * s, y = a.y * s, z = a.z * s }; }
        static float Dot3(N.Vec3 a, N.Vec3 b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
        static float Len3(N.Vec3 a) { return (float)Math.Sqrt(Dot3(a, a)); }
        static N.Vec3 Cross3(N.Vec3 a, N.Vec3 b) { return new N.Vec3 { x = a.y * b.z - a.z * b.y, y = a.z * b.x - a.x * b.z, z = a.x * b.y - a.y * b.x }; }

        static N.Vec3 Rotate(N.Quat q, N.Vec3 v)
        {
            var u = new N.Vec3 { x = q.x, y = q.y, z = q.z };
            var t = Mul3(Cross3(u, v), 2f);
            return Add3(Add3(v, Mul3(t, q.w)), Cross3(u, t));
        }

        // quaternion of the rotation whose columns are the given basis vectors (x right, y up, z back)
        static N.Quat FromBasis(N.Vec3 x, N.Vec3 y, N.Vec3 z)
        {
            float m00 = x.x, m01 = y.x, m02 = z.x, m10 = x.y, m11 = y.y, m12 = z.y, m20 = x.z, m21 = y.z, m22 = z.z;
            float tr = m00 + m11 + m22; N.Quat q = new N.Quat();
            if (tr > 0f) { float s = (float)Math.Sqrt(tr + 1f) * 2f; q.w = 0.25f * s; q.x = (m21 - m12) / s; q.y = (m02 - m20) / s; q.z = (m10 - m01) / s; }
            else if (m00 > m11 && m00 > m22) { float s = (float)Math.Sqrt(1f + m00 - m11 - m22) * 2f; q.w = (m21 - m12) / s; q.x = 0.25f * s; q.y = (m01 + m10) / s; q.z = (m02 + m20) / s; }
            else if (m11 > m22) { float s = (float)Math.Sqrt(1f + m11 - m00 - m22) * 2f; q.w = (m02 - m20) / s; q.x = (m01 + m10) / s; q.y = 0.25f * s; q.z = (m12 + m21) / s; }
            else { float s = (float)Math.Sqrt(1f + m22 - m00 - m11) * 2f; q.w = (m10 - m01) / s; q.x = (m02 + m20) / s; q.y = (m12 + m21) / s; q.z = 0.25f * s; }
            return q;
        }

        bool BuildLaser(out N.CompositionLayerQuad q)
        {
            q = new N.CompositionLayerQuad();
            if (!PointerValid || laserSwapchain == 0 || !laserReady) return false;
            N.Vec3 a = LaserFrom, b = LaserTo;
            N.Vec3 dir = Sub3(b, a); float len = Len3(dir);
            if (len < 0.05f) return false;
            dir = Mul3(dir, 1f / len);
            N.Vec3 mid2 = Mul3(Add3(a, b), 0.5f);
            N.Vec3 eye = Mul3(Add3(Views[0].pose.position, Views[1].pose.position), 0.5f);
            N.Vec3 z = Sub3(eye, mid2); z = Sub3(z, Mul3(dir, Dot3(z, dir)));
            if (Len3(z) < 1e-4f) z = new N.Vec3 { z = 1f };
            z = Mul3(z, 1f / Len3(z));
            N.Vec3 x = Cross3(dir, z);
            q = new N.CompositionLayerQuad
            {
                type = N.T.CompositionLayerQuad, layerFlags = 2 | 4, space = localSpace, eyeVisibility = 0,
                subImage = Sub(laserSwapchain, 0, 4, 4),
                pose = new N.Pose { orientation = FromBasis(x, dir, z), position = mid2 },
                size = new N.Extent2Df { width = 0.006f, height = len }
            };
            return true;
        }

        /// <summary>Place the menu screen in front of the head (upright, facing it) and keep it there until the menu closes.</summary>
        void LockMenu(float distance)
        {
            var hp = Mul3(Add3(Views[0].pose.position, Views[1].pose.position), 0.5f);
            var f = Rotate(Views[0].pose.orientation, new N.Vec3 { z = -1f });
            float yaw = (float)Math.Atan2(-f.x, -f.z);
            var q = new N.Quat { x = 0, y = (float)Math.Sin(yaw / 2), z = 0, w = (float)Math.Cos(yaw / 2) };
            var fwd = Rotate(q, new N.Vec3 { z = -1f });
            menuPose = new N.Pose { orientation = q, position = new N.Vec3 { x = hp.x + fwd.x * distance, y = hp.y, z = hp.z + fwd.z * distance } };
            menuLocked = true;
        }

        /// <summary>Where the right controller points on the locked menu screen (called every frame while a menu is open).</summary>
        void UpdatePointer()
        {
            PointerValid = false;
            if (!MenuMode) { menuLocked = false; return; }
            if (!menuLocked || !HandRValid) return;
            var o = HandR.position;
            var d = Rotate(HandR.orientation, new N.Vec3 { z = -1f });
            var n = Rotate(menuPose.orientation, new N.Vec3 { z = 1f });
            float denom = Dot3(d, n);
            if (Math.Abs(denom) < 1e-4f) return;
            float t = Dot3(Sub3(menuPose.position, o), n) / denom;
            if (t <= 0.05f || t > 6f) return;
            var hit = Add3(o, Mul3(d, t));
            var rel = Sub3(hit, menuPose.position);
            float x = Dot3(rel, Rotate(menuPose.orientation, new N.Vec3 { x = 1f }));
            float y = Dot3(rel, Rotate(menuPose.orientation, new N.Vec3 { y = 1f }));
            float u = x / menuW + 0.5f, v = 0.5f - y / menuH;
            LaserFrom = Add3(o, Mul3(d, 0.04f)); LaserTo = hit;
            if (u < 0f || u > 1f || v < 0f || v > 1f) return;
            PointerU = u; PointerV = v; PointerValid = true;
        }

        public bool EnsureLaserSwapchain(int dxgiFormat, out string error)
        {
            error = null;
            if (laserSwapchain != 0) return true;
            long fmt = PickFormat(dxgiFormat);
            if (fmt == 0) { error = "no laser format"; return false; }
            return CreateSc(4, 4, fmt, out laserSwapchain, out laserImages, out error);
        }
        public IntPtr AcquireLaser() { return Acquire(laserSwapchain, laserImages); }
        public void ReleaseLaser() { Release(laserSwapchain); laserReady = true; }

        void LogEnd(int r) { if (r != 0 && endLog++ < 10) Plugin.Log("XR: xrEndFrame " + r); }

        public void Shutdown()
        {
            try
            {
                if (swapchain != 0) N.xrDestroySwapchain(swapchain); if (hudSwapchain != 0) N.xrDestroySwapchain(hudSwapchain);
                if (session != 0) { if (SessionRunning) N.xrEndSession(session); N.xrDestroySession(session); }
                if (instance != 0) N.xrDestroyInstance(instance);
            }
            catch { }
            swapchain = 0; session = 0; instance = 0; SessionRunning = false;
        }
    }
}

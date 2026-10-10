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
        ulong handSpaceL, handSpaceR, aimActionL, aimActionR, hapticL, hapticR;
        public string ActiveProfile = "";    // interaction profile the runtime is using for the controllers
        int profileCheck;

        /// <summary>True for the Steam Frame controllers (D-pad, bumpers, X/Y on the right). With Touch-style controllers the sticks stand in for the D-pad.</summary>
        public bool IsFrameController { get { var c = Plugin.Config.Controller; if (c == "frame") return true; if (c == "touch") return false; return ActiveProfile.Length > 0 ? ActiveProfile.Contains("frame_controller") : FrameProfile; } }

        /// <summary>A short vibration of one controller (render thread).</summary>
        public void Haptic(bool left, float seconds, float amplitude)
        {
            ulong a = left ? hapticL : hapticR;
            if (a == 0 || session == 0 || Fake || !SessionRunning) return;
            var info = new N.HapticActionInfo { type = N.T.HapticActionInfo, action = a };
            var vib = new N.HapticVibration { type = N.T.HapticVibration, duration = (long)(seconds * 1e9), frequency = 0f, amplitude = amplitude };
            N.xrApplyHapticFeedback(session, ref info, ref vib);
        }

        void CheckProfile()
        {
            if (session == 0 || (profileCheck++ % 120) != 0) return;
            var st = new N.InteractionProfileState { type = (N.T)53 };
            if (N.xrGetCurrentInteractionProfile(session, Path("/user/hand/right"), ref st) != N.XR_SUCCESS || st.interactionProfile == 0) return;
            byte* buf = stackalloc byte[256];
            if (N.xrPathToString(instance, st.interactionProfile, 256, out uint n, buf) != N.XR_SUCCESS) return;
            string s = System.Text.Encoding.ASCII.GetString(buf, (int)Math.Max(0, n - 1));
            if (s != ActiveProfile) { ActiveProfile = s; Plugin.Log("XR: controller profile " + s); }
        }
        public N.Pose HandL, HandR;
        public bool HandLValid, HandRValid;

        // grip pose = where the real hand holds the controller (the aim pose is at the front, for pointing)
        ulong gripActionL, gripActionR, gripSpaceL, gripSpaceR;
        public N.Pose GripPoseL, GripPoseR;
        public bool GripLValid, GripRValid;

        /// <summary>Pose for the character's hands: the aim orientation (calibrated for the hand model) at the position of the real hand.</summary>
        public N.Pose BodyHandL { get { var p = HandL; if (Plugin.Config.HandsAtGrip && GripLValid) p.position = GripPoseL.position; return p; } }
        public N.Pose BodyHandR { get { var p = HandR; if (Plugin.Config.HandsAtGrip && GripRValid) p.position = GripPoseR.position; return p; } }
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
            gripActionL = NewAction("grip_pose_left", 4, "Grip left");
            gripActionR = NewAction("grip_pose_right", 4, "Grip right");
            hapticL = NewAction("haptic_left", 100, "Haptic left");     // 100 = vibration output
            hapticR = NewAction("haptic_right", 100, "Haptic right");

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
                if (profile != "valve/frame_controller_valve" && gripActionL != 0 && gripActionR != 0)
                {
                    list.Add(new N.ActionSuggestedBinding { action = gripActionL, binding = Path("/user/hand/left/input/grip/pose") });
                    list.Add(new N.ActionSuggestedBinding { action = gripActionR, binding = Path("/user/hand/right/input/grip/pose") });
                }
                if (hapticL != 0 && hapticR != 0)
                {
                    var withHaptic = new List<N.ActionSuggestedBinding>(list);
                    withHaptic.Add(new N.ActionSuggestedBinding { action = hapticL, binding = Path("/user/hand/left/output/haptic") });
                    withHaptic.Add(new N.ActionSuggestedBinding { action = hapticR, binding = Path("/user/hand/right/output/haptic") });
                    if (profile == "valve/frame_controller_valve")
                    {
                        // not sure the Frame profile has this output path: try it, and fall back to the old bindings if the runtime refuses
                        int sr = Suggest("/interaction_profiles/" + profile, withHaptic);
                        Plugin.Log("XR: Steam Frame haptic binding " + (sr == N.XR_SUCCESS ? "accepted" : "refused (" + sr + ")"));
                        if (sr != N.XR_SUCCESS) Suggest("/interaction_profiles/" + profile, list);
                        continue;
                    }
                    list = withHaptic;
                }
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
            if (gripActionL != 0 && gripActionR != 0)
            {
                sp.action = gripActionL; N.xrCreateActionSpace(session, ref sp, out gripSpaceL);
                sp.action = gripActionR; N.xrCreateActionSpace(session, ref sp, out gripSpaceR);
            }
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

        int Suggest(string profile, List<N.ActionSuggestedBinding> b)
        {
            if (b.Count == 0) return N.XR_SUCCESS;
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
                return N.xrSuggestInteractionProfileBindings(instance, ref s);
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

            // hands first: the body zones in EndFrameInput need this frame's hand positions
            var loc = new N.SpaceLocation { type = N.T.SpaceLocation };
            if (handSpaceL != 0 && N.xrLocateSpace(handSpaceL, localSpace, DisplayTime, ref loc) == 0 && (loc.locationFlags & 3) == 3) { HandL = loc.pose; HandLValid = true; } else HandLValid = false;
            loc = new N.SpaceLocation { type = N.T.SpaceLocation };
            if (handSpaceR != 0 && N.xrLocateSpace(handSpaceR, localSpace, DisplayTime, ref loc) == 0 && (loc.locationFlags & 3) == 3) { HandR = loc.pose; HandRValid = true; } else HandRValid = false;
            loc = new N.SpaceLocation { type = N.T.SpaceLocation };
            if (gripSpaceL != 0 && N.xrLocateSpace(gripSpaceL, localSpace, DisplayTime, ref loc) == 0 && (loc.locationFlags & 3) == 3) { GripPoseL = loc.pose; GripLValid = true; } else GripLValid = false;
            loc = new N.SpaceLocation { type = N.T.SpaceLocation };
            if (gripSpaceR != 0 && N.xrLocateSpace(gripSpaceR, localSpace, DisplayTime, ref loc) == 0 && (loc.locationFlags & 3) == 3) { GripPoseR = loc.pose; GripRValid = true; } else GripRValid = false;
            CheckProfile();

            long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            float dtInput = lastInputTicks == 0 ? 0.011f : (float)(nowTicks - lastInputTicks) / System.Diagnostics.Stopwatch.Frequency;
            lastInputTicks = nowTicks;
            VrControls.EndFrameInput(Math.Min(dtInput, 0.1f));
        }


        // ---------------------------------------------------------------- swapchains / submission

        // eye image: two eye views side by side. HUD image: the game's HUD alone, shown on a quad.
        ulong hudSwapchain; IntPtr[] hudImages = new IntPtr[0]; uint hudW, hudH;
        public float HudWidthDeg = 62f, HudYOffset = 0f;
        float HudSizeMeters() { return 2f * HudDistance * (float)Math.Tan(HudWidthDeg * 0.5f * 0.017453292f); }
        public bool HudReady;                 // set by the renderer when this frame has a HUD image

        // HUD parts shown on the body instead of in the flat HUD window. Each is cut out of the HUD image by the renderer and floats at its
        // own anchor: next to a hand (stacked above each other when several share a hand), at the belt, or on the chest.
        // part: 0 block info head, 1 component list, 2 hotbar, 3 status bars, 4 gravity and warnings, 5 chat
        public const float HudDepth = 30f;      // metres: distance of the HUD picture (markers sit in about the right direction for both eyes)
        public bool HudLocked;                  // the HUD picture stays where the head was when it was drawn
        public N.Pose HudLockedPose;            // that head pose (tracking space)
        public float HudTanX = 1f, HudTanY = 1f;
        public const int PartCount = 10;        // 0 info, 1 components, 5 chat, 6 crosshair (always head-locked), 2 3 4 7 8 9: the pieces found in the rest of the HUD
        public const int PanelW = 1536, PanelH = 1024;
        sealed class PanelImg { public ulong Sc; public IntPtr[] Images = new IntPtr[0]; public bool Ready; public int W, H; public string Anchor = "hud"; public float WidthM = 0.26f; public bool Premult; }
        readonly PanelImg[] panels = MakePanels();
        static PanelImg[] MakePanels() { var a = new PanelImg[PartCount]; for (int i = 0; i < PartCount; i++) a[i] = new PanelImg(); return a; }
        public static readonly int[] ClusterSlots = { 3, 2, 4, 7, 8, 9 };      // the pieces of the HUD, from left to right
        static readonly int[] RowOrder = { 7, 0, 1, 3, 4, 5, 8, 9, 2 };      // left to right in a bottom row
        // 0 status, indicators and chat (under the tool bar), 1 tool bar, 2 block info and components, 3 notifications
        static int RowOf(int part) { return part == 2 ? 1 : (part == 0 || part == 1) ? 2 : part == 7 ? 3 : part == 5 ? 5 : 0; }      // 5: the chat, placed on its own
        static readonly int[] StackOrder = { 3, 4, 7, 8, 9, 0, 1, 5, 2 };     // bottom to top when several parts share a hand

        public bool EnsurePanelSwapchain(int part, int backbufferDxgiFormat, out string error)
        {
            error = null;
            var p = panels[part];
            if (p.Sc != 0) return true;
            long chosen = PickFormat(backbufferDxgiFormat);
            if (chosen == 0) { error = "No compatible panel swapchain format"; return false; }
            if (!CreateSc((uint)PanelW, (uint)PanelH, chosen, out p.Sc, out p.Images, out error)) return false;
            Plugin.Log("XR: panel swapchain " + part + " " + PanelW + "x" + PanelH);
            return true;
        }
        public IntPtr AcquirePanel(int part) { return Acquire(panels[part].Sc, panels[part].Images); }
        public void ReleasePanel(int part) { Release(panels[part].Sc); }
        public void SetPanel(int part, int w, int h, string anchor, float widthMeters, bool premultiplied = false) { var p = panels[part]; p.W = w; p.H = h; p.Anchor = anchor; p.WidthM = widthMeters; p.Premult = premultiplied; p.Ready = true; }

        // which hand an anchor means (the other one when that hand is not tracked); false for body anchors
        bool AnchorHand(string anchor, out bool left)
        {
            left = anchor != "rhand";
            if (anchor != "lhand" && anchor != "rhand") return false;
            bool ok = left ? HandLValid : HandRValid;
            if (!ok) { left = !left; ok = left ? HandLValid : HandRValid; }
            return ok;
        }

        float PartHeight(PanelImg p) { return p.H * (p.WidthM / Math.Max(1, p.W)); }

        /// <summary>Orientation of the head: the two eyes' orientations averaged (displays can be turned outwards a little, one eye alone is not straight ahead).</summary>
        public N.Quat HeadOrientation()
        {
            var a = Views[0].pose.orientation; var b = Views[1].pose.orientation;
            float dot = a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
            float s = dot < 0f ? -1f : 1f;
            var q = new N.Quat { x = a.x + b.x * s, y = a.y + b.y * s, z = a.z + b.z * s, w = a.w + b.w * s };
            float l = (float)Math.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (l < 1e-6f) return a;
            q.x /= l; q.y /= l; q.z /= l; q.w /= l;
            return q;
        }

        /// <summary>Head position and the yaw-only frame of where it looks (right, forward), in tracking space.</summary>
        void BodyFrame(out N.Vec3 head, out N.Vec3 right, out N.Vec3 fwd)
        {
            head = Mul3(Add3(Views[0].pose.position, Views[1].pose.position), 0.5f);
            var f = Rotate(HeadOrientation(), new N.Vec3 { z = -1f });
            float yaw = (float)Math.Atan2(f.x, -f.z);
            if (f.x * f.x + f.z * f.z < 0.09f) yaw = lastBodyYaw; else lastBodyYaw = yaw;
            yaw -= Plugin.Config.HudYaw * 0.017453292f;      // HudYaw: degrees, positive turns the whole HUD to the left
            float sy = (float)Math.Sin(yaw), cy = (float)Math.Cos(yaw);
            right = new N.Vec3 { x = cy, z = sy };
            fwd = new N.Vec3 { x = sy, z = -cy };
        }
        float lastBodyYaw;

        /// <summary>Pose of a part: near its hand (turned towards the head) or fixed on the body.</summary>
        bool BuildPartPose(int part, out N.Pose pose)
        {
            pose = new N.Pose();
            var me = panels[part];
            float hm = PartHeight(me);
            N.Vec3 head = Mul3(Add3(Views[0].pose.position, Views[1].pose.position), 0.5f);
            N.Vec3 pos;
            bool left;
            if (me.Anchor == "view")
            {
                // head-locked, straight ahead (the crosshair)
                var hq = HeadOrientation();
                var fv = Rotate(hq, new N.Vec3 { z = -1f });
                float yo = Plugin.Config.HudYaw * 0.017453292f;
                if (yo != 0f)
                {
                    float cy0 = (float)Math.Cos(yo), sy0 = (float)Math.Sin(yo);
                    fv = new N.Vec3 { x = fv.x * cy0 + fv.z * sy0, y = fv.y, z = -fv.x * sy0 + fv.z * cy0 };
                }
                pose = new N.Pose { orientation = hq, position = Add3(head, Mul3(fv, HudDepth)) };
                return true;
            }
            if (AnchorHand(me.Anchor, out left))
            {
                float up = 0.07f;
                foreach (int i in StackOrder)
                {
                    if (i == part) break;
                    var o = panels[i];
                    bool ol;
                    if (!o.Ready || o.Sc == 0 || !AnchorHand(o.Anchor, out ol) || ol != left) continue;
                    up += PartHeight(o) + 0.01f;
                }
                up += hm / 2f;
                N.Vec3 hp = left ? BodyHandL.position : BodyHandR.position;
                N.Vec3 toHead = Sub3(head, hp); toHead.y = 0f;
                float l = Len3(toHead);
                toHead = l > 1e-3f ? Mul3(toHead, 1f / l) : new N.Vec3 { z = 1f };
                pos = Add3(Add3(hp, new N.Vec3 { y = up }), Mul3(toHead, 0.07f));
            }
            else if (me.Anchor == "bottom" || me.Anchor == "bottom2")
            {
                // three rows of panels on a circle below the view, each row centred as a group (the tool bar alone in the lowest, then status
                // and indicators, then block info, components and chat), left to right in the order of RowOrder
                N.Vec3 h, right, fwd;
                BodyFrame(out h, out right, out fwd);
                // HudDistance: how far away the HUD floats; positions and panel sizes grow with it, so everything keeps its apparent size and place
                float R = Math.Max(0.5f, Plugin.Config.HudDistance), fs = R / 0.72f;
                const float gap = 0.03f;
                if (part == 5)
                {
                    // the chat: upper left of the view, its right edge about 9 degrees to the left, its bottom edge at eye level (it grows upwards)
                    float ac = -(0.16f + me.WidthM / R * 0.5f);
                    float cy = 0.0f + hm / 2f;
                    pos = Add3(Add3(h, Mul3(right, (float)Math.Sin(ac) * R)), Add3(new N.Vec3 { y = cy }, Mul3(fwd, (float)Math.Cos(ac) * R)));
                    goto placed;
                }
                int myRow = RowOf(part);
                float total = 0f, start = 0f;
                bool found = false;
                foreach (int i in RowOrder)
                {
                    var o = panels[i];
                    if (!o.Ready || o.Sc == 0 || RowOf(i) != myRow || !(o.Anchor == "bottom" || o.Anchor == "bottom2")) continue;
                    if (i == part) { found = true; start = total; }
                    total += o.WidthM / R + gap;
                }
                if (!found) return false;
                float a = -(total - gap) * 0.5f + start + me.WidthM / R * 0.5f;
                float rowBottom = (myRow == 0 ? -0.95f : myRow == 1 ? -0.67f : myRow == 2 ? -0.45f : -0.18f) * fs;
                float rowY = rowBottom + hm / 2f;                              // rows sit on their bottom edge, tall panels grow upwards
                pos = Add3(Add3(h, Mul3(right, (float)Math.Sin(a) * R)), Add3(new N.Vec3 { y = rowY }, Mul3(fwd, (float)Math.Cos(a) * R)));
            }
            else
            {
                N.Vec3 h, right, fwd;
                BodyFrame(out h, out right, out fwd);
                float ox = 0f, oy = -0.55f, oz = 0.30f;                   // belt
                if (me.Anchor == "chestl") { ox = -0.20f; oy = -0.30f; oz = 0.36f; }
                else if (me.Anchor == "chestr") { ox = 0.20f; oy = -0.30f; oz = 0.36f; }
                pos = Add3(Add3(h, Mul3(right, ox)), Add3(new N.Vec3 { y = oy }, Mul3(fwd, oz)));
            }
        placed:
            N.Vec3 z = Sub3(head, pos); float zl = Len3(z); if (zl < 1e-3f) return false;
            z = Mul3(z, 1f / zl);
            N.Vec3 x = Cross3(new N.Vec3 { y = 1f }, z); float xl = Len3(x); if (xl < 1e-3f) return false;
            x = Mul3(x, 1f / xl);
            N.Vec3 y = Cross3(z, x);
            pose = new N.Pose { orientation = FromBasis(x, y, z), position = pos };
            return true;
        }
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

            void** layers = stackalloc void*[20];
            N.CompositionLayerQuad* pq = stackalloc N.CompositionLayerQuad[PartCount];
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
                        if (HudLocked)
                        {
                            // The rest of the HUD (what is not a marker and not a body part): a picture in front of the head, far away, covering the
                            // field of view the HUD was drawn for.
                            hud = new N.CompositionLayerQuad
                            {
                                type = N.T.CompositionLayerQuad, layerFlags = 2 | 4, space = viewSpace != 0 ? viewSpace : localSpace, eyeVisibility = 0,
                                subImage = Sub(hudSwapchain, 0, (int)hudW, (int)hudH),
                                pose = new N.Pose { orientation = new N.Quat { w = 1f }, position = new N.Vec3 { x = 0, y = 0, z = -HudDepth } },
                                size = new N.Extent2Df { width = 2f * HudDepth * HudTanX, height = 2f * HudDepth * HudTanY }
                            };
                        }
                        else
                        {
                            // head-locked HUD window (no marker data yet)
                            hud = new N.CompositionLayerQuad
                            {
                                type = N.T.CompositionLayerQuad, layerFlags = 2 | 4, space = viewSpace != 0 ? viewSpace : localSpace, eyeVisibility = 0,
                                subImage = Sub(hudSwapchain, 0, (int)hudW, (int)hudH),
                                pose = new N.Pose { orientation = new N.Quat { w = 1f }, position = new N.Vec3 { x = 0, y = HudYOffset, z = -HudDistance } },
                                size = new N.Extent2Df { width = HudSizeMeters(), height = HudSizeMeters() }
                            };
                        }
                        layers[n++] = &hud;

                        for (int part = 0; part < PartCount; part++)
                        {
                            var pn = panels[part];
                            N.Pose pp;
                            if (!pn.Ready || pn.Sc == 0 || pn.W <= 0 || pn.H <= 0 || !BuildPartPose(part, out pp)) continue;
                            pq[part] = new N.CompositionLayerQuad
                            {
                                type = N.T.CompositionLayerQuad, layerFlags = pn.Premult ? 2UL : 6UL, space = localSpace, eyeVisibility = 0,
                                subImage = Sub(pn.Sc, 0, pn.W, pn.H),
                                pose = pp,
                                size = new N.Extent2Df { width = pn.WidthM, height = PartHeight(pn) }
                            };
                            layers[n++] = &pq[part];
                        }
                    }
                }
                for (int part = 0; part < PartCount; part++) panels[part].Ready = false;
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





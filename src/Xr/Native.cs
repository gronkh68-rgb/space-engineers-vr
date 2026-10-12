using System;
using System.Runtime.InteropServices;
using System.Text;

namespace SeVr.Xr
{
    // Minimal hand-written OpenXR 1.0 bindings. Structure layouts follow openxr.h (Khronos Group).
    internal static unsafe class N
    {
        const string L = "openxr_loader";

        public const int XR_SUCCESS = 0;
        public const int XR_EVENT_UNAVAILABLE = 4;

        public enum T : int
        {
            InstanceCreateInfo = 3, SystemGetInfo = 4, SystemProperties = 5, ViewLocateInfo = 6, View = 7,
            SessionCreateInfo = 8, SwapchainCreateInfo = 9, SessionBeginInfo = 10, ViewState = 11, FrameEndInfo = 12,
            HapticVibration = 13, EventDataBuffer = 16, EventDataSessionStateChanged = 18,
            ActionStateBoolean = 23, ActionStateFloat = 24, ActionStateVector2f = 25, ActionStatePose = 27,
            ActionSetCreateInfo = 28, ActionCreateInfo = 29, FrameWaitInfo = 33, CompositionLayerProjection = 35,
            CompositionLayerQuad = 36, ReferenceSpaceCreateInfo = 37, ActionSpaceCreateInfo = 38,
            ViewConfigurationView = 41, SpaceLocation = 42, FrameState = 44, FrameBeginInfo = 46,
            CompositionLayerProjectionView = 48, InteractionProfileSuggestedBinding = 51,
            SwapchainImageAcquireInfo = 55, SwapchainImageWaitInfo = 56, SwapchainImageReleaseInfo = 57,
            ActionStateGetInfo = 58, HapticActionInfo = 59, SessionActionSetsAttachInfo = 60, ActionsSyncInfo = 61,
            GraphicsBindingD3D11 = 1000027000, SwapchainImageD3D11 = 1000027001, GraphicsRequirementsD3D11 = 1000027002,
        }

        public const int SessionIdle = 1, SessionReady = 2, SessionSynchronized = 3, SessionVisible = 4, SessionFocused = 5,
            SessionStopping = 6, SessionLossPending = 7, SessionExiting = 8;
        public const int ViewConfigPrimaryStereo = 2;
        public const int SpaceView = 1, SpaceLocal = 2, SpaceStage = 3;

        [StructLayout(LayoutKind.Sequential)] public struct Quat { public float x, y, z, w; }
        [StructLayout(LayoutKind.Sequential)] public struct Vec3 { public float x, y, z; }
        [StructLayout(LayoutKind.Sequential)] public struct Pose { public Quat orientation; public Vec3 position; }
        [StructLayout(LayoutKind.Sequential)] public struct Fov { public float left, right, up, down; }
        [StructLayout(LayoutKind.Sequential)] public struct Extent2Di { public int width, height; }
        [StructLayout(LayoutKind.Sequential)] public struct Extent2Df { public float width, height; }
        [StructLayout(LayoutKind.Sequential)] public struct Offset2Di { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] public struct Rect2Di { public Offset2Di offset; public Extent2Di extent; }

        [StructLayout(LayoutKind.Sequential)]
        public struct ApplicationInfo
        {
            public fixed byte applicationName[128]; public uint applicationVersion;
            public fixed byte engineName[128]; public uint engineVersion; public ulong apiVersion;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct InstanceCreateInfo
        {
            public T type; public void* next; public ulong createFlags; public ApplicationInfo applicationInfo;
            public uint enabledApiLayerCount; public byte** enabledApiLayerNames;
            public uint enabledExtensionCount; public byte** enabledExtensionNames;
        }
        [StructLayout(LayoutKind.Sequential)] public struct SystemGetInfo { public T type; public void* next; public int formFactor; }
        [StructLayout(LayoutKind.Sequential)] public struct GraphicsBindingD3D11 { public T type; public void* next; public IntPtr device; }
        [StructLayout(LayoutKind.Sequential)] public struct GraphicsRequirementsD3D11 { public T type; public void* next; public long adapterLuid; public int minFeatureLevel; }
        [StructLayout(LayoutKind.Sequential)] public struct SessionCreateInfo { public T type; public void* next; public ulong createFlags; public ulong systemId; }
        [StructLayout(LayoutKind.Sequential)] public struct SessionBeginInfo { public T type; public void* next; public int primaryViewConfigurationType; }
        [StructLayout(LayoutKind.Sequential)] public struct ReferenceSpaceCreateInfo { public T type; public void* next; public int referenceSpaceType; public Pose poseInReferenceSpace; }
        [StructLayout(LayoutKind.Sequential)]
        public struct ViewConfigurationView
        {
            public T type; public void* next;
            public uint recommendedImageRectWidth, maxImageRectWidth, recommendedImageRectHeight, maxImageRectHeight;
            public uint recommendedSwapchainSampleCount, maxSwapchainSampleCount;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct SwapchainCreateInfo
        {
            public T type; public void* next; public ulong createFlags; public ulong usageFlags; public long format;
            public uint sampleCount, width, height, faceCount, arraySize, mipCount;
        }
        [StructLayout(LayoutKind.Sequential)] public struct SwapchainImageD3D11 { public T type; public void* next; public IntPtr texture; }
        [StructLayout(LayoutKind.Sequential)] public struct SwapchainImageAcquireInfo { public T type; public void* next; }
        [StructLayout(LayoutKind.Sequential)] public struct SwapchainImageWaitInfo { public T type; public void* next; public long timeout; }
        [StructLayout(LayoutKind.Sequential)] public struct SwapchainImageReleaseInfo { public T type; public void* next; }
        [StructLayout(LayoutKind.Sequential)] public struct FrameWaitInfo { public T type; public void* next; }
        [StructLayout(LayoutKind.Sequential)] public struct FrameBeginInfo { public T type; public void* next; }
        [StructLayout(LayoutKind.Sequential)]
        public struct FrameState { public T type; public void* next; public long predictedDisplayTime; public long predictedDisplayPeriod; public uint shouldRender; }
        [StructLayout(LayoutKind.Sequential)]
        public struct FrameEndInfo { public T type; public void* next; public long displayTime; public int environmentBlendMode; public uint layerCount; public void** layers; }
        [StructLayout(LayoutKind.Sequential)]
        public struct View { public T type; public void* next; public Pose pose; public Fov fov; }
        [StructLayout(LayoutKind.Sequential)]
        public struct ViewLocateInfo { public T type; public void* next; public int viewConfigurationType; public long displayTime; public ulong space; }
        [StructLayout(LayoutKind.Sequential)] public struct ViewState { public T type; public void* next; public ulong viewStateFlags; }
        [StructLayout(LayoutKind.Sequential)]
        public struct SwapchainSubImage { public ulong swapchain; public Rect2Di imageRect; public uint imageArrayIndex; }
        [StructLayout(LayoutKind.Sequential)]
        public struct CompositionLayerProjectionView { public T type; public void* next; public Pose pose; public Fov fov; public SwapchainSubImage subImage; }
        [StructLayout(LayoutKind.Sequential)]
        public struct CompositionLayerProjection { public T type; public void* next; public ulong layerFlags; public ulong space; public uint viewCount; public CompositionLayerProjectionView* views; }
        [StructLayout(LayoutKind.Sequential)]
        public struct CompositionLayerQuad
        {
            public T type; public void* next; public ulong layerFlags; public ulong space; public int eyeVisibility;
            public SwapchainSubImage subImage; public Pose pose; public Extent2Df size;
        }
        [StructLayout(LayoutKind.Sequential)] public struct EventDataBuffer { public T type; public void* next; public fixed byte varying[4000]; }
        [StructLayout(LayoutKind.Sequential)] public struct EventDataSessionStateChanged { public T type; public void* next; public ulong session; public int state; public long time; }

        [StructLayout(LayoutKind.Sequential)] public struct ActionSetCreateInfo { public T type; public void* next; public fixed byte actionSetName[64]; public fixed byte localizedActionSetName[128]; public uint priority; }
        [StructLayout(LayoutKind.Sequential)]
        public struct ActionCreateInfo { public T type; public void* next; public fixed byte actionName[64]; public int actionType; public uint countSubactionPaths; public ulong* subactionPaths; public fixed byte localizedActionName[128]; }
        [StructLayout(LayoutKind.Sequential)] public struct ActionSuggestedBinding { public ulong action; public ulong binding; }
        [StructLayout(LayoutKind.Sequential)] public struct InteractionProfileSuggestedBinding { public T type; public void* next; public ulong interactionProfile; public uint countSuggestedBindings; public ActionSuggestedBinding* suggestedBindings; }
        [StructLayout(LayoutKind.Sequential)] public struct SessionActionSetsAttachInfo { public T type; public void* next; public uint countActionSets; public ulong* actionSets; }
        [StructLayout(LayoutKind.Sequential)] public struct ActionSpaceCreateInfo { public T type; public void* next; public ulong action; public ulong subactionPath; public Pose poseInActionSpace; }
        [StructLayout(LayoutKind.Sequential)] public struct ActiveActionSet { public ulong actionSet; public ulong subactionPath; }
        [StructLayout(LayoutKind.Sequential)] public struct ActionsSyncInfo { public T type; public void* next; public uint countActiveActionSets; public ActiveActionSet* activeActionSets; }
        [StructLayout(LayoutKind.Sequential)] public struct ActionStateGetInfo { public T type; public void* next; public ulong action; public ulong subactionPath; }
        [StructLayout(LayoutKind.Sequential)] public struct HapticActionInfo { public T type; public void* next; public ulong action; public ulong subactionPath; }
        [StructLayout(LayoutKind.Sequential)] public struct HapticVibration { public T type; public void* next; public long duration; public float frequency; public float amplitude; }
        [StructLayout(LayoutKind.Sequential)] public struct InteractionProfileState { public T type; public void* next; public ulong interactionProfile; }
        [StructLayout(LayoutKind.Sequential)] public struct ActionStateBoolean { public T type; public void* next; public uint currentState; public uint changedSinceLastSync; public long lastChangeTime; public uint isActive; }
        [StructLayout(LayoutKind.Sequential)] public struct ActionStateFloat { public T type; public void* next; public float currentState; public uint changedSinceLastSync; public long lastChangeTime; public uint isActive; }
        [StructLayout(LayoutKind.Sequential)] public struct ActionStateVector2f { public T type; public void* next; public float x, y; public uint changedSinceLastSync; public long lastChangeTime; public uint isActive; }
        [StructLayout(LayoutKind.Sequential)] public struct SpaceLocation { public T type; public void* next; public ulong locationFlags; public Pose pose; }

        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int xrCreateInstance(ref InstanceCreateInfo info, out ulong instance);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int xrDestroyInstance(ulong instance);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern unsafe int xrGetSystemProperties(ulong instance, ulong systemId, void* properties);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int xrGetSystem(ulong instance, ref SystemGetInfo info, out ulong systemId);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int xrGetInstanceProcAddr(ulong instance, [MarshalAs(UnmanagedType.LPStr)] string name, out IntPtr fn);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int xrCreateSession(ulong instance, ref SessionCreateInfo info, out ulong session);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int xrDestroySession(ulong session);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int xrBeginSession(ulong session, ref SessionBeginInfo info);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int xrEndSession(ulong session);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int xrPollEvent(ulong instance, ref EventDataBuffer data);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int xrCreateReferenceSpace(ulong session, ref ReferenceSpaceCreateInfo info, out ulong space);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int xrDestroySpace(ulong space);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int xrEnumerateViewConfigurationViews(ulong instance, ulong systemId, int viewConfigType, uint capacity, out uint countOut, ViewConfigurationView* views);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int xrEnumerateSwapchainFormats(ulong session, uint capacity, out uint countOut, long* formats);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int xrCreateSwapchain(ulong session, ref SwapchainCreateInfo info, out ulong swapchain);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int xrDestroySwapchain(ulong swapchain);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int xrEnumerateSwapchainImages(ulong swapchain, uint capacity, out uint countOut, SwapchainImageD3D11* images);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int xrAcquireSwapchainImage(ulong swapchain, ref SwapchainImageAcquireInfo info, out uint index);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int xrWaitSwapchainImage(ulong swapchain, ref SwapchainImageWaitInfo info);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int xrReleaseSwapchainImage(ulong swapchain, ref SwapchainImageReleaseInfo info);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int xrWaitFrame(ulong session, ref FrameWaitInfo info, ref FrameState state);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int xrBeginFrame(ulong session, ref FrameBeginInfo info);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int xrEndFrame(ulong session, ref FrameEndInfo info);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int xrLocateViews(ulong session, ref ViewLocateInfo info, ref ViewState state, uint capacity, out uint countOut, View* views);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int xrLocateSpace(ulong space, ulong baseSpace, long time, ref SpaceLocation location);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int xrStringToPath(ulong instance, [MarshalAs(UnmanagedType.LPStr)] string path, out ulong xrPath);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int xrCreateActionSet(ulong instance, ref ActionSetCreateInfo info, out ulong set);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int xrCreateAction(ulong set, ref ActionCreateInfo info, out ulong action);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int xrSuggestInteractionProfileBindings(ulong instance, ref InteractionProfileSuggestedBinding info);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int xrAttachSessionActionSets(ulong session, ref SessionActionSetsAttachInfo info);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int xrCreateActionSpace(ulong session, ref ActionSpaceCreateInfo info, out ulong space);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int xrApplyHapticFeedback(ulong session, ref HapticActionInfo info, ref HapticVibration vib);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int xrGetCurrentInteractionProfile(ulong session, ulong topLevelUserPath, ref InteractionProfileState state);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int xrPathToString(ulong instance, ulong path, uint capacity, out uint count, byte* buffer);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int xrSyncActions(ulong session, ref ActionsSyncInfo info);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int xrGetActionStateBoolean(ulong session, ref ActionStateGetInfo info, ref ActionStateBoolean state);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int xrGetActionStateFloat(ulong session, ref ActionStateGetInfo info, ref ActionStateFloat state);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int xrGetActionStateVector2f(ulong session, ref ActionStateGetInfo info, ref ActionStateVector2f state);

        public static void Str(byte* dst, int cap, string s)
        {
            var b = Encoding.UTF8.GetBytes(s);
            int n = Math.Min(b.Length, cap - 1);
            for (int i = 0; i < n; i++) dst[i] = b[i];
            dst[n] = 0;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int GetD3D11Requirements(ulong instance, ulong systemId, ref GraphicsRequirementsD3D11 req);
    }
}

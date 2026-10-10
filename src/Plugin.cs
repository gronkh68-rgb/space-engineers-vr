using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using SeVr.Vr;
using VRage.Plugins;

namespace SeVr
{
    public sealed class Plugin : IPlugin
    {
        internal sealed class Cfg { public float RenderScale = 1.0f; public bool Enabled = true; public string Runtime = ""; public float EyeSmooth = 0f; }
        internal static Cfg Config = new Cfg();
        static string logPath = Path.Combine(Path.GetTempPath(), "SeVr.log");
        Harmony harmony;

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibrary(string path);

        internal static void Log(string s)
        {
            try { File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff ") + s + Environment.NewLine); } catch { }
        }

        static string assetDir;
        static bool xrStarted, hooksReady;

        /// <summary>Called by Pulsar with the folder of the plugin's asset files (openxr_loader.dll) when it is installed from the plugin list.</summary>
        public void LoadAssets(string assetFolder)
        {
            assetDir = assetFolder;
            Log("assets folder: " + assetFolder);
            if (hooksReady) StartXr();
        }

        static string FindLoader()
        {
            var cands = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(assetDir)) cands.Add(assetDir);
            try { var loc = Assembly.GetExecutingAssembly().Location; if (!string.IsNullOrEmpty(loc)) cands.Add(Path.GetDirectoryName(loc)); } catch { }
            foreach (var d in cands)
            {
                var p = Path.Combine(d, "openxr_loader.dll");
                if (File.Exists(p)) return p;
            }
            return null;
        }

        // Runtime=steamvr (or a path to a runtime .json) in SeVr.cfg: use that OpenXR runtime for this game only, whatever the system default is.
        static void ChooseRuntime()
        {
            try
            {
                var r = Config.Runtime;
                if (string.IsNullOrWhiteSpace(r) || r.Equals("default", StringComparison.OrdinalIgnoreCase)) return;
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XR_RUNTIME_JSON"))) return;
                string json = r;
                if (r.Equals("steamvr", StringComparison.OrdinalIgnoreCase))
                {
                    string steam = null;
                    try { steam = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string; } catch { }
                    var cands = new System.Collections.Generic.List<string>();
                    if (!string.IsNullOrEmpty(steam)) cands.Add(Path.Combine(steam.Replace('/', '\\'), @"steamapps\common\SteamVR\steamxr_win64.json"));
                    cands.Add(@"C:\Program Files (x86)\Steam\steamapps\common\SteamVR\steamxr_win64.json");
                    json = null;
                    foreach (var c in cands) if (File.Exists(c)) { json = c; break; }
                    if (json == null) { Log("Runtime=steamvr: SteamVR runtime file not found"); return; }
                }
                if (!File.Exists(json)) { Log("Runtime file not found: " + json); return; }
                Environment.SetEnvironmentVariable("XR_RUNTIME_JSON", json);
                Log("OpenXR runtime forced: " + json);
            }
            catch (Exception e) { Log("ChooseRuntime: " + e.Message); }
        }

        // Test mode without a headset: needs SEVR_FAKE=1 AND a file "sevr.fake" next to the plugin. The environment variable alone is not
        // enough, because a launcher such as Steam can inherit it from a test run and pass it on to every game it starts.
        static bool IsFakeRequested()
        {
            if (Environment.GetEnvironmentVariable("SEVR_FAKE") != "1") return false;
            try
            {
                var loc = Assembly.GetExecutingAssembly().Location;
                return !string.IsNullOrEmpty(loc) && File.Exists(Path.Combine(Path.GetDirectoryName(loc), "sevr.fake"));
            }
            catch { return false; }
        }

        static void StartXr()
        {
            if (xrStarted) return;
            try
            {
                var ldr = FindLoader();
                if (ldr == null) { Log("openxr_loader.dll not found yet"); return; }
                if (LoadLibrary(ldr) == IntPtr.Zero) { Log("could not load " + ldr); return; }
                xrStarted = true;
                ChooseRuntime();
                if (IsFakeRequested()) { Xr.XrRuntime.CreateFake(); Log("fake stereo mode (no headset)"); }
                else if (!Xr.XrRuntime.TryCreateInstance(out string err)) Log("VR inactive, OpenXR not available: " + err);
                else Log("OpenXR ready, eye size " + Xr.XrRuntime.Instance.EyeWidth + "x" + Xr.XrRuntime.Instance.EyeHeight + ", Steam Frame controller profile " + (Xr.XrRuntime.Instance.FrameProfile ? "enabled" : "not available"));
            }
            catch (Exception e) { Log("StartXr failed: " + e); }
        }

        public void Init(object gameInstance)
        {
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceEngineers");
            logPath = Path.Combine(data, "SeVr.log");
            try { File.WriteAllText(logPath, ""); } catch { }
            LoadConfig(Path.Combine(data, "SeVr.cfg"));
            Log("Space Engineers VR " + Assembly.GetExecutingAssembly().GetName().Version + " starting");
            if (!Config.Enabled || Environment.GetEnvironmentVariable("SEVR_DISABLE") == "1") { Log("disabled by config"); return; }
            try
            {
                harmony = new Harmony("seVr.vr");
                VrRenderer.Install(harmony);
                VrInput.Install(harmony);
                VrMarkers.Install(harmony);
                VrHands.Install(harmony);
                VrBuild.Install(harmony);
                Log("hooks installed");
                hooksReady = true;
                StartXr();
            }
            catch (Exception e) { Log("Init failed: " + e); }
        }

        static void LoadConfig(string path)
        {
            try
            {
                if (!File.Exists(path))
                    File.WriteAllText(path, "# Space Engineers VR\r\nEnabled=true\r\n# 0.4 - 1.5, fraction of the headset's recommended resolution\r\nRenderScale=1.0\r\n");
                foreach (var line in File.ReadAllLines(path))
                {
                    var l = line.Trim();
                    if (l.StartsWith("#") || !l.Contains("=")) continue;
                    var kv = l.Split(new[] { '=' }, 2);
                    var k = kv[0].Trim(); var v = kv[1].Trim();
                    if (k == "Enabled") Config.Enabled = v.ToLowerInvariant() != "false";
                    if (k == "Runtime") Config.Runtime = v;
                    if (k == "EyeSmooth" && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float es)) Config.EyeSmooth = Math.Max(0f, Math.Min(0.5f, es));
                    if (k == "RenderScale" && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                        Config.RenderScale = Math.Max(0.4f, Math.Min(1.5f, f));
                }
            }
            catch { }
        }

        public void Update() { }

        public void Dispose()
        {
            try { if (harmony != null) harmony.UnpatchAll("seVr.vr"); if (Xr.XrRuntime.Instance != null) Xr.XrRuntime.Instance.Shutdown(); } catch { }
        }
    }
}

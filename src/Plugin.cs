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
        internal sealed class Cfg
        {
            public float RenderScale = 1.0f; public bool Enabled = true; public string Runtime = ""; public float EyeSmooth = 0f; public string Zones = "on";
            public float TurnSpeed = 150f, DeadZone = 0.18f, MenuDistance = 1.4f, MenuWidth = 1.6f;
            // where each HUD part floats: lhand, rhand, belt, chestl, chestr, or hud (stays in the flat HUD window)
            // parts: block info head, component list, hotbar, status bars, gravity and warnings, chat
            public string[] Anchors = { "bottom2", "bottom2", "bottom", "bottom", "bottom", "bottom2", "view" };
            public bool GuideHands;                          // also draw the simple block hands (exactly at the controllers) to compare the character's hands with
            public float HandLead = 0f, HandSmooth = 0f, HandBack = 8f, HandDown = 1.5f, HudYaw = 0f;
            public string HudMode = "hand";                  // rows: panels in rows below the view; wrist: status, indicators and tool bar on the left wrist, visible when you look at it; hand: one menu on the left hand, opened with a small button
            public bool ZonesInSeat = false;                 // body zones also in a seat (they swallow the grips that roll the ship)
            public string Mirror = "vr";                     // the monitor shows: vr (the left eye with the HUD panels where they are in the world) or game (left eye with the game's flat HUD)
            public bool HandAimOnline = true;                // in online games (not hosting) the character's head is steered to the hand's direction while firing, so the server shoots there
            public float MenuTilt = 25f, MenuHeight = -0.20f;      // the game's menu windows (terminal, inventory ...): tilted back like a tablet, below eye level
            public bool LogBillboards;                       // writes a few lines about the overlay pieces of mods (BuildInfo ...) to the log (cfg only: LogBillboards=true)
            public bool MenuLocked;                         // the menu window cannot be picked up and moved
            public float MenuSide = 0f;                     // metres to the right (negative: left) of straight ahead; MenuDistance and MenuWidth are above
            /// <summary>Where the player moved HUD parts to (edit mode): "part@mode" -> position x y z, rotation x y z w, scale (relative to the part's default place).</summary>
            public System.Collections.Generic.Dictionary<string, float[]> Layout = new System.Collections.Generic.Dictionary<string, float[]>();
            public bool HandAimUse = true;                  // what you can use or select (doors, terminals, cockpits, blocks) is found with the right controller's ray
            public bool Crosshair = false;                  // the head-locked crosshair (the hand aims instead)
            public float WristScale = 0.5f;                 // size of the wrist panels compared with the rows
            public float HudDistance = 1.0f;                 // metres: how far away the HUD floats (it keeps its apparent size)
            public float HudOpacity = 1f;                   // 0.15 - 1: how opaque the plugin's own HUD panels are
            public string ModHud = "hide";                   // overlays of mods drawn near the camera (BuildInfo ...): hide = blocked, float = in front of the head, game = left alone
            public float ModHudDistance = 0.9f, ModHudScale = 0.8f;                // milliseconds: hand targets are moved ahead by speed x this (hands trail behind at speed)
            public float TurnSmooth = 0.12f;                // seconds the turn stick takes to ease in and out (0 = direct)
            public bool Help = true;                        // the help window (controls and hints) is shown; the ? button on the left hand switches it
            public bool OwnHud = true;                      // the plugin draws status, tool bar and block info itself (false: pieces cut out of the game's HUD picture)
            public string ZonesOff = "";                     // comma separated zone names that are switched off (rhead, lhead, shoulder, lshoulder, chest, rhip, lhip)
            public bool LayoutV2;                            // set when the cfg file already has the bottom HUD layout (older files get the new defaults once)
            public string Markers = "custom";             // GPS and signal markers: original (the game's own, placed by the VR head pose) or custom (own drawing)
            public static readonly string[] PartKeys = { "HudInfo", "HudComponents", "HudHotbar", "HudStats", "HudGravity", "HudChat" };
            public float PanelSize = 0.34f;                                     // width of a panel in metres
            public float HandRoll = 90f;                                        // degrees: turn of the hands about the pointing direction (90 = palms face each other)
            public bool HandsAtGrip = true;                                    // the character's hands sit where the real hands hold the controllers
            public string Controller = "auto";                                  // auto, touch, frame: which button layout to use
            public System.Collections.Generic.Dictionary<string, string> Binds = new System.Collections.Generic.Dictionary<string, string>();   // source -> game control (or none)
        }
        internal static Cfg Config = new Cfg();
        internal const string Tag = "SeVr";            // names the settings file, the log and the captures
        internal static string CfgPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceEngineers", Tag + ".cfg");
        internal static string LogPath { get { return logPath; } }

        /// <summary>Pulsar shows a settings button for the plugin when it finds this method.</summary>
        public void OpenConfigDialog() { if (settingsTest == "2") BindingsScreen.Open(); else if (settingsTest == "3") { var tb = Sandbox.Game.Screens.Helpers.MyToolbarComponent.CurrentToolbar; if (tb != null) for (int i = 0; i < 9; i++) { var it = tb.GetItemAtSlot(i); if (it != null && it.GetType().Name.Contains("CubeBlock")) { tb.ActivateItemAtSlot(i, false, true, false); break; } } } else SettingsScreen.Open(); }

        /// <summary>Push the settings that can change while the game runs into the live values.</summary>
        internal static void ApplyConfig()
        {
            VrHead.TurnRateDegPerSec = Config.TurnSpeed;
            VrControls.DeadZone = Config.DeadZone;
            VrRenderer.MenuDistance = Config.MenuDistance;
            VrRenderer.MenuWidth = Config.MenuWidth;
            VrHands.SetRoll(Config.HandRoll);
        }

        internal static string NormAnchor(string v)
        {
            v = (v ?? "").Trim().ToLowerInvariant();
            if (v == "left") return "lhand";
            if (v == "right") return "rhand";
            if (v == "lhand" || v == "rhand" || v == "belt" || v == "chestl" || v == "chestr" || v == "hud" || v == "bottom" || v == "bottom2") return v;
            return "hud";
        }

        internal static void SaveConfig()
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# Space Engineers VR (also editable from Pulsar: Plugins, Space Engineers VR, settings)");
            sb.AppendLine("Enabled=" + (Config.Enabled ? "true" : "false"));
            sb.AppendLine("# 0.4 - 1.5, fraction of the headset's recommended resolution (restart)");
            sb.AppendLine("RenderScale=" + Config.RenderScale.ToString("0.##", ci));
            sb.AppendLine("# default or steamvr: OpenXR runtime for this game only (restart)");
            sb.AppendLine("Runtime=" + (string.IsNullOrWhiteSpace(Config.Runtime) ? "default" : Config.Runtime));
            sb.AppendLine("# left, right, hud: where the block info (name, description) and the component list float (welder, grinder, building)");
            for (int pi = 0; pi < Cfg.PartKeys.Length; pi++) sb.AppendLine(Cfg.PartKeys[pi] + "=" + Config.Anchors[pi]);
            sb.AppendLine("# original = the game's own GPS and signal markers, custom = own drawing");
            sb.AppendLine("Markers=" + Config.Markers);
            sb.AppendLine("# width of a floating panel in metres");
            sb.AppendLine("PanelSize=" + Config.PanelSize.ToString("0.##", ci));
            sb.AppendLine("# degrees: turn of the character's hands about the pointing direction (90 = palms face each other, 0 = flat, negative = the other way)");
            sb.AppendLine("HandRoll=" + Config.HandRoll.ToString("0", ci));
            sb.AppendLine("# true: the character's hands sit where your real hands hold the controllers");
            sb.AppendLine("HandsAtGrip=" + (Config.HandsAtGrip ? "true" : "false"));
            sb.AppendLine("# auto, touch, frame: which controller layout to use (auto detects it)");
            sb.AppendLine("Controller=" + Config.Controller);
            sb.AppendLine("# auto (controllers with few buttons), on, off: body zones (head, chest, hips, shoulder)");
            sb.AppendLine("Zones=" + Config.Zones);
            sb.AppendLine("# zones that are switched off, comma separated: rhead, lhead, shoulder, lshoulder, chest, rhip, lhip");
            sb.AppendLine("ZonesOff=" + Config.ZonesOff);
            sb.AppendLine("# milliseconds, 0 = off: hand targets are moved ahead by speed x this value (hands trail behind at speed)");
            sb.AppendLine("HandLeadMs=" + Config.HandLead.ToString("0", ci));
            sb.AppendLine("# centimetres: the character's hands are pulled back along the pointing direction (negative = forward)");
            sb.AppendLine("HandBackCm=" + Config.HandBack.ToString("0", ci));
            sb.AppendLine("# metres, 0.5 - 2.5: how far away the HUD floats (it keeps its apparent size)");
            sb.AppendLine("HudDistance=" + Config.HudDistance.ToString("0.##", ci));
            sb.AppendLine("# rows (panels in rows below the view), wrist (status, indicators and tool bar on the left wrist, visible when you look at it) or hand (one menu on the left hand that turns with it, opened with a small button on the hand: point at it with the right hand and pull the trigger; point at the tool bar and pull the trigger to pick a slot)");
            sb.AppendLine("HudMode=" + Config.HudMode);
            sb.AppendLine("# true: what you can use or select (doors, terminals, cockpits, block highlight) is found with the right controller's ray instead of the head");
            sb.AppendLine("HandAimUse=" + (Config.HandAimUse ? "true" : "false"));
            sb.AppendLine("# true: a crosshair floats straight ahead of the head; false: nothing, you aim with the right hand");
            sb.AppendLine("Crosshair=" + (Config.Crosshair ? "true" : "false"));
            sb.AppendLine("# size of the wrist panels compared with the rows (0.3 - 1)");
            sb.AppendLine("WristScale=" + Config.WristScale.ToString("0.##", ci));
            sb.AppendLine("# true: body zones also work while sitting in a seat (they swallow the grips that roll the ship)");
            sb.AppendLine("ZonesInSeat=" + (Config.ZonesInSeat ? "true" : "false"));
            sb.AppendLine("# 0.15 - 1: how opaque the plugin's own HUD panels are (1 = fully)");
            sb.AppendLine("HudOpacity=" + Config.HudOpacity.ToString("0.##", ci));
            sb.AppendLine("# hide (blocked), float (in front of the head) or game (left alone): overlays of mods that draw near the camera (BuildInfo ...); distance in metres and size of the floating layout");
            sb.AppendLine("ModHudMode=" + Config.ModHud);
            sb.AppendLine("ModHudDistance=" + Config.ModHudDistance.ToString("0.##", ci));
            sb.AppendLine("ModHudScale=" + Config.ModHudScale.ToString("0.##", ci));
            sb.AppendLine("# centimetres: the character's hands are moved down (negative = up)");
            sb.AppendLine("HandDownCm=" + Config.HandDown.ToString("0.#", ci));
            sb.AppendLine("# degrees: turns the whole HUD and the crosshair sideways (positive = to the left)");
            sb.AppendLine("HudYawDeg=" + Config.HudYaw.ToString("0.#", ci));
            sb.AppendLine("# seconds, 0 = off: smooths the hands' offset to the body (less tremble, but the hands lag at speed)");
            sb.AppendLine("HandSmooth=" + Config.HandSmooth.ToString("0.##", ci));
            sb.AppendLine("# true: also draw the simple block hands at the controllers, to tune HandLead against the character's hands");
            sb.AppendLine("GuideHands=" + (Config.GuideHands ? "true" : "false"));
            sb.AppendLine("# true: status, tool bar and block info are drawn by the plugin itself; false: cut out of the game's HUD picture");
            sb.AppendLine("TurnSmooth=" + Config.TurnSmooth.ToString("0.###", ci));
            sb.AppendLine("OwnHud=" + (Config.OwnHud ? "true" : "false"));
            sb.AppendLine("Help=" + (Config.Help ? "true" : "false"));
            sb.AppendLine("# 2 = HUD parts in a row below the view (older files are moved to it once)");
            sb.AppendLine("HudLayout=2");
            sb.AppendLine("# seconds, 0 = off: smooths the eye point against head bone sway");
            sb.AppendLine("EyeSmooth=" + Config.EyeSmooth.ToString("0.##", ci));
            sb.AppendLine("# degrees per second at full stick");
            sb.AppendLine("TurnSpeed=" + Config.TurnSpeed.ToString("0", ci));
            sb.AppendLine("# stick dead zone 0 - 0.5");
            sb.AppendLine("DeadZone=" + Config.DeadZone.ToString("0.##", ci));
            sb.AppendLine("# floating menu: distance and width in metres");
            sb.AppendLine("MenuDistance=" + Config.MenuDistance.ToString("0.##", ci));
            sb.AppendLine("MenuWidth=" + Config.MenuWidth.ToString("0.##", ci));
            sb.AppendLine("# the game's menu windows: tilt in degrees (0 = upright, positive = leaning back), height in metres relative to the eyes, side offset in metres");
            sb.AppendLine("MenuTilt=" + Config.MenuTilt.ToString("0.#", ci));
            sb.AppendLine("MenuHeight=" + Config.MenuHeight.ToString("0.##", ci));
            sb.AppendLine("MenuSide=" + Config.MenuSide.ToString("0.##", ci));
            sb.AppendLine("# the monitor shows: vr (what you see in VR: the left eye with the HUD panels) or game (left eye with the game's flat HUD)");
            sb.AppendLine("Mirror=" + Config.Mirror);
            sb.AppendLine("# true: in online games where you are not the host, the character's head follows the hand's direction while the right trigger is held, so shots go where you point");
            sb.AppendLine("HandAimOnline=" + (Config.HandAimOnline ? "true" : "false"));
            sb.AppendLine("# true: the game's menu windows cannot be moved (the lock button below the window)");
            sb.AppendLine("MenuLocked=" + (Config.MenuLocked ? "true" : "false"));
            sb.AppendLine("# where HUD parts were moved to in edit mode: Layout.<part>@<hud mode>=x,y,z,qx,qy,qz,qw,scale,attach (attach: 0 default place, 1 left hand, 2 head; delete the line to put a part back)");
            foreach (var kv in Config.Layout) sb.AppendLine("Layout." + kv.Key + "=" + string.Join(",", System.Linq.Enumerable.Select(kv.Value, x => x.ToString("0.####", ci))));
            sb.AppendLine("# key bindings: Bind.<VR input>=<game control or none>, edited in the settings screen");
            foreach (var kv in Config.Binds) sb.AppendLine("Bind." + kv.Key + "=" + kv.Value);
            File.WriteAllText(CfgPath, sb.ToString());
        }
        static string logPath = Path.Combine(Path.GetTempPath(), Tag + ".log");
        Harmony harmony;

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibrary(string path);

        internal static void Log(string s)
        {
            try { File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff ") + s + Environment.NewLine); } catch { }
        }

        static string assetDir;
        static bool xrStarted, hooksReady;

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

        /// <summary>The game's user data folder (follows the -appdata argument), %AppData%\SpaceEngineers as a fallback.</summary>
        internal static string DataDir()
        {
            try { string u = VRage.FileSystem.MyFileSystem.UserDataPath; if (!string.IsNullOrEmpty(u)) return u; } catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceEngineers");
        }

        public void Init(object gameInstance)
        {
            var data = DataDir();
            try { Directory.CreateDirectory(data); } catch { }
            logPath = Path.Combine(data, Tag + ".log");
            try { File.WriteAllText(logPath, ""); } catch { }
            CfgPath = Path.Combine(data, Tag + ".cfg");
            LoadConfig(CfgPath);
            ApplyConfig();
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
                VrPanel.Install(harmony);
                VrHudLayout.Install(harmony);
                VrHud.Install(harmony);
                VrBillboards.Install(harmony);
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
                    if (k == "Zones") Config.Zones = v.ToLowerInvariant() == "off" ? "off" : "on";
                    if (k == "ZonesOff") Config.ZonesOff = v.ToLowerInvariant();
                    if (k == "HudLayout" && v == "2") Config.LayoutV2 = true;
                    if (k == "OwnHud") Config.OwnHud = v.ToLowerInvariant() != "false";
                    if (k == "Help") Config.Help = v.ToLowerInvariant() != "false";
                    if (k == "GuideHands") Config.GuideHands = v.ToLowerInvariant() == "true";
                    if (k == "HudMode") { var hm = v.ToLowerInvariant(); Config.HudMode = hm == "wrist" || hm == "hand" ? hm : "rows"; }
                    if (k == "ZonesInSeat") Config.ZonesInSeat = v.ToLowerInvariant() != "false";
                    if (k == "Crosshair") Config.Crosshair = v.ToLowerInvariant() != "false";
                    if (k == "MenuLocked") Config.MenuLocked = v.ToLowerInvariant() == "true";
                    if (k == "LogBillboards") Config.LogBillboards = v.ToLowerInvariant() == "true";
                    if (k == "Mirror") Config.Mirror = v.ToLowerInvariant() == "vr" ? "vr" : "game";
                    if (k == "HandAimOnline") Config.HandAimOnline = v.ToLowerInvariant() == "true";
                    if (k == "HandAimUse") Config.HandAimUse = v.ToLowerInvariant() != "false";
                    if (k.StartsWith("Layout.") && k.Length > 7)
                    {
                        var parts = v.Split(',');
                        if (parts.Length == 8 || parts.Length == 9)
                        {
                            var arr = new float[9]; bool okl = true;       // [8]: 0 = relative to the default place, 1 = tied to the left hand, 2 = tied to the head
                            for (int li = 0; li < parts.Length; li++) if (!float.TryParse(parts[li], NumberStyles.Float, CultureInfo.InvariantCulture, out arr[li])) okl = false;
                            if (okl) Config.Layout[k.Substring(7)] = arr;
                        }
                    }
                    if (k == "ModHudMode") { var mv = v.ToLowerInvariant(); Config.ModHud = mv == "float" || mv == "game" ? mv : "hide"; }      // (the older key ModHud is ignored on purpose)
                    if (k == "Controller") Config.Controller = v.ToLowerInvariant();
                    for (int pi = 0; pi < Cfg.PartKeys.Length; pi++) if (k == Cfg.PartKeys[pi]) Config.Anchors[pi] = NormAnchor(v);
                    if (k == "Markers") Config.Markers = v.ToLowerInvariant() == "custom" ? "custom" : "original";
                    if (k == "PanelInfo") Config.Anchors[0] = NormAnchor(v);              // older names
                    if (k == "PanelComponents") Config.Anchors[1] = NormAnchor(v);
                    if (k == "HandsAtGrip") Config.HandsAtGrip = v.ToLowerInvariant() != "false";
                    if (k.StartsWith("Bind.") && k.Length > 5 && v.Length > 0) Config.Binds[k.Substring(5)] = v;
                    float fv;
                    if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out fv))
                    {
                        if (k == "TurnSpeed") Config.TurnSpeed = Math.Max(30f, Math.Min(400f, fv));
                        if (k == "DeadZone") Config.DeadZone = Math.Max(0f, Math.Min(0.5f, fv));
                        if (k == "MenuDistance") Config.MenuDistance = Math.Max(0.6f, Math.Min(4f, fv));
                        if (k == "HandLeadMs") Config.HandLead = Math.Max(0f, Math.Min(60f, fv));      // (the older key HandLead is ignored on purpose)
                        if (k == "HandBackCm") Config.HandBack = Math.Max(-15f, Math.Min(15f, fv));
                        if (k == "HudDistance") Config.HudDistance = Math.Max(0.5f, Math.Min(2.5f, fv));
                        if (k == "MenuTilt") Config.MenuTilt = Math.Max(-10f, Math.Min(80f, fv));
                        if (k == "MenuHeight") Config.MenuHeight = Math.Max(-1.2f, Math.Min(1f, fv));
                        if (k == "MenuSide") Config.MenuSide = Math.Max(-2f, Math.Min(2f, fv));
                        if (k == "WristScale") Config.WristScale = Math.Max(0.3f, Math.Min(1f, fv));
                        if (k == "HudOpacity") Config.HudOpacity = Math.Max(0.15f, Math.Min(1f, fv));
                        if (k == "ModHudDistance") Config.ModHudDistance = Math.Max(0.3f, Math.Min(3f, fv));
                        if (k == "ModHudScale") Config.ModHudScale = Math.Max(0.2f, Math.Min(2f, fv));
                        if (k == "HandDownCm") Config.HandDown = Math.Max(-15f, Math.Min(15f, fv));
                        if (k == "HudYawDeg") Config.HudYaw = Math.Max(-30f, Math.Min(30f, fv));
                        if (k == "HandSmooth") Config.HandSmooth = Math.Max(0f, Math.Min(0.5f, fv));
                        if (k == "HandRoll") Config.HandRoll = Math.Max(-180f, Math.Min(180f, fv));
                        if (k == "TurnSmooth") Config.TurnSmooth = Math.Max(0f, Math.Min(0.6f, fv));
                        if (k == "PanelSize") Config.PanelSize = Math.Max(0.12f, Math.Min(0.6f, fv));
                        if (k == "MenuWidth") Config.MenuWidth = Math.Max(0.6f, Math.Min(4f, fv));
                    }
                    if (k == "EyeSmooth" && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float es)) Config.EyeSmooth = Math.Max(0f, Math.Min(0.5f, es));
                    if (k == "RenderScale" && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                        Config.RenderScale = Math.Max(0.4f, Math.Min(1.5f, f));
                }
                if (!Config.LayoutV2)
                {
                    var d = new Cfg();
                    Array.Copy(d.Anchors, Config.Anchors, d.Anchors.Length);
                    Config.PanelSize = d.PanelSize;
                    Config.EyeSmooth = d.EyeSmooth;
                }
            }
            catch { }
        }

        static bool buildInfoNoticed, buildInfoPlugin, buildInfoChecked, chatTested; static int noticeTicks, chatTicks;
        static readonly bool chatTest = Environment.GetEnvironmentVariable("SEVR_CHATTEST") == "1";     // test switch: a few chat messages to look at the chat panel

        /// <summary>Once per game: a note in the chat when BuildInfo is there, because its overlay does not work properly in VR yet.</summary>
        static void BuildInfoNotice()
        {
            try
            {
                if (chatTest && !chatTested && Sandbox.Game.World.MySession.Static != null && Sandbox.Game.World.MySession.Static.LocalCharacter != null && Sandbox.Game.Gui.MyHud.Chat != null && ++chatTicks > 700)
                {
                    chatTested = true;
                    Sandbox.Game.Gui.MyHud.Chat.ShowMessage("Fuchs", "Hallo, das ist eine kurze Nachricht.", VRageMath.Color.LightGreen);
                    Sandbox.Game.Gui.MyHud.Chat.ShowMessage("Anna", "Und das hier ist eine ziemlich lange Nachricht, die ueber mehrere Zeilen umbrechen muss, damit man sieht, ob der Zeilenumbruch und die Groesse des Fensters passen.", VRageMath.Color.Orange);
                    Sandbox.Game.Gui.MyHud.Chat.ShowMessage("Ben", "ok", VRageMath.Color.LightBlue);
                }
                if (buildInfoNoticed || !VrRenderer.Active) return;
                var s = Sandbox.Game.World.MySession.Static;
                if (s == null || s.LocalCharacter == null || Sandbox.Game.Gui.MyHud.Chat == null) { noticeTicks = 0; return; }
                if (++noticeTicks < 240) return;           // a few seconds after entering the world
                if (!buildInfoChecked)
                {
                    buildInfoChecked = true;
                    foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                        if (a.GetName().Name.IndexOf("BuildInfo", StringComparison.OrdinalIgnoreCase) >= 0) { buildInfoPlugin = true; break; }
                }
                if (!buildInfoPlugin && !VrBillboards.BuildInfoSeen) return;
                buildInfoNoticed = true;
                string what = Config.ModHud == "hide" ? "its overlay is hidden in VR for now" : Config.ModHud == "float" ? "its overlay floats in front of you and may look wrong" : "its overlay may show artifacts";
                Sandbox.Game.Gui.MyHud.Chat.ShowMessage("Space Engineers VR", "BuildInfo detected: it does not work properly in VR yet, " + what + ".", VRageMath.Color.Gold);
                Log("notice: BuildInfo detected (plugin " + buildInfoPlugin + ", billboards " + VrBillboards.BuildInfoSeen + ")");
            }
            catch (Exception e) { buildInfoNoticed = true; Log("notice: " + e.Message); }
        }

        static int updates, menuUpdates; static bool settingsTested, saveLoadTried;
        static readonly string loadSave = Environment.GetEnvironmentVariable("SEVR_LOADSAVE");   // test switch: load this single player world from the menu
        static readonly string settingsTest = Environment.GetEnvironmentVariable("SEVR_SETTINGSTEST");

        public void Update()
        {
            BuildInfoNotice();
            if (!string.IsNullOrEmpty(loadSave) && !saveLoadTried && Sandbox.Game.World.MySession.Static == null && ++menuUpdates > 400)
            {
                saveLoadTried = true;
                Log("test: loading world " + loadSave);
                try { Sandbox.Game.World.MySessionLoader.LoadSessionByPath(loadSave); } catch (Exception e) { Log("test: load failed " + e.Message); }
            }
            // test switch: open the settings screen once a world is running (for checking it without a headset)
            if (!string.IsNullOrEmpty(settingsTest) && !settingsTested && ++updates > 600 && Sandbox.Game.World.MySession.Static != null && Sandbox.Game.World.MySession.Static.LocalCharacter != null)
            { settingsTested = true; Log("settings screen: opening (test)"); if (settingsTest == "2") BindingsScreen.Open(); else if (settingsTest == "3") { var tb = Sandbox.Game.Screens.Helpers.MyToolbarComponent.CurrentToolbar; if (tb != null) for (int i = 0; i < 9; i++) { var it = tb.GetItemAtSlot(i); if (it != null && it.GetType().Name.Contains("CubeBlock")) { tb.ActivateItemAtSlot(i, false, true, false); break; } } } else SettingsScreen.Open(); }
        }

        public void Dispose()
        {
            try { if (harmony != null) harmony.UnpatchAll("seVr.vr"); if (Xr.XrRuntime.Instance != null) Xr.XrRuntime.Instance.Shutdown(); } catch { }
        }
    }
}











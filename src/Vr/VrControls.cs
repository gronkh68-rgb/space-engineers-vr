using System;
using System.Collections.Generic;
using System.Reflection;
using Sandbox.Game;
using SeVr.Generated;
using VRage.Utils;

namespace SeVr.Vr
{
    /// <summary>
    /// The VR control scheme. The XR layer writes raw controller values here (render thread); the game's own control
    /// queries read the derived state (update thread). Which game control each VR source drives is in sheets/controls.json.
    /// </summary>
    internal static class VrControls
    {
        // raw analog values
        public static float MoveX, MoveY, TurnX, TurnY, TrigL, TrigR, GripL, GripR;
        public static float RollAxis;         // jetpack roll from the D-pad (-1 left .. 1 right)
        public static bool ZoomIn, ZoomOut;   // D-pad up / down while the block is held: bring it closer / push it away
        public static bool BuildMode;     // set by VrBuild: the block is held in place for rotating

        sealed class Src
        {
            public bool Level, RiseLatch, FallLatch;     // written by the render thread
            public bool PrevTick, LevelTick, NewPressedTick, NewReleasedTick;   // snapshot for the update thread
            public float Analog;
            public double HoldUntil;                     // pulse: stays "down" until then
        }

        static readonly object gate = new object();
        static readonly Dictionary<string, Src> src = new Dictionary<string, Src>();
        static volatile Dictionary<MyStringId, List<ControlRow>> byControl = new Dictionary<MyStringId, List<ControlRow>>();
        static volatile Dictionary<VRage.Input.MyKeys, List<ControlRow>> byKey = new Dictionary<VRage.Input.MyKeys, List<ControlRow>>();
        static volatile bool built;

        public static bool KeyLevel(VRage.Input.MyKeys k) { return KeyState(k, 0); }
        public static bool KeyNewPressed(VRage.Input.MyKeys k) { return KeyState(k, 1); }
        public static bool KeyNewReleased(VRage.Input.MyKeys k) { return KeyState(k, 2); }

        static bool KeyState(VRage.Input.MyKeys k, int what)
        {
            List<ControlRow> l;
            if (!built || !byKey.TryGetValue(k, out l)) return false;
            lock (gate)
                foreach (var r in l)
                {
                    if (!Applies(r)) continue;
                    var s = S(r.Source);
                    if (what == 0 ? s.LevelTick : what == 1 ? s.NewPressedTick : s.NewReleasedTick) return true;
                }
            return false;
        }

        // raw button levels set by the XR layer
        static readonly Dictionary<string, bool> raw = new Dictionary<string, bool>();
        static float xDown, yDown;      // seconds the X / Y buttons have been held
        static bool xHoldFired, yHoldFired, xPrevTouch, yPrevTouch;
        const float HoldSeconds = 0.45f;

        static Src S(string name)
        {
            Src s;
            if (!src.TryGetValue(name, out s)) { s = new Src(); src[name] = s; }
            return s;
        }

        static void Build()
        {
            if (built) return;
            // filled into new tables and swapped in at the end, so the update thread never reads a half-built table
            var byControl = new Dictionary<MyStringId, List<ControlRow>>();
            var byKey = new Dictionary<VRage.Input.MyKeys, List<ControlRow>>();
            var t = typeof(MyControlsSpace);
            foreach (var row0 in ControlMap.Rows)
            {
                // the player may have assigned another game control to this VR input (Bind.<source>=... in SeVr.cfg, edited in the settings screen)
                var row = row0;
                string bound;
                if (!row0.GameControl.StartsWith("KEY:") && Plugin.Config.Binds.TryGetValue(row0.Source, out bound))
                {
                    if (string.Equals(bound, "none", StringComparison.OrdinalIgnoreCase)) { S(row0.Source); continue; }
                    row = new ControlRow(row0.Id, row0.Source, bound, row0.Mode, row0.Context);
                }
                if (row.GameControl.StartsWith("KEY:"))
                {
                    // a real keyboard key (works whatever the player bound that key to)
                    VRage.Input.MyKeys key;
                    if (!Enum.TryParse(row.GameControl.Substring(4), out key)) { Plugin.Log("controls: unknown key " + row.GameControl); continue; }
                    List<ControlRow> kl;
                    if (!byKey.TryGetValue(key, out kl)) { kl = new List<ControlRow>(); byKey[key] = kl; }
                    kl.Add(row);
                    S(row.Source);
                    continue;
                }
                var f = t.GetField(row.GameControl, BindingFlags.Public | BindingFlags.Static);
                if (f == null) { Plugin.Log("controls: unknown game control " + row.GameControl); continue; }
                var id = (MyStringId)f.GetValue(null);
                List<ControlRow> list;
                if (!byControl.TryGetValue(id, out list)) { list = new List<ControlRow>(); byControl[id] = list; }
                list.Add(row);
                S(row.Source);
            }
            VrControls.byControl = byControl; VrControls.byKey = byKey;
            built = true;
        }

        /// <summary>Read the control table again (after the player changed a binding).</summary>
        public static void Rebuild()
        {
            built = false;
        }

        // ------------------------------------------------------------ writers (render thread)

        public static void BeginFrameInput()
        {
            MoveX = MoveY = TurnX = TurnY = TrigL = TrigR = GripL = GripR = 0f;
            raw.Clear();
        }

        public static void SetStick(string name, float x, float y)
        {
            // radial dead zone and a gentle response curve: fine control near the centre, full speed at the edge
            float len = (float)Math.Sqrt(x * x + y * y);
            if (len < DeadZone) { x = 0f; y = 0f; }
            else
            {
                float scaled = Math.Min(1f, (len - DeadZone) / (1f - DeadZone));
                float k = (float)Math.Pow(scaled, 1.4) / len;
                x *= k; y *= k;
            }
            if (name == "move") { MoveX = x; MoveY = y; }
            else if (name == "turn") { TurnX = x; TurnY = y; }
        }

        public static float DeadZone = 0.18f;
        /// <summary>The right trigger as the controller reports it (TrigR can be zeroed by a zone or by pointing at the wrist tool bar).</summary>
        public static float TrigRRaw, TrigLRaw;

        public static void SetFloat(string name, float v)
        {
            if (name == "trigger_l") { TrigL = Math.Max(TrigL, v); TrigLRaw = TrigL; }
            else if (name == "trigger_r") { TrigR = Math.Max(TrigR, v); TrigRRaw = TrigR; }
            else if (name == "grip_l") GripL = Math.Max(GripL, v);
            else if (name == "grip_r") GripR = Math.Max(GripR, v);
        }

        public static void SetBool(string name, bool v)
        {
            bool old;
            raw.TryGetValue(name, out old);
            raw[name] = old || v;
        }

        static bool Raw(string n) { bool b; return raw.TryGetValue(n, out b) && b; }
        public static bool RawDown(string n) { return Raw(n); }

        static void Drive(string name, bool level)
        {
            var s = S(name);
            if (level && !s.Level) s.RiseLatch = true;
            if (!level && s.Level) s.FallLatch = true;
            s.Level = level;
        }

        static void Pulse(string name)
        {
            // a pulse is a short real press: pressed now, released a moment later. Press and release in the very same tick made
            // the game see "use" pressed and released at once (entering a cockpit and leaving it again).
            var s = S(name);
            s.RiseLatch = true;
            s.HoldUntil = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency + PulseSeconds;
        }
        const double PulseSeconds = 0.09;

        /// <summary>Called by the XR layer after all actions of a frame were written.</summary>
        public static void EndFrameInput(float dt)
        {
            Build();
            lock (gate)
            {
                // While the block is held in place for rotating (left grip while building), the right hand's buttons change the block:
                // X = previous variant, B = next variant, Y = colour menu. Their normal actions are switched off meanwhile.
                double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
                // Controllers without a D-pad (Touch, Pico, ...): body zones, and a grip plus the right stick stands in for the D-pad.
                var xr = Xr.XrRuntime.Instance;
                bool touchStyle = xr != null && !xr.IsFrameController;
                bool menuOpen = xr != null && MenuState.IsMenuOpen();
                bool editing = VrOwnHud.EditMode && !menuOpen;
                bool zones = xr != null && !menuOpen && !editing && ZonesWanted(touchStyle) && (VrHead.OnFoot || VrHead.WalkingChar || Plugin.Config.ZonesInSeat);
                // pointing at the wrist tool bar or a button, or moving HUD parts: the trigger must not use the tool as well
                if ((VrOwnHud.HotbarHover || VrOwnHud.ButtonHover || VrOwnHud.SeatHover >= 0 || editing) && !menuOpen) TrigR = 0f;
                if (VrOwnHud.LeftAim) TrigL = 0f;                      // the left hand points at a HUD target: its trigger belongs to that
                if (zones)
                {
                    VrZones.Update(xr);
                    ZoneHand(true, xr, VrZones.ZoneL, ref TrigL, ref GripL, ref prevZoneL, ref tHeldL, ref gHeldL);
                    ZoneHand(false, xr, VrZones.ZoneR, ref TrigR, ref GripR, ref prevZoneR, ref tHeldR, ref gHeldR);
                }
                else { prevZoneL = prevZoneR = VrZones.Zone.None; tHeldL = tHeldR = gHeldL = gHeldR = false; }
                if (touchStyle && !menuOpen && !editing && VrHead.OnFoot && (GripL > 0.65f || GripR > 0.65f))
                {
                    float sx = TurnX, sy = TurnY;
                    bool vert = Math.Abs(sy) >= Math.Abs(sx);
                    if (vert && sy > 0.7f) raw["dpad_up"] = true; else if (vert && sy < -0.7f) raw["dpad_down"] = true;
                    else if (!vert && sx > 0.7f) raw["dpad_right"] = true; else if (!vert && sx < -0.7f) raw["dpad_left"] = true;
                    TurnX = 0f; TurnY = 0f;                        // the stick is a D-pad while a grip is held
                }

                // right hand at the head: A = V, B = Escape (their normal jump / crouch are switched off meanwhile)
                bool headAB = zones && VrZones.ZoneR == VrZones.Zone.RHead;
                raw["z_rhead_a"] = headAB && Raw("jump"); raw["z_rhead_b"] = headAB && Raw("crouch");
                if (headAB) { raw["jump"] = false; raw["crouch"] = false; }
                EdgePulse("z_rhead_a", now, 0.35); EdgePulse("z_rhead_b", now, 0.35);
                // Right grip held on foot: D-pad sends F10 / Alt+F10 / Shift+F10 / Ctrl+F10, A / B / X / Y drive the build planner.
                // The normal actions of those buttons are switched off meanwhile.
                bool combo = VrHead.OnFoot && GripR > 0.65f && !editing;
                raw["pl_a"] = combo && Raw("jump"); raw["pl_b"] = combo && Raw("crouch");
                raw["pl_x"] = combo && (Raw("fx") || Raw("x")); raw["pl_y"] = combo && (Raw("fy") || Raw("y"));
                if (combo)
                {
                    raw["dpad_up"] = false; raw["dpad_down"] = false; raw["dpad_left"] = false; raw["dpad_right"] = false;
                    raw["jump"] = false; raw["crouch"] = false; raw["fx"] = false; raw["x"] = false; raw["fy"] = false; raw["y"] = false;
                }
                foreach (var cn in new[] { "pl_a", "pl_b", "pl_x", "pl_y" }) EdgePulse(cn, now, 0.35);
                bool bx = Raw("fx") || Raw("x"), bb = Raw("crouch"), by = Raw("fy") || Raw("y");
                // jetpack without a block in hand: freeze grip + D-pad left/right = Q/E roll
                bool rollMode = !BuildMode && VrHead.OnFoot && VrHead.Jetpack && GripL > 0.65f;
                RollAxis = rollMode ? (Raw("dpad_right") ? 1f : 0f) - (Raw("dpad_left") ? 1f : 0f) : 0f;
                if (rollMode) { raw["dpad_left"] = false; raw["dpad_right"] = false; }
                ZoomIn = BuildMode && Raw("dpad_up"); ZoomOut = BuildMode && Raw("dpad_down");
                if (BuildMode) { raw["dpad_up"] = false; raw["dpad_down"] = false; }
                // Touch: grip + stick keeps only F (up) as a key; Z / L / J are covered by the body zones (roll and build zoom above still use the stick)
                if (touchStyle) { raw["dpad_down"] = false; raw["dpad_left"] = false; raw["dpad_right"] = false; }
                raw["menu_back"] = bb || Raw("view") || Raw("menu");
                raw["blk_prev"] = BuildMode && bx; raw["blk_next"] = BuildMode && bb; raw["color_menu"] = BuildMode && by;
                if (BuildMode) { raw["crouch"] = false; raw["fx"] = false; raw["fy"] = false; raw["x"] = false; raw["y"] = false; }
                EdgePulse("blk_prev", now, 0.25); EdgePulse("blk_next", now, 0.25); EdgePulse("color_menu", now, 0.6);
                Drive("trigger_r", TrigR > (S("trigger_r").Level ? 0.35f : 0.55f));
                Drive("trigger_l", TrigL > (S("trigger_l").Level ? 0.35f : 0.55f));
                Drive("grip_l", GripL > (S("grip_l").Level ? 0.4f : 0.65f));
                Drive("grip_r", GripR > (S("grip_r").Level ? 0.4f : 0.65f));
                Drive("jump", Raw("jump"));
                Drive("crouch", Raw("crouch"));
                Drive("sprint", Raw("sprint"));
                Drive("jetpack", Raw("jetpack"));
                Drive("menu", Raw("menu"));
                S("grip_l").Analog = GripL; S("grip_r").Analog = GripR;

                // Steam Frame buttons: one press = one action, with a lockout so holding or bouncing does not spam
                EdgePulse("dpad_up", now, 0.35); EdgePulse("dpad_down", now, 0.35); EdgePulse("dpad_left", now, 0.35); EdgePulse("dpad_right", now, 0.35);
                EdgePulse("bump_l", now, 0.18); EdgePulse("bump_r", now, 0.18);
                EdgePulse("menu_back", now, 0.35);
                EdgePulse("view", now, 0.4); EdgePulse("fx", now, 0.35); EdgePulse("fy", now, 0.35);

                // X: tap = use, hold = terminal.   Y: tap = dampeners, hold = inventory
                // Touch (Quest ...): the terminal and the inventory have their own wrist buttons (K, I) and body zones, so X and Y only do one thing each
                if (touchStyle)
                {
                    bool xn = Raw("x"), yn = Raw("y");
                    if (xn && !xPrevTouch) Pulse("x_tap");
                    if (yn && !yPrevTouch) Pulse("y_tap");
                    xPrevTouch = xn; yPrevTouch = yn; xDown = yDown = 0f; xHoldFired = yHoldFired = false;
                }
                else
                {
                    Hold(Raw("x"), ref xDown, ref xHoldFired, dt, "x_tap", "x_hold");
                    Hold(Raw("y"), ref yDown, ref yHoldFired, dt, "y_tap", "y_hold");
                }
            }
        }

        static VrZones.Zone prevZoneL, prevZoneR;
        static bool tHeldL, tHeldR, gHeldL, gHeldR;

        static bool ZonesWanted(bool touchStyle)
        {
            string z = Plugin.Config.Zones;
            if (z == "on") return true;
            if (z == "off") return false;
            return true;
        }

        // a hand in a zone owns its trigger and grip: they fire the zone's action instead of the normal one
        static void ZoneHand(bool left, Xr.XrRuntime xr, VrZones.Zone z, ref float trig, ref float grip, ref VrZones.Zone prev, ref bool tHeld, ref bool gHeld)
        {
            bool tNow = trig > 0.6f, gNow = grip > 0.6f;
            if (z != VrZones.Zone.None)
            {
                if (prev == VrZones.Zone.None) xr.Haptic(left, 0.035f, 0.45f);        // short buzz when the hand enters a zone
                string n = "z_" + VrZones.Name(z);
                if (tNow && !tHeld) { Pulse(n + "_t"); xr.Haptic(left, 0.06f, 0.8f); }
                if (gNow && !gHeld) { Pulse(n + "_g"); xr.Haptic(left, 0.06f, 0.8f); }
                trig = 0f; grip = 0f;
            }
            tHeld = tNow; gHeld = gNow; prev = z;
        }

        static readonly Dictionary<string, bool> edgePrev = new Dictionary<string, bool>();
        static readonly Dictionary<string, double> edgeLast = new Dictionary<string, double>();

        static void EdgePulse(string name, double now, double minInterval)
        {
            bool down = Raw(name), prev;
            edgePrev.TryGetValue(name, out prev);
            edgePrev[name] = down;
            if (!down || prev) return;
            double last;
            if (edgeLast.TryGetValue(name, out last) && now - last < minInterval) return;
            edgeLast[name] = now;
            Pulse(name);
        }

        static void Hold(bool down, ref float t, ref bool fired, float dt, string tap, string hold)
        {
            if (down)
            {
                t += dt;
                if (!fired && t >= HoldSeconds) { fired = true; Pulse(hold); }
            }
            else
            {
                if (t > 0f && !fired) Pulse(tap);
                t = 0f; fired = false;
            }
        }

        // ------------------------------------------------------------ update-thread side

        /// <summary>Once per engine input tick: freeze the state the game will see this tick.</summary>
        public static void Tick()
        {
            Build();
            lock (gate)
            {
                double t = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
                foreach (var s in src.Values)
                {
                    bool now = s.Level || s.RiseLatch || t < s.HoldUntil;
                    s.NewPressedTick = s.RiseLatch || (now && !s.PrevTick);
                    s.NewReleasedTick = s.FallLatch || (!now && s.PrevTick);
                    s.LevelTick = now;
                    s.PrevTick = now;
                    s.RiseLatch = false; s.FallLatch = false;
                }
                seatLeft = !VrHead.OnFoot && (S("bump_l").NewPressedTick || S("z_lhip_g").NewPressedTick);
                seatRight = !VrHead.OnFoot && (S("bump_r").NewPressedTick || S("z_rhip_g").NewPressedTick);
                seatActivate = !VrHead.OnFoot && (S("jetpack").NewPressedTick || S("z_lhip_t").NewPressedTick || S("z_rhip_t").NewPressedTick);
                holster = VrHead.OnFoot && (S("z_lhip_t").NewPressedTick || S("z_rhip_t").NewPressedTick);
            }
            // cockpit hotbar: D-pad left/right moves the cursor, right stick click activates the slot under it
            if (seatLeft) VrToolbar.Move(-1);
            if (seatRight) VrToolbar.Move(1);
            if (seatActivate) VrToolbar.Activate();
            if (holster) VrToolbar.ToggleHolster();
        }
        static bool seatLeft, seatRight, seatActivate, holster;

        static bool Applies(ControlRow r)
        {
            if (r.Context == "foot") return VrHead.OnFoot;
            if (r.Context == "touchfoot") return VrHead.OnFoot && !(Xr.XrRuntime.Instance != null && Xr.XrRuntime.Instance.FrameProfile);
            if (r.Context == "seat") return !VrHead.OnFoot;
            return true;
        }

        public static void ExternalPulse(string name) { lock (gate) Pulse(name); }

        /// <summary>A menu is open and the player pressed B / view / menu: that is the Escape key.</summary>
        public static bool MenuBackPressed() { lock (gate) return S("menu_back").NewPressedTick; }

        public static bool Level(MyStringId id)
        {
            List<ControlRow> l;
            if (!byControl.TryGetValue(id, out l)) return false;
            lock (gate) foreach (var r in l) if (Applies(r) && r.Mode != "analog" && S(r.Source).LevelTick) return true;
            return false;
        }

        public static bool NewPressed(MyStringId id)
        {
            List<ControlRow> l;
            if (!byControl.TryGetValue(id, out l)) return false;
            lock (gate) foreach (var r in l) if (Applies(r) && S(r.Source).NewPressedTick) return true;
            return false;
        }

        public static bool NewReleased(MyStringId id)
        {
            List<ControlRow> l;
            if (!byControl.TryGetValue(id, out l)) return false;
            lock (gate) foreach (var r in l) if (Applies(r) && S(r.Source).NewReleasedTick) return true;
            return false;
        }

        public static float Analog(MyStringId id)
        {
            List<ControlRow> l;
            if (!byControl.TryGetValue(id, out l)) return 0f;
            float best = 0f;
            lock (gate)
                foreach (var r in l)
                {
                    if (!Applies(r)) continue;
                    var s = S(r.Source);
                    float v = r.Mode == "analog" ? s.Analog : (s.LevelTick ? 1f : 0f);
                    if (v > best) best = v;
                }
            return best;
        }
    }
}


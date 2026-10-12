using System;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Components;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using SeVr.Xr;
using VRageMath;

namespace SeVr.Vr
{
    /// <summary>
    /// The player character's own arms follow the controllers. The game already has a two-bone arm IK for holding tools
    /// (MyCharacter.CalculateHandIK); it is run again after the normal animation, with the controller poses as targets.
    /// </summary>
    internal static class VrHands
    {
        public static bool Enabled = Environment.GetEnvironmentVariable("SEVR_NOHANDIK") != "1";
        // palm frame relative to the controller's aim pose (calibrated against the rig's palm bone axes)
        public static Matrix PalmOffsetL = Matrix.CreateRotationY(-MathHelper.PiOver2), PalmOffsetR = Matrix.CreateRotationY(-MathHelper.PiOver2);

        /// <summary>Turn the hands about the pointing direction: with 90 degrees the palms face each other, like hands holding the controllers.</summary>
        public static void SetRoll(float degrees)
        {
            float r = MathHelper.ToRadians(degrees);
            PalmOffsetL = Matrix.CreateRotationY(-MathHelper.PiOver2) * Matrix.CreateRotationZ(r);
            PalmOffsetR = Matrix.CreateRotationY(-MathHelper.PiOver2) * Matrix.CreateRotationZ(-r);
        }
        public static float ReachFraction = 1.12f;   // the hand may go 12 percent beyond the arm's length: arms look longer, hardly any rubber effect
        public static float ShoulderForward = float.Parse(Environment.GetEnvironmentVariable("SEVR_SHOULDER") ?? "0", System.Globalization.CultureInfo.InvariantCulture);   // share of the arm's full length the hand may be pulled to
        static MethodInfo calcIk;
        static AccessTools.FieldRef<MyCharacter, int> lUp, lFore, lPalm, rUp, rFore, rPalm;
        static int logCount;

        /// <summary>True while the character's arms are driven (the simple block hands are then not drawn).</summary>
        public static bool Driving { get { return Enabled && VrHead.OnFoot; } }

        public static void Install(Harmony h)
        {
            var t = typeof(MyCharacter);
            foreach (var mi in t.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                if (mi.Name == "CalculateHandIK" && mi.GetParameters().Length == 4) calcIk = mi;   // (upperarm, forearm, palm, ref target)
            lUp = AccessTools.FieldRefAccess<MyCharacter, int>("m_leftHandIKStartBone");
            lFore = AccessTools.FieldRefAccess<MyCharacter, int>("m_leftForearmBone");
            lPalm = AccessTools.FieldRefAccess<MyCharacter, int>("m_leftHandIKEndBone");
            rUp = AccessTools.FieldRefAccess<MyCharacter, int>("m_rightHandIKStartBone");
            rFore = AccessTools.FieldRefAccess<MyCharacter, int>("m_rightForearmBone");
            rPalm = AccessTools.FieldRefAccess<MyCharacter, int>("m_rightHandIKEndBone");
            skinned = AccessTools.Field(typeof(MyRenderComponentSkinnedEntity), "m_skinnedEntity");
            h.Patch(AccessTools.Method(typeof(MyRenderComponentSkinnedEntity), "Draw"), prefix: new HarmonyMethod(typeof(VrHands).GetMethod(nameof(DrawPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
            // tools and weapons: position and aim come from the right controller
            var wp = typeof(Sandbox.Game.Entities.Character.Components.MyCharacterWeaponPositionComponent);
            h.Patch(AccessTools.Method(wp, "UpdateLogicalWeaponPosition"), postfix: new HarmonyMethod(typeof(VrHands).GetMethod(nameof(WeaponLogicalPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            h.Patch(AccessTools.Method(wp, "UpdateGraphicalWeaponPosition"), postfix: new HarmonyMethod(typeof(VrHands).GetMethod(nameof(WeaponGraphicalPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            h.Patch(AccessTools.Method(t, "UpdateShootDirection"), postfix: new HarmonyMethod(typeof(VrHands).GetMethod(nameof(ShootDirPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            h.Patch(AccessTools.Method(t, "GetAimedPointFromHead"), postfix: new HarmonyMethod(typeof(VrHands).GetMethod(nameof(AimPointPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            h.Patch(AccessTools.Method(t, "GetAimedPointFromCamera"), postfix: new HarmonyMethod(typeof(VrHands).GetMethod(nameof(AimPointPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            h.Patch(AccessTools.Method(t, "UpdateHeadAndWeapon"), postfix: new HarmonyMethod(typeof(VrHands).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic)));
            InstallUseAim(h);
        }

        // ---- what the character can use or select (doors, terminals, cockpits, blocks) is found with a ray from the head; with HandAimUse it
        // comes from the right controller instead. The detectors ask for the head matrix while they work, so that is what is replaced then.
        [ThreadStatic] static bool inDetect;

        static void InstallUseAim(Harmony h)
        {
            try
            {
                var asm = typeof(MyCharacter).Assembly;
                var pre = new HarmonyMethod(typeof(VrHands).GetMethod(nameof(DetectPrefix), BindingFlags.Static | BindingFlags.NonPublic));
                var fin = new HarmonyMethod(typeof(VrHands).GetMethod(nameof(DetectFinalizer), BindingFlags.Static | BindingFlags.NonPublic));
                foreach (var tn in new[] { "MyCharacterClosestDetectorComponent", "MyCharacterRaycastDetectorComponent", "MyCharacterShapecastDetectorComponent" })
                {
                    var dt = asm.GetType("Sandbox.Game.Entities.Character.Components." + tn);
                    if (dt == null) continue;
                    foreach (var mi in dt.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (mi.Name != "DoDetection") continue;
                        var body = mi.GetMethodBody();
                        if (body == null || body.GetILAsByteArray().Length < 20) continue;
                        h.Patch(mi, prefix: pre, finalizer: fin);
                    }
                }
                var post = new HarmonyMethod(typeof(VrHands).GetMethod(nameof(HeadMatrixPostfix), BindingFlags.Static | BindingFlags.NonPublic));
                foreach (var mi in typeof(MyCharacter).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (mi.Name == "GetHeadMatrix" && mi.ReturnType == typeof(MatrixD)) h.Patch(mi, postfix: post);
            }
            catch (Exception e) { Plugin.Log("use aim install: " + e.Message); }
        }

        static void DetectPrefix() { inDetect = true; }
        static void DetectFinalizer() { inDetect = false; }

        static void HeadMatrixPostfix(MyCharacter __instance, ref MatrixD __result)
        {
            if (!inDetect || !Plugin.Config.HandAimUse) return;
            try
            {
                var s = MySession.Static;
                var xr = XrRuntime.Instance;
                if (!Driving || !VrRenderer.Active || xr == null || !xr.HandRValid || s == null || __instance != s.LocalCharacter) return;
                MatrixD hand = VrHead.EyeWorldUpd(VrRenderer.PoseMatrix(xr.HandR));
                __result = MatrixD.CreateWorld(hand.Translation, hand.Forward, hand.Up);
            }
            catch { }
        }

        static FieldInfo shootDirField;
        static bool weaponLogged;

        /// <summary>The weapon or tool held by the character sits in the right controller's hand and shoots where it points.</summary>
        static void PlaceWeapon(MyCharacter ch)
        {
            var xr = XrRuntime.Instance;
            if (!Driving || !VrRenderer.Active || xr == null || !xr.HandRValid) return;
            var gun = ch.CurrentWeapon as VRage.Game.Entity.MyEntity;
            if (gun == null) return;
            MatrixD hand = VrHead.EyeWorldUpd(VrRenderer.PoseMatrix(xr.HandR));
            MatrixD wm = MatrixD.CreateWorld(hand.Translation, hand.Forward, hand.Up);
            gun.WorldMatrix = wm;
            if (!weaponLogged) { weaponLogged = true; Plugin.Log("weapon: placed " + gun.GetType().Name + " in the right hand"); }
        }

        static void ShootDirPostfix(MyCharacter __instance)
        {
            try
            {
                var s = MySession.Static;
                var xr = XrRuntime.Instance;
                if (!Driving || !VrRenderer.Active || xr == null || !xr.HandRValid || s == null || __instance != s.LocalCharacter) return;
                if (shootDirField == null) shootDirField = AccessTools.Field(typeof(MyCharacter), "ShootDirection");
                MatrixD hand = VrHead.EyeWorldUpd(VrRenderer.PoseMatrix(xr.HandR));
                shootDirField.SetValue(__instance, (Vector3)Vector3D.Normalize(hand.Forward));
            }
            catch (Exception e) { if (logCount++ < 5) Plugin.Log("shoot dir: " + e.Message); }
        }

        static void AimPointPostfix(MyCharacter __instance, ref Vector3D __result)
        {
            var s = MySession.Static;
            var xr = XrRuntime.Instance;
            if (!Driving || !VrRenderer.Active || xr == null || !xr.HandRValid || s == null || __instance != s.LocalCharacter) return;
            MatrixD hand = VrHead.EyeWorldUpd(VrRenderer.PoseMatrix(xr.HandR));
            __result = hand.Translation + Vector3D.Normalize(hand.Forward) * 200.0;
        }

        static void SetProp(object o, string name, Vector3D v)
        {
            var m = AccessTools.Property(o.GetType(), name).GetSetMethod(true);
            m.Invoke(o, new object[] { v });
        }

        static bool WeaponHand(Sandbox.Game.Entities.Character.Components.MyCharacterWeaponPositionComponent c, out MatrixD hand)
        {
            hand = MatrixD.Identity;
            var s = MySession.Static;
            var xr = XrRuntime.Instance;
            if (!Driving || !VrRenderer.Active || xr == null || !xr.HandRValid || s == null || c.Character != s.LocalCharacter) return false;
            hand = VrHead.EyeWorldUpd(VrRenderer.PoseMatrix(xr.HandR));
            return true;
        }

        static void WeaponLogicalPostfix(Sandbox.Game.Entities.Character.Components.MyCharacterWeaponPositionComponent __instance)
        {
            try
            {
                MatrixD hand;
                if (!WeaponHand(__instance, out hand)) return;
                Vector3D dir = Vector3D.Normalize(hand.Forward);
                SetProp(__instance, "LogicalPositionWorld", hand.Translation);
                SetProp(__instance, "LogicalOrientationWorld", dir);
                SetProp(__instance, "LogicalCrosshairPoint", hand.Translation + dir * 200.0);
            }
            catch (Exception e) { if (logCount++ < 5) Plugin.Log("weapon logical: " + e.Message); }
        }

        static void WeaponGraphicalPostfix(Sandbox.Game.Entities.Character.Components.MyCharacterWeaponPositionComponent __instance)
        {
            try
            {
                MatrixD hand;
                if (WeaponHand(__instance, out hand)) SetProp(__instance, "GraphicalPositionWorld", hand.Translation);
            }
            catch (Exception e) { if (logCount++ < 5) Plugin.Log("weapon graphical: " + e.Message); }
        }

        static void Postfix(MyCharacter __instance)
        {
            try
            {
                if (!Driving || !VrRenderer.Active) return;
                var s = MySession.Static;
                if (s == null || __instance != s.LocalCharacter) return;
                var xr = XrRuntime.Instance;
                bool any = false;
                if (xr.HandLValid) { Solve(__instance, lUp(__instance), lFore(__instance), lPalm(__instance), xr.BodyHandL, PalmOffsetL); any = true; }
                if (xr.HandRValid) { Solve(__instance, rUp(__instance), rFore(__instance), rPalm(__instance), xr.BodyHandR, PalmOffsetR); any = true; }
                PlaceWeapon(__instance);
                if (any)
                {
                    __instance.AnimationController.UpdateTransformations();
                }
            }
            catch (Exception e) { if (logCount++ < 5) Plugin.Log("hands: " + e.Message); }
        }

        /// <summary>Hands float at the controllers with the arms hidden (SEVR_ARMS=1 brings the arms and their reach-limited IK back).</summary>
        public static bool FloatingHands = Environment.GetEnvironmentVariable("SEVR_ARMS") != "1";

        static FieldInfo skinned; static int dbg;
        static readonly bool CollapseCollar = Environment.GetEnvironmentVariable("SEVR_COLLAR") != "0";

        // hide the arms right before the skeleton goes to the renderer: collapse the upper arm and forearm skin onto the hand
        static void DrawPrefix(MyRenderComponentSkinnedEntity __instance)
        {
            try
            {
                if (!Driving || !FloatingHands || !VrRenderer.Active) return;
                var ch = skinned.GetValue(__instance) as MyCharacter;
                var s = MySession.Static;
                if (ch == null || s == null || ch != s.LocalCharacter) return;
                var xr = XrRuntime.Instance;
                var abs = ch.BoneAbsoluteTransforms;
                if (dbg == 0)
                {
                    var cb = ch.AnimationController.CharacterBones;
                    var sb = new System.Text.StringBuilder("bones:");
                    for (int bi = 0; bi < cb.Length; bi++) sb.Append(" " + bi + "=" + cb[bi].Name + "<" + (cb[bi].Parent == null ? -1 : cb[bi].Parent.Index));
                    Plugin.Log(sb.ToString());
                }
                if (dbg++ < 3) Plugin.Log("arm collapse: bones L " + lUp(ch) + "," + lFore(ch) + "," + lPalm(ch) + " R " + rUp(ch) + "," + rFore(ch) + "," + rPalm(ch) + " count " + (abs == null ? -1 : abs.Length));
                PlaceWeapon(ch);
                if (xr.HandLValid && xr.HandRValid) HideBody(ch, abs, lPalm(ch), rPalm(ch), lUp(ch), rUp(ch));
            }
            catch (Exception e) { if (logCount++ < 5) Plugin.Log("hands draw: " + e.Message); }
        }

        // Hide the whole body but the hands: every skeleton bone that is not a palm or finger is collapsed onto the eye point.
        // Vertices that are only partly attached to the hand then stretch along the line towards the eye, which is invisible.
        static void HideBody(MyCharacter ch, Matrix[] abs, int palmL, int palmR, int upL, int upR)
        {
            var bones = ch.AnimationController.CharacterBones;
            if (abs == null || bones == null || palmL < 0 || palmR < 0) return;
            if (palmL >= abs.Length || palmR >= abs.Length) return;
            Vector3 pl = abs[palmL].Translation, pr = abs[palmR].Translation;
            Matrix cl = Matrix.CreateScale(0.0001f), cr = cl;
            cl.Translation = pl; cr.Translation = pr;
            for (int i = 0; i < bones.Length && i < abs.Length; i++)
            {
                string n = bones[i].Name;
                if (n == null || !n.StartsWith("SE_Rig")) continue;
                bool rightHand = i == palmR || Descends(bones, i, palmR);
                if (rightHand && ch.CurrentWeapon != null && !VrBuild.BuilderActive()) { abs[i] = cr; continue; }   // a tool is held: it replaces the right hand
                if (i == palmL || rightHand || Descends(bones, i, palmL)) continue;   // hands and fingers stay
                // collapse onto the nearer palm: vertices shared with the hand then stretch to nothing
                // the side comes from the skeleton (arm chain), not from the distance, so crossed hands do not pull streaks across
                bool left = IsUnder(bones, i, upL), right = IsUnder(bones, i, upR);
                if (left == right) left = Vector3.DistanceSquared(abs[i].Translation, pl) <= Vector3.DistanceSquared(abs[i].Translation, pr);
                abs[i] = left ? cl : cr;
            }
        }
        // the bone is the upper arm, below it, or its collarbone (parent)
        static bool IsUnder(VRageRender.Animations.MyCharacterBone[] bones, int index, int upperArm)
        {
            if (upperArm < 0 || upperArm >= bones.Length) return false;
            if (index == upperArm || Descends(bones, index, upperArm)) return true;
            var p = bones[upperArm].Parent;
            return p != null && p.Index == index;
        }
        static bool Descends(VRageRender.Animations.MyCharacterBone[] bones, int index, int ancestor)
        {
            var b = bones[index].Parent;
            int guard = 0;
            while (b != null && guard++ < 64) { if (b.Index == ancestor) return true; b = b.Parent; }
            return false;
        }
        static void Solve(MyCharacter c, int up, int fore, int palm, N.Pose pose, Matrix offset)
        {
            if (up < 0 || fore < 0 || palm < 0) return;
            MatrixD world = VrHead.EyeWorldUpd(VrRenderer.PoseMatrix(pose));
            MatrixD target = (MatrixD)offset * world;
            // the hand model sits slightly ahead of the controller: pull it back along the pointing direction (HandBack, centimetres)
            if (Plugin.Config.HandBack != 0f) target.Translation -= Vector3D.Normalize(world.Forward) * (Plugin.Config.HandBack * 0.01);
            if (Plugin.Config.HandDown != 0f) target.Translation -= Vector3D.Normalize(c.PositionComp.WorldMatrixRef.Up) * (Plugin.Config.HandDown * 0.01);
            if (FloatingHands)
            {
                var pb = c.AnimationController.CharacterBones;
                if (pb == null || palm >= pb.Length) return;
                Matrix local = (Matrix)(target * c.PositionComp.WorldMatrixNormalizedInv);
                pb[palm].SetCompleteTransformFromAbsoluteMatrix(ref local, false);
                return;
            }

            // the character's arm is shorter than yours: keep the hand within its reach so the arm does not stretch like rubber
            var bones = c.AnimationController.CharacterBones;
            if (bones != null && up < bones.Length && fore < bones.Length && palm < bones.Length)
            {
                Vector3 pUp = bones[up].AbsoluteTransform.Translation, pFore = bones[fore].AbsoluteTransform.Translation, pPalm = bones[palm].AbsoluteTransform.Translation;
                float reach = (Vector3.Distance(pUp, pFore) + Vector3.Distance(pFore, pPalm)) * ReachFraction;
                MatrixD wm = c.PositionComp.WorldMatrixRef;
                Vector3D local = Vector3D.Transform(target.Translation, c.PositionComp.WorldMatrixNormalizedInv);
                Vector3D d = local - (Vector3D)pUp;
                double len = d.Length();
                if (len > reach && len > 1e-6)
                {
                    local = (Vector3D)pUp + d * (reach / len);
                    target.Translation = Vector3D.Transform(local, wm);
                }
            }
            calcIk.Invoke(c, new object[] { up, fore, palm, target });
        }
    }
}


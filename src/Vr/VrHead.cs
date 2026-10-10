using System;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using VRageMath;

namespace SeVr.Vr
{
    /// <summary>
    /// Head steering ("head = mouse") for a character on foot.
    /// The headset's absolute orientation (zero latency) is what is rendered, composed with a base rotation M that maps the
    /// tracking space onto the world: M keeps +Y on the character's up vector (parallel-transported as the character moves
    /// through gravity changes) and its heading only changes when the player turns with the thumbstick. The game's own
    /// camera is then steered towards the head direction by feeding rotation into the engine's mouse-look
    /// (closed loop), so tools, aim and walking direction follow where you look.
    /// </summary>
    internal static class VrHead
    {
        public static Matrix MoveToBody = Matrix.Identity;   // stick vector (head frame) -> character's local axes
        public static bool Jetpack;               // jetpack running: the right stick pitches instead of moving up/down
        public static bool OnFoot;                 // head-steered character (not in a seat, first person)
        public static Matrix BaseM = Matrix.Identity;
        public static Vector3 HeadRefPos;          // tracking-space position of the head when the base was set
        public static Vector2 Inject;              // rotation indicator for the engine (x pitch, y yaw), updated per render frame
        public static float TurnRateDegPerSec = 150f;
        public static float PitchRateDegPerSec = 90f;
        public static float Gain = 0.35f;          // fraction of the remaining angle corrected per engine tick
        static bool eyeOffInit, eyeBaseValid;
        static Vector3 eyeOff;
        static Vector3D EyeBase;
        const float EyeSmoothSeconds = 0.15f;
        static bool baseInit, refInit, havePrevRot;
        static Matrix prevRot;
        static Vector3 lastUp = Vector3.Up;
        static int logCount;

        // degrees of body yaw / head pitch per rotation-indicator unit on foot (from MyCharacter.MoveAndRotateInternal)
        const float YawDegPerUnit = 0.02f * 0.13f * 57.29578f;
        const float PitchDegPerUnit = 0.13f;

        public static void Reset() { baseInit = false; refInit = false; Inject = Vector2.Zero; }

        static MyCharacter FootCharacter()
        {
            try
            {
                var s = MySession.Static;
                if (s == null) return null;
                var c = s.ControlledEntity != null ? s.ControlledEntity.Entity as MyCharacter : null;
                if (c == null || c != s.LocalCharacter || c.IsDead) return null;
                if (!c.IsInFirstPersonView) return null;
                return c;
            }
            catch { return null; }
        }

        /// <summary>Once per render frame, before the eyes are positioned.</summary>
        public static void BeginFrame(MatrixD camWorld, Matrix headLocal, Vector3 headLocalPos, float dt, float turnStick, float pitchStick)
        {
            var ch = FootCharacter();
            if (!refInit) { HeadRefPos = headLocalPos; refInit = true; }
            else
            {
                // The camera stays at the character's head. The reference point follows the real head slowly, so walking around
                // the room (or a bad start position) does not leave the view outside the body; quick leaning still shows parallax.
                Vector3 delta = headLocalPos - HeadRefPos;
                float aH = 1f - (float)Math.Exp(-dt / 1.2f), aV = 1f - (float)Math.Exp(-dt / 12f);
                HeadRefPos.X += delta.X * aH; HeadRefPos.Z += delta.Z * aH; HeadRefPos.Y += delta.Y * aV;
            }
            if (ch == null) { OnFoot = false; Inject = Vector2.Zero; baseInit = false; eyeOffInit = false; eyeBaseValid = false; return; }
            OnFoot = true;

            MatrixD body = ch.PositionComp.WorldMatrixRef;
            // A freshly spawned character (new world) can have an invalid orientation for a moment; anything computed from it
            // would be NaN, and NaN fed into the engine's rotation input ruins the character. Wait until everything is valid.
            if (!Valid((Vector3)body.Up) || !Valid((Vector3)body.Right) || !Valid((Vector3)body.Forward) || !Valid((Vector3)camWorld.Forward) || !Valid((Vector3)camWorld.Translation)
                || !Valid(headLocal.Forward) || !Valid(headLocalPos))
            { OnFoot = false; Inject = Vector2.Zero; baseInit = false; eyeOffInit = false; eyeBaseValid = false; return; }
            if (baseInit && !ValidM(BaseM)) baseInit = false;

            // The engine's camera sits on the animated head bone, which sways a little (turning, idle, footsteps) and shows up as a slight
            // tremble. Keep the eye point at the same place relative to the body, low-pass filtered, and use that instead.
            if (Plugin.Config.EyeSmooth > 0f)       // EyeSmooth=<seconds> in SeVr.cfg, off by default
            {
                MatrixD ori = body.GetOrientation();
                Vector3 offLocal = (Vector3)Vector3D.TransformNormal(camWorld.Translation - body.Translation, MatrixD.Transpose(ori));
                if (Valid(offLocal))
                {
                    if (!eyeOffInit) { eyeOff = offLocal; eyeOffInit = true; }
                    else eyeOff += (offLocal - eyeOff) * (1f - (float)Math.Exp(-dt / Plugin.Config.EyeSmooth));
                    EyeBase = body.Translation + Vector3D.TransformNormal((Vector3D)eyeOff, ori);
                    eyeBaseValid = true;
                }
            }
            Vector3 U = Vector3.Normalize((Vector3)body.Up);
            Vector3 bodyR = Vector3.Normalize((Vector3)body.Right), bodyF = Vector3.Normalize((Vector3)body.Forward);

            if (!baseInit)
            {
                // tracking +Y -> U, and the current head heading -> the camera's heading
                BaseM = ArcMatrix(Vector3.Up, U);
                Vector3 camF = Flatten((Vector3)camWorld.Forward, U);
                Vector3 headF = Flatten(Vector3.TransformNormal(headLocal.Forward, BaseM), U);
                float a = SignedAngle(headF, camF, U);
                BaseM = BaseM * Matrix.CreateFromAxisAngle(U, a);
                lastUp = U;
                baseInit = true;
                return;
            }

            // keep the base attached to the character's up vector while gravity/orientation changes.
            // With the jetpack the body itself pitches and rolls to follow the head, so the base must stay fixed in the world,
            // otherwise it would turn with the body and the loop would run away (very twitchy head control).
            bool jet = false;
            try { jet = ch.JetpackRunning; } catch { }
            Jetpack = jet;
            if (!jet && Vector3.Dot(lastUp, U) < 0.99999f) BaseM = BaseM * ArcMatrix(lastUp, U);
            if (!jet)
            {
                // after flying, level the view with the character again (smoothly) so the horizon is not left tilted
                Vector3 mu = Vector3.Normalize(Vector3.TransformNormal(Vector3.Up, BaseM));
                float d = MathHelper.Clamp(Vector3.Dot(mu, U), -1f, 1f);
                if (d < 0.99995f)
                {
                    float ang = (float)Math.Acos(d);
                    float step = Math.Min(ang, 1.4f * dt);   // about 80 degrees per second
                    Vector3 ax = Vector3.Cross(mu, U);
                    if (ax.LengthSquared() > 1e-8f) BaseM = BaseM * Matrix.CreateFromAxisAngle(Vector3.Normalize(ax), step);
                }
            }
            lastUp = U;

            // thumbstick turning rotates the player about their up axis (right = clockwise seen from above)
            if (Math.Abs(turnStick) > 0.01f)
                BaseM = BaseM * Matrix.CreateFromAxisAngle(U, -turnStick * TurnRateDegPerSec * dt * 0.017453292f);

            // jetpack: the right stick also pitches the player up and down (head-to-neck: the whole body follows)
            if (jet && Math.Abs(pitchStick) > 0.01f)
                BaseM = BaseM * Matrix.CreateFromAxisAngle(bodyR, pitchStick * PitchRateDegPerSec * dt * 0.017453292f);

            // jetpack roll (freeze grip + D-pad): the game rolls the body, the view must roll with it. Only the body's roll about its own
            // forward axis since the last frame is carried over to the base; pitch and yaw stay with the closed loop.
            Matrix curRot = (Matrix)body.GetOrientation();
            if (jet && havePrevRot)
            {
                Matrix rw = Matrix.Transpose(prevRot) * curRot;
                Quaternion q = Quaternion.CreateFromRotationMatrix(rw);
                if (q.W < 0) q = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);
                float sn = (float)Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z);
                if (sn > 1e-6f)
                {
                    Vector3 wv = new Vector3(q.X, q.Y, q.Z) * (2f * (float)Math.Atan2(sn, q.W) / sn);
                    float rollDelta = Vector3.Dot(wv, bodyF);
                    if (Math.Abs(rollDelta) > 1e-5f && Math.Abs(rollDelta) < 0.5f) BaseM = BaseM * Matrix.CreateFromAxisAngle(bodyF, rollDelta);
                }
            }
            prevRot = curRot; havePrevRot = jet;

            // closed loop: steer the engine's camera towards where the head points
            Vector3 fD = Vector3.Normalize(Vector3.TransformNormal(headLocal.Forward, BaseM));
            Vector3 fC = Vector3.Normalize((Vector3)camWorld.Forward);
            float yawD = (float)Math.Atan2(Vector3.Dot(fD, bodyR), Vector3.Dot(fD, bodyF));
            float yawC = (float)Math.Atan2(Vector3.Dot(fC, bodyR), Vector3.Dot(fC, bodyF));
            float pitchD = (float)Math.Asin(MathHelper.Clamp(Vector3.Dot(fD, U), -1f, 1f));
            float pitchC = (float)Math.Asin(MathHelper.Clamp(Vector3.Dot(fC, U), -1f, 1f));
            float eYaw = MathHelper.WrapAngle(yawD - yawC) * 57.29578f;
            float ePitch = (pitchD - pitchC) * 57.29578f;
            // Walking and flying follow the gaze, not the (slightly lagging) body: express the stick vector, given in the head's frame,
            // in the body's own axes. On the ground only the heading of the gaze counts; with the jetpack the full head orientation does.
            {
                Matrix hr = headLocal; hr.Translation = Vector3.Zero;
                Matrix frame;
                if (jet) frame = hr * BaseM;
                else
                {
                    Vector3 fF = Flatten(fD, U);
                    frame = Matrix.Identity;
                    frame.Right = Vector3.Normalize(Vector3.Cross(fF, U));
                    frame.Up = U;
                    frame.Backward = -fF;
                }
                Matrix bodyRot = (Matrix)body.GetOrientation();
                MoveToBody = frame * Matrix.Transpose(bodyRot);
            }

            float y = Gain * eYaw / YawDegPerUnit;       // positive = turn right
            float x = -Gain * ePitch / (jet ? YawDegPerUnit : PitchDegPerUnit);  // positive = look down
            Inject = new Vector2(MathHelper.Clamp(x, -60f, 60f), MathHelper.Clamp(y, -60f, 60f));
            if (!Valid(new Vector3(Inject, 0f)) || !ValidM(BaseM) || !ValidM(MoveToBody)) { Inject = Vector2.Zero; baseInit = false; MoveToBody = Matrix.Identity; return; }
            if (logCount++ % 90 == 0) Plugin.Log("head: errYaw " + eYaw.ToString("0.0") + " errPitch " + ePitch.ToString("0.0") + " inject " + Inject.X.ToString("0.0") + "," + Inject.Y.ToString("0.0"));
        }

        /// <summary>World matrix of an eye given its tracking-space pose.</summary>
        public static MatrixD EyeWorld(Matrix eyeLocal, MatrixD camWorld)
        {
            if (!OnFoot)
            {
                Matrix e = eyeLocal; e.Translation = eyeLocal.Translation - HeadRefPos;
                return (MatrixD)e * camWorld;
            }
            Matrix rot = eyeLocal; rot.Translation = Vector3.Zero;
            Matrix w = rot * BaseM;
            Vector3 off = Vector3.TransformNormal(eyeLocal.Translation - HeadRefPos, BaseM);
            MatrixD m = (MatrixD)w;
            m.Translation = (eyeBaseValid ? EyeBase : camWorld.Translation) + (Vector3D)off;
            return m;
        }

        static bool Valid(Vector3 v) { return !(float.IsNaN(v.X) || float.IsNaN(v.Y) || float.IsNaN(v.Z) || float.IsInfinity(v.X) || float.IsInfinity(v.Y) || float.IsInfinity(v.Z)) && v.LengthSquared() > 1e-6f; }
        static bool ValidM(Matrix m)
        {
            return Valid(m.Right) && Valid(m.Up) && Valid(m.Backward);
        }

        static Matrix ArcMatrix(Vector3 from, Vector3 to)
        {
            float d = Vector3.Dot(from, to);
            if (d > 0.999999f) return Matrix.Identity;
            if (d < -0.999999f)
            {
                Vector3 ax = Vector3.Cross(from, Vector3.Right);
                if (ax.LengthSquared() < 1e-4f) ax = Vector3.Cross(from, Vector3.Forward);
                return Matrix.CreateFromAxisAngle(Vector3.Normalize(ax), (float)Math.PI);
            }
            Vector3 axis = Vector3.Normalize(Vector3.Cross(from, to));
            return Matrix.CreateFromAxisAngle(axis, (float)Math.Acos(MathHelper.Clamp(d, -1f, 1f)));
        }

        static Vector3 Flatten(Vector3 v, Vector3 up)
        {
            Vector3 f = v - up * Vector3.Dot(v, up);
            if (f.LengthSquared() < 1e-6f) return Vector3.Normalize(Vector3.Cross(up, Vector3.Right));
            return Vector3.Normalize(f);
        }

        static float SignedAngle(Vector3 a, Vector3 b, Vector3 axis)
        {
            float ang = (float)Math.Acos(MathHelper.Clamp(Vector3.Dot(a, b), -1f, 1f));
            return Vector3.Dot(Vector3.Cross(a, b), axis) < 0 ? -ang : ang;
        }
    }
}

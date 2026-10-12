using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using VRageMath;
using Buffer = SharpDX.Direct3D11.Buffer;
using Color = System.Drawing.Color;
using FillMode = SharpDX.Direct3D11.FillMode;
using Device = SharpDX.Direct3D11.Device;
using Matrix = VRageMath.Matrix;
using Vector3 = VRageMath.Vector3;

namespace SeVr.Vr
{
    /// <summary>
    /// Own drawing into the finished eye image: world markers (billboards with text) and the player's hands (simple solid models).
    /// Runs on the render thread right after the engine drew the eye, before the image is copied to the headset.
    /// </summary>
    internal static class VrOverlay
    {
        const string BillboardHlsl = @"
cbuffer C : register(b0) { row_major float4x4 P; };
Texture2D tex : register(t0); SamplerState smp : register(s0);
struct VI { float3 pos : POSITION; float2 uv : TEXCOORD0; float4 col : COLOR0; };
struct PI { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float4 col : COLOR0; };
PI VS(VI i) { PI o; o.pos = mul(float4(i.pos, 1), P); o.uv = i.uv; o.col = i.col; return o; }
float4 PS(PI i) : SV_Target { float4 c = tex.Sample(smp, i.uv); return float4(c.rgb * i.col.rgb * i.col.a, c.a * i.col.a); }
float4 PSH(PI i) : SV_Target { float4 c = tex.Sample(smp, i.uv); return float4(c.rgb * c.a * i.col.a, c.a * i.col.a); }";

        const string MeshHlsl = @"
cbuffer C : register(b0) { row_major float4x4 WVP; row_major float4x4 WV; float4 color; };
struct VI { float3 pos : POSITION; float3 nrm : NORMAL; };
struct PI { float4 pos : SV_POSITION; float3 nrm : TEXCOORD0; };
PI VS(VI i) { PI o; o.pos = mul(float4(i.pos, 1), WVP); o.nrm = mul(i.nrm, (float3x3)WV); return o; }
float4 PS(PI i) : SV_Target { float3 n = normalize(i.nrm); float l = saturate(dot(n, normalize(float3(0.3, 0.7, 0.6)))) * 0.55 + 0.45; return float4(color.rgb * l, 1); }";

        static bool initTried, ready;
        static Device dev;
        static VertexShader vsB, vsM; static PixelShader psB, psM, psH;
        /// <summary>The HUD picture the game drew its markers into (straight alpha), and the field of view it was drawn for.</summary>
        public static ShaderResourceView HudSrv; public static int HudW, HudH; public static float HudTanX = 1f, HudTanY = 1f;
        static InputLayout ilB, ilM;
        static Buffer cbB, cbM, vbB, vbHand;
        static BlendState blend; static RasterizerState raster; static DepthStencilState noDepth; static SamplerState sampler;
        static int handVerts;
        const int MaxBillVerts = 6 * 64;

        sealed class Tex { public Texture2D T; public ShaderResourceView V; public float Aspect; public long Used; }
        static readonly Dictionary<string, Tex> cache = new Dictionary<string, Tex>();

        [StructLayout(LayoutKind.Sequential)] struct BV { public float x, y, z, u, v, r, g, b, a; }

        static bool Init(Device device)
        {
            if (initTried) return ready;
            initTried = true;
            try
            {
                dev = device;
                var b = ShaderBytecode.Compile(BillboardHlsl, "VS", "vs_5_0");
                var bp = ShaderBytecode.Compile(BillboardHlsl, "PS", "ps_5_0");
                var m = ShaderBytecode.Compile(MeshHlsl, "VS", "vs_5_0");
                var mp = ShaderBytecode.Compile(MeshHlsl, "PS", "ps_5_0");
                if (b.HasErrors || bp.HasErrors || m.HasErrors || mp.HasErrors) { Plugin.Log("overlay: shader error " + b.Message + bp.Message + m.Message + mp.Message); return false; }
                vsB = new VertexShader(dev, b.Bytecode); psB = new PixelShader(dev, bp.Bytecode);
                var bh = ShaderBytecode.Compile(BillboardHlsl, "PSH", "ps_5_0");
                if (bh.HasErrors) { Plugin.Log("overlay: PSH error " + bh.Message); return false; }
                psH = new PixelShader(dev, bh.Bytecode);
                vsM = new VertexShader(dev, m.Bytecode); psM = new PixelShader(dev, mp.Bytecode);
                ilB = new InputLayout(dev, ShaderSignature.GetInputSignature(b.Bytecode), new[] {
                    new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                    new InputElement("TEXCOORD", 0, Format.R32G32_Float, 12, 0),
                    new InputElement("COLOR", 0, Format.R32G32B32A32_Float, 20, 0) });
                ilM = new InputLayout(dev, ShaderSignature.GetInputSignature(m.Bytecode), new[] {
                    new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                    new InputElement("NORMAL", 0, Format.R32G32B32_Float, 12, 0) });
                cbB = new Buffer(dev, 64, ResourceUsage.Dynamic, BindFlags.ConstantBuffer, CpuAccessFlags.Write, ResourceOptionFlags.None, 0);
                cbM = new Buffer(dev, 144, ResourceUsage.Dynamic, BindFlags.ConstantBuffer, CpuAccessFlags.Write, ResourceOptionFlags.None, 0);
                vbB = new Buffer(dev, MaxBillVerts * 36, ResourceUsage.Dynamic, BindFlags.VertexBuffer, CpuAccessFlags.Write, ResourceOptionFlags.None, 0);

                var hand = BuildHand();
                handVerts = hand.Length / 6;
                vbHand = Buffer.Create(dev, BindFlags.VertexBuffer, hand);

                var bd = new BlendStateDescription();
                bd.RenderTarget[0] = new RenderTargetBlendDescription(true, BlendOption.One, BlendOption.InverseSourceAlpha, BlendOperation.Add,
                    BlendOption.One, BlendOption.InverseSourceAlpha, BlendOperation.Add, ColorWriteMaskFlags.All);
                blend = new BlendState(dev, bd);
                raster = new RasterizerState(dev, new RasterizerStateDescription { CullMode = CullMode.None, FillMode = FillMode.Solid, IsDepthClipEnabled = false });
                noDepth = new DepthStencilState(dev, new DepthStencilStateDescription { IsDepthEnabled = false, IsStencilEnabled = false });
                sampler = new SamplerState(dev, new SamplerStateDescription { Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaximumLod = float.MaxValue });
                ready = true;
                Plugin.Log("overlay: ready (markers and hands)");
            }
            catch (Exception e) { Plugin.Log("overlay init failed: " + e); }
            return ready;
        }

        // ------------------------------------------------------------------ hand model (aim pose space: -Z forward, +Y up)

        static float[] BuildHand()
        {
            var v = new List<float>();
            // palm, wrist, index finger pointing forward, three curled fingers, thumb
            Box(v, 0f, 0f, 0.00f, 0.034f, 0.011f, 0.042f);
            Box(v, 0f, -0.002f, 0.070f, 0.026f, 0.017f, 0.030f);
            Box(v, 0.016f, 0.004f, -0.062f, 0.0075f, 0.0075f, 0.036f);
            Box(v, 0.004f, -0.002f, -0.040f, 0.0075f, 0.012f, 0.012f);
            Box(v, -0.008f, -0.002f, -0.040f, 0.0075f, 0.012f, 0.012f);
            Box(v, -0.020f, -0.002f, -0.036f, 0.0075f, 0.012f, 0.012f);
            Box(v, -0.040f, 0.000f, 0.000f, 0.010f, 0.008f, 0.026f);
            return v.ToArray();
        }

        static void Box(List<float> v, float cx, float cy, float cz, float hx, float hy, float hz)
        {
            float[][] faces = {
                new float[]{ 1,0,0,  1,-1,-1, 1,1,-1, 1,1,1, 1,-1,1 },  new float[]{ -1,0,0, -1,-1,1, -1,1,1, -1,1,-1, -1,-1,-1 },
                new float[]{ 0,1,0, -1,1,-1, -1,1,1, 1,1,1, 1,1,-1 },   new float[]{ 0,-1,0, -1,-1,1, -1,-1,-1, 1,-1,-1, 1,-1,1 },
                new float[]{ 0,0,1, -1,-1,1, 1,-1,1, 1,1,1, -1,1,1 },   new float[]{ 0,0,-1, 1,-1,-1, -1,-1,-1, -1,1,-1, 1,1,-1 } };
            foreach (var f in faces)
            {
                int[] idx = { 0, 1, 2, 0, 2, 3 };
                foreach (int i in idx)
                {
                    v.Add(cx + f[3 + i * 3] * hx); v.Add(cy + f[4 + i * 3] * hy); v.Add(cz + f[5 + i * 3] * hz);
                    v.Add(f[0]); v.Add(f[1]); v.Add(f[2]);
                }
            }
        }

        // ------------------------------------------------------------------ text textures

        static Tex GetTex(string key, Func<Bitmap> make, bool srgb = false)
        {
            Tex t;
            if (cache.TryGetValue(key, out t)) { t.Used = Environment.TickCount; return t; }
            if (cache.Count > 220)
            {
                var drop = new List<string>();
                foreach (var kv in cache) if (Environment.TickCount - kv.Value.Used > 3000) drop.Add(kv.Key);
                foreach (var k in drop) { cache[k].V.Dispose(); cache[k].T.Dispose(); cache.Remove(k); }
            }
            using (var bmp = make())
            {
                var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                var desc = new Texture2DDescription
                {
                    Width = bmp.Width, Height = bmp.Height, MipLevels = 1, ArraySize = 1, Format = srgb ? Format.B8G8R8A8_UNorm_SRgb : Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource
                };
                var tex = new Texture2D(dev, desc, new DataRectangle(data.Scan0, data.Stride));
                bmp.UnlockBits(data);
                t = new Tex { T = tex, V = new ShaderResourceView(dev, tex), Aspect = (float)bmp.Width / bmp.Height, Used = Environment.TickCount };
            }
            cache[key] = t;
            return t;
        }

        static Bitmap MakeLabel(string text)
        {
            using (var font = new Font("Segoe UI Semibold", 34f, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                SizeF size;
                using (var tmp = new Bitmap(1, 1)) using (var g0 = Graphics.FromImage(tmp)) size = g0.MeasureString(text, font);
                int w = Math.Max(16, (int)size.Width + 16), h = (int)size.Height + 8;
                var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    using (var path = new GraphicsPath())
                    {
                        path.AddString(text, font.FontFamily, (int)FontStyle.Regular, 34f, new PointF(8, 4), StringFormat.GenericDefault);
                        using (var pen = new Pen(Color.FromArgb(150, 0, 0, 0), 2.2f) { LineJoin = LineJoin.Round }) g.DrawPath(pen, path);
                        g.FillPath(Brushes.White, path);
                    }
                }
                return bmp;
            }
        }

        static Bitmap MakeIcon(System.Drawing.Color c)
        {
            var bmp = new Bitmap(96, 96, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var dark = new Pen(Color.FromArgb(220, 0, 0, 0), 12f)) g.DrawEllipse(dark, 18, 18, 60, 60);
                using (var ring = new Pen(Color.FromArgb(255, c.R, c.G, c.B), 7f)) g.DrawEllipse(ring, 18, 18, 60, 60);
                using (var dot = new SolidBrush(Color.FromArgb(255, c.R, c.G, c.B))) g.FillEllipse(dot, 38, 38, 20, 20);
            }
            return bmp;
        }

        static string Distance(double d)
        {
            if (d < 1000) return ((int)d) + " m";
            if (d < 1e9) return (d / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " km";
            return (d / 1e9).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " Gm";
        }

        // ------------------------------------------------------------------ drawing

        static void WriteBill(List<BV> list, Vector3 c, float hw, float hh, float u0, float v0, float u1, float v1, float r, float g, float b, float a)
        {
            Action<float, float, float, float> V = (dx, dy, u, v) => list.Add(new BV { x = c.X + dx, y = c.Y + dy, z = c.Z, u = u, v = v, r = r, g = g, b = b, a = a });
            V(-hw, hh, u0, v0); V(hw, hh, u1, v0); V(hw, -hh, u1, v1);
            V(-hw, hh, u0, v0); V(hw, -hh, u1, v1); V(-hw, -hh, u0, v1);
        }

        /// <summary>Draw markers and hands into one eye's finished image.</summary>
        public static void DrawEye(Device device, DeviceContext ctx, RenderTargetView rtv, int width, int height, MatrixD eyeWorld, Matrix proj,
            List<VrMarkers.Marker> marks, bool drawHands, MatrixD? handL, MatrixD? handR, float triggerL, float triggerR)
        {
            if (!Init(device)) return;
            try
            {
                MatrixD view = MatrixD.Invert(eyeWorld);
                ctx.OutputMerger.SetRenderTargets((DepthStencilView)null, rtv);
                ctx.Rasterizer.SetViewport(0f, 0f, width, height, 0f, 1f);
                ctx.Rasterizer.State = raster;
                ctx.OutputMerger.SetDepthStencilState(noDepth);
                ctx.OutputMerger.SetBlendState(blend, new SharpDX.Mathematics.Interop.RawColor4(0, 0, 0, 0), -1);
                ctx.InputAssembler.PrimitiveTopology = SharpDX.Direct3D.PrimitiveTopology.TriangleList;

                if (drawHands) DrawHands(ctx, view, proj, handL, handR, triggerL, triggerR);
                if (marks != null && marks.Count > 0) DrawMarkers(ctx, view, proj, marks);

                ctx.OutputMerger.SetRenderTargets((DepthStencilView)null, (RenderTargetView)null);
            }
            catch (Exception e) { Plugin.Log("overlay draw: " + e.Message); }
        }

        /// <summary>Draw the on-screen keyboard (a textured quad) onto the menu screen image. Rects are in UV (0..1, origin top left).</summary>
        public static void DrawKeyboard(Device device, DeviceContext ctx, RenderTargetView rtv, int width, int height, System.Drawing.RectangleF rect, System.Drawing.RectangleF hover, float pressed)
        {
            if (!Init(device)) return;
            try
            {
                ctx.OutputMerger.SetRenderTargets((DepthStencilView)null, rtv);
                ctx.Rasterizer.SetViewport(0f, 0f, width, height, 0f, 1f);
                ctx.Rasterizer.State = raster;
                ctx.OutputMerger.SetDepthStencilState(noDepth);
                ctx.OutputMerger.SetBlendState(blend, new SharpDX.Mathematics.Interop.RawColor4(0, 0, 0, 0), -1);
                ctx.InputAssembler.PrimitiveTopology = SharpDX.Direct3D.PrimitiveTopology.TriangleList;
                ctx.InputAssembler.InputLayout = ilB;
                ctx.VertexShader.Set(vsB); ctx.PixelShader.Set(psB);
                ctx.VertexShader.SetConstantBuffer(0, cbB);
                ctx.PixelShader.SetSampler(0, sampler);
                DataStream ds;
                ctx.MapSubresource(cbB, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None, out ds);
                ds.WriteRange(new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 });     // identity: positions are clip coordinates
                ctx.UnmapSubresource(cbB, 0);

                var kb = GetTex(VrKeyboard.LayerKey(), VrKeyboard.MakeBitmap, true);
                QuadUv(ctx, kb, rect, 1f, 1f, 1f, 1f);
                if (!hover.IsEmpty)
                {
                    var white = GetTex("white", VrKeyboard.MakeWhite, true);
                    QuadUv(ctx, white, hover, 0.45f, 0.75f, 1f, 0.35f + 0.45f * pressed);
                }
                ctx.OutputMerger.SetRenderTargets((DepthStencilView)null, (RenderTargetView)null);
            }
            catch (Exception e) { Plugin.Log("keyboard draw: " + e.Message); }
        }

        static void QuadUv(DeviceContext ctx, Tex tex, System.Drawing.RectangleF r, float cr, float cg, float cb, float ca)
        {
            float cx = (r.X + r.Width / 2f) * 2f - 1f, cy = 1f - (r.Y + r.Height / 2f) * 2f;
            var list = new List<BV>(6);
            WriteBill(list, new Vector3(cx, cy, 0.5f), r.Width, r.Height, 0, 0, 1, 1, cr, cg, cb, ca);
            DataStream ds;
            ctx.MapSubresource(vbB, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None, out ds);
            ds.WriteRange(list.ToArray());
            ctx.UnmapSubresource(vbB, 0);
            ctx.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(vbB, 36, 0));
            ctx.PixelShader.SetShaderResource(0, tex.V);
            ctx.Draw(6, 0);
        }

        static void DrawMarkers(DeviceContext ctx, MatrixD view, Matrix proj, List<VrMarkers.Marker> marks)
        {
            ctx.InputAssembler.InputLayout = ilB;
            ctx.OutputMerger.SetBlendState(blend, new SharpDX.Mathematics.Interop.RawColor4(0, 0, 0, 0), -1);   // the hands switched blending off
            ctx.VertexShader.Set(vsB); ctx.PixelShader.Set(psB);
            ctx.VertexShader.SetConstantBuffer(0, cbB);
            ctx.PixelShader.SetSampler(0, sampler);
            DataStream ds;
            ctx.MapSubresource(cbB, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None, out ds);
            ds.WriteRange(new[] { proj.M11, proj.M12, proj.M13, proj.M14, proj.M21, proj.M22, proj.M23, proj.M24, proj.M31, proj.M32, proj.M33, proj.M34, proj.M41, proj.M42, proj.M43, proj.M44 });
            ctx.UnmapSubresource(cbB, 0);

            int shown = 0;
            bool sprite = HudSrv != null && Plugin.Config.Markers != "custom";
            if (sprite) { ctx.PixelShader.Set(psH); ctx.PixelShader.SetShaderResource(0, HudSrv); }
            foreach (var m in marks)
            {
                if (shown >= 24) break;
                Vector3D p = Vector3D.Transform(m.Pos, view);
                double d = p.Length();
                if (d < 0.5 || p.Z > -0.3) continue;                    // behind the viewer
                float r = (float)Math.Min(d, 60.0);                     // far markers sit at 60 m in their true direction
                Vector3 dir = (Vector3)(p / d);
                Vector3 c = dir * r;
                if (sprite)
                {
                    // the game's own sprites of this marker (icon, name, distance), cut out of its HUD picture and put at the marker's real place
                    int sx0, sy0, sx1, sy1;
                    if (!VrMarkers.Rect(m, HudW, HudH, out sx0, out sy0, out sx1, out sy1)) continue;
                    float unitX = 2f * HudTanX / HudW * r, unitY = 2f * HudTanY / HudH * r;      // metres per pixel at this distance
                    float anchorY = (0.5f - m.Ndc.Y * 0.5f) * HudH;
                    float cy = (sy0 + sy1) * 0.5f;
                    Vector3 cs = c + new Vector3(0f, -(cy - anchorY) * unitY, 0f);
                    var ls = new List<BV>(6);
                    WriteBill(ls, cs, (sx1 - sx0) * 0.5f * unitX, (sy1 - sy0) * 0.5f * unitY, (float)sx0 / HudW, (float)sy0 / HudH, (float)sx1 / HudW, (float)sy1 / HudH, 1, 1, 1, 1f);
                    ctx.MapSubresource(vbB, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None, out ds);
                    ds.WriteRange(ls.ToArray());
                    ctx.UnmapSubresource(vbB, 0);
                    ctx.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(vbB, 36, 0));
                    ctx.Draw(6, 0);
                    shown++;
                    continue;
                }
                float gaze = (float)Math.Acos(Math.Max(-1.0, Math.Min(1.0, -dir.Z)));   // angle from straight ahead
                float textA = Math.Max(0f, Math.Min(1f, 1f - (gaze - 0.20f) / 0.25f));
                float k = r * 0.030f;                                    // icon half size: ~3.4 degrees
                var icon = GetTex("i" + m.Color.ToArgb(), () => MakeIcon(m.Color));
                var list = new List<BV>(12);
                WriteBill(list, c, k, k, 0, 0, 1, 1, 1, 1, 1, 1f);
                var bufDraw = new List<KeyValuePair<Tex, List<BV>>> { new KeyValuePair<Tex, List<BV>>(icon, list) };
                if (textA > 0.02f)
                {
                    string label = string.IsNullOrEmpty(m.Text) ? "GPS" : m.Text;
                    var tl = GetTex("t" + label, () => MakeLabel(label));
                    float th = k * 0.85f, tw = th * tl.Aspect;
                    var l2 = new List<BV>(6);
                    WriteBill(l2, c + new Vector3(0, -(k + th * 0.8f), 0), tw, th, 0, 0, 1, 1, 1, 1, 1, textA);
                    bufDraw.Add(new KeyValuePair<Tex, List<BV>>(tl, l2));
                    string dist = Distance((m.Pos - Vector3D.Transform(Vector3D.Zero, MatrixD.Invert(view))).Length());
                    var td = GetTex("t" + dist, () => MakeLabel(dist));
                    float dh = th * 0.75f, dw = dh * td.Aspect;
                    var l3 = new List<BV>(6);
                    WriteBill(l3, c + new Vector3(0, -(k + th * 1.6f + dh * 0.8f), 0), dw, dh, 0, 0, 1, 1, 0.85f, 0.95f, 1f, textA);
                    bufDraw.Add(new KeyValuePair<Tex, List<BV>>(td, l3));
                }
                foreach (var kv in bufDraw)
                {
                    ctx.MapSubresource(vbB, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None, out ds);
                    ds.WriteRange(kv.Value.ToArray());
                    ctx.UnmapSubresource(vbB, 0);
                    ctx.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(vbB, 36, 0));
                    ctx.PixelShader.SetShaderResource(0, kv.Key.V);
                    ctx.Draw(kv.Value.Count, 0);
                }
                shown++;
            }
        }

        /// <summary>A panel as it is drawn in the desktop mirror: its picture and its four corners in the world (top left, top right, bottom right, bottom left).</summary>
        public sealed class MirrorQuad { public ShaderResourceView View; public Vector3D[] Corners = new Vector3D[4]; public double Dist; }

        /// <summary>The panels drawn into the desktop window as they are placed in the world, seen through one eye (far ones first).</summary>
        public static void DrawPanelQuads(Device device, DeviceContext ctx, RenderTargetView rtv, int width, int height, MatrixD eyeWorld, Matrix proj, List<MirrorQuad> quads)
        {
            if (quads == null || quads.Count == 0 || !Init(device)) return;
            try
            {
                MatrixD view = MatrixD.Invert(eyeWorld);
                ctx.OutputMerger.SetRenderTargets((DepthStencilView)null, rtv);
                ctx.Rasterizer.SetViewport(0f, 0f, width, height, 0f, 1f);
                ctx.Rasterizer.State = raster;
                ctx.OutputMerger.SetDepthStencilState(noDepth);
                ctx.OutputMerger.SetBlendState(blend, new SharpDX.Mathematics.Interop.RawColor4(0, 0, 0, 0), -1);
                ctx.InputAssembler.PrimitiveTopology = SharpDX.Direct3D.PrimitiveTopology.TriangleList;
                ctx.InputAssembler.InputLayout = ilB;
                ctx.VertexShader.Set(vsB); ctx.PixelShader.Set(psB);
                ctx.VertexShader.SetConstantBuffer(0, cbB);
                ctx.PixelShader.SetSampler(0, sampler);
                DataStream ds;
                ctx.MapSubresource(cbB, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None, out ds);
                ds.WriteRange(new[] { proj.M11, proj.M12, proj.M13, proj.M14, proj.M21, proj.M22, proj.M23, proj.M24, proj.M31, proj.M32, proj.M33, proj.M34, proj.M41, proj.M42, proj.M43, proj.M44 });
                ctx.UnmapSubresource(cbB, 0);
                quads.Sort((a, b) => b.Dist.CompareTo(a.Dist));
                foreach (var q in quads)
                {
                    var p = new Vector3[4];
                    bool front = true;
                    for (int i = 0; i < 4; i++) { var v = Vector3D.Transform(q.Corners[i], view); p[i] = (Vector3)v; if (v.Z > -0.05) front = false; }
                    if (!front) continue;                                  // (partly) behind the viewer
                    var list = new List<BV>(6);
                    Action<int, float, float> V = (i, u, vv) => list.Add(new BV { x = p[i].X, y = p[i].Y, z = p[i].Z, u = u, v = vv, r = 1, g = 1, b = 1, a = 1 });
                    V(0, 0, 0); V(1, 1, 0); V(2, 1, 1);
                    V(0, 0, 0); V(2, 1, 1); V(3, 0, 1);
                    ctx.MapSubresource(vbB, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None, out ds);
                    ds.WriteRange(list.ToArray());
                    ctx.UnmapSubresource(vbB, 0);
                    ctx.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(vbB, 36, 0));
                    ctx.PixelShader.SetShaderResource(0, q.View);
                    ctx.Draw(6, 0);
                }
                ctx.OutputMerger.SetRenderTargets((DepthStencilView)null, (RenderTargetView)null);
            }
            catch (Exception e) { Plugin.Log("mirror panels: " + e.Message); }
        }

        // ------------------------------------------------------------------ 2D drawing for the own HUD panels (pixel coordinates, origin top left)

        static DeviceContext c2; static int w2, h2; static bool srgb2;
        /// <summary>Everything drawn for the own HUD panels is multiplied by this (the HUD opacity setting).</summary>
        public static float Opacity = 1f;
        /// <summary>Set before Begin2D for one panel: fades it (wrist panels fade out when you do not look at them).</summary>
        public static float OpacityMul = 1f;
        static readonly Dictionary<string, Tex> icons = new Dictionary<string, Tex>();

        /// <summary>Start drawing into a panel image. Straight pixel coordinates; colours are given as sRGB bytes.</summary>
        public static bool Begin2D(Device device, DeviceContext ctx, RenderTargetView rtv, int width, int height, bool srgbTarget)
        {
            if (!Init(device)) return false;
            try
            {
                c2 = ctx; w2 = width; h2 = height; srgb2 = srgbTarget;
                Opacity = Math.Max(0.15f, Math.Min(1f, Plugin.Config.HudOpacity)) * Math.Max(0f, Math.Min(1f, OpacityMul));
                ctx.ClearRenderTargetView(rtv, new SharpDX.Mathematics.Interop.RawColor4(0f, 0f, 0f, 0f));
                ctx.OutputMerger.SetRenderTargets((DepthStencilView)null, rtv);
                ctx.Rasterizer.SetViewport(0f, 0f, width, height, 0f, 1f);
                ctx.Rasterizer.State = raster;
                ctx.OutputMerger.SetDepthStencilState(noDepth);
                ctx.OutputMerger.SetBlendState(blend, new SharpDX.Mathematics.Interop.RawColor4(0, 0, 0, 0), -1);
                ctx.InputAssembler.PrimitiveTopology = SharpDX.Direct3D.PrimitiveTopology.TriangleList;
                ctx.InputAssembler.InputLayout = ilB;
                ctx.VertexShader.Set(vsB); ctx.PixelShader.Set(psB);
                ctx.VertexShader.SetConstantBuffer(0, cbB);
                ctx.PixelShader.SetSampler(0, sampler);
                DataStream ds;
                ctx.MapSubresource(cbB, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None, out ds);
                ds.WriteRange(new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 });     // identity: positions are clip coordinates
                ctx.UnmapSubresource(cbB, 0);
                return true;
            }
            catch (Exception e) { Plugin.Log("2d begin: " + e.Message); return false; }
        }

        public static void End2D()
        {
            try { if (c2 != null) c2.OutputMerger.SetRenderTargets((DepthStencilView)null, (RenderTargetView)null); } catch { }
            c2 = null;
        }

        static readonly float[] linLut = BuildLut();
        static float[] BuildLut() { var t = new float[256]; for (int i = 0; i < 256; i++) t[i] = (float)Math.Pow(i / 255.0, 2.2); return t; }
        static float Lin(int v) { v = v < 0 ? 0 : v > 255 ? 255 : v; return srgb2 ? linLut[v] : v / 255f; }

        static readonly BV[] quad6 = new BV[6];        // reused: a panel is hundreds of quads, none of them should allocate

        static void Quad2(Tex tex, float x, float y, float w, float h, float u0, float v0, float u1, float v1, int r, int g, int b, int a)
        {
            if (c2 == null || tex == null || w <= 0f || h <= 0f) return;
            float cx = (x + w / 2f) / w2 * 2f - 1f, cy = 1f - (y + h / 2f) / h2 * 2f;
            float hw = w / w2, hh = h / h2, rr = Lin(r), gg = Lin(g), bb = Lin(b), aa = a / 255f * Opacity;
            // top left, top right, bottom right, top left, bottom right, bottom left
            quad6[0] = new BV { x = cx - hw, y = cy + hh, z = 0.5f, u = u0, v = v0, r = rr, g = gg, b = bb, a = aa };
            quad6[1] = new BV { x = cx + hw, y = cy + hh, z = 0.5f, u = u1, v = v0, r = rr, g = gg, b = bb, a = aa };
            quad6[2] = new BV { x = cx + hw, y = cy - hh, z = 0.5f, u = u1, v = v1, r = rr, g = gg, b = bb, a = aa };
            quad6[3] = quad6[0];
            quad6[4] = quad6[2];
            quad6[5] = new BV { x = cx - hw, y = cy - hh, z = 0.5f, u = u0, v = v1, r = rr, g = gg, b = bb, a = aa };
            DataStream ds;
            c2.MapSubresource(vbB, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None, out ds);
            ds.WriteRange(quad6, 0, 6);
            c2.UnmapSubresource(vbB, 0);
            c2.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(vbB, 36, 0));
            c2.PixelShader.SetShaderResource(0, tex.V);
            c2.Draw(6, 0);
        }

        public static void FillRect(float x, float y, float w, float h, int r, int g, int b, int a)
        {
            Quad2(GetTex("white", VrKeyboard.MakeWhite, true), x, y, w, h, 0, 0, 1, 1, r, g, b, a);
        }

        public static void FrameRect(float x, float y, float w, float h, float t, int r, int g, int b, int a)
        {
            FillRect(x, y, w, t, r, g, b, a); FillRect(x, y + h - t, w, t, r, g, b, a);
            FillRect(x, y + t, t, h - 2 * t, r, g, b, a); FillRect(x + w - t, y + t, t, h - 2 * t, r, g, b, a);
        }

        static Bitmap MakeText(string text, int px)
        {
            using (var font = new Font("Segoe UI Semibold", px, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                SizeF size;
                using (var tmp = new Bitmap(1, 1)) using (var g0 = Graphics.FromImage(tmp)) size = g0.MeasureString(text, font, 4000, StringFormat.GenericTypographic);
                int w = Math.Max(4, (int)Math.Ceiling(size.Width) + 4), h = (int)Math.Ceiling(px * 1.35f);
                var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.DrawString(text, font, Brushes.White, 2f, px * 0.1f, StringFormat.GenericTypographic);
                }
                return bmp;
            }
        }

        /// <summary>Width in pixels of a text.</summary>
        public static float TextWidth(string text, int px)
        {
            if (string.IsNullOrEmpty(text)) return 0f;
            var t = GetTex("T|" + px + "|" + text, () => MakeText(text, px));
            return t.T.Description.Width;
        }

        /// <summary>align: 0 left, 1 centre, 2 right of x. y is the top of the line.</summary>
        public static void DrawText(string text, float x, float y, int px, int r, int g, int b, int a, int align = 0)
        {
            if (string.IsNullOrEmpty(text)) return;
            var t = GetTex("T|" + px + "|" + text, () => MakeText(text, px));
            float w = t.T.Description.Width, h = t.T.Description.Height;
            if (align == 1) x -= w / 2f; else if (align == 2) x -= w;
            Quad2(t, x, y, w, h, 0, 0, 1, 1, r, g, b, a);
        }

        /// <summary>One of the game's own icons (a .dds file below the content folder); false when it cannot be loaded.</summary>
        public static bool DrawIcon(string path, float x, float y, float w, float h, int a = 255)
        {
            var t = LoadDds(path);
            if (t == null) return false;
            Quad2(t, x, y, w, h, 0, 0, 1, 1, 255, 255, 255, a);
            return true;
        }

        static int missingLog;

        /// <summary>A file through the game's file system (also inside mod packages), else straight from the disk.</summary>
        static byte[] ReadAllBytes(string path)
        {
            try
            {
                using (var st = VRage.FileSystem.MyFileSystem.OpenRead(path))
                    if (st != null) using (var ms = new System.IO.MemoryStream()) { st.CopyTo(ms); if (ms.Length > 0) return ms.ToArray(); }
            }
            catch { }
            try { if (System.IO.File.Exists(path)) return System.IO.File.ReadAllBytes(path); } catch { }
            return null;
        }

        static Tex LoadDds(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return null;
            Tex res;
            if (icons.TryGetValue(rel, out res)) return res;
            res = null;
            try
            {
                // icons of the game are below the content folder; icons of mods (online games, workshop items) lie in the mod's folder or inside its package:
                // the game's own file system finds both
                byte[] b = null;
                string p = rel;
                if (!System.IO.Path.IsPathRooted(p)) p = System.IO.Path.Combine(VRage.FileSystem.MyFileSystem.ContentPath, rel);
                b = ReadAllBytes(p);
                if (b == null && !System.IO.Path.IsPathRooted(rel)) b = ReadAllBytes(rel);
                if (b == null && missingLog++ < 12) Plugin.Log("icon not found: " + rel);
                if (b != null && b.Length > 8 && dev != null && !(b.Length > 128 && b[0] == 'D' && b[1] == 'D' && b[2] == 'S'))
                {
                    // a picture file (png, jpg ...)
                    using (var ms = new System.IO.MemoryStream(b)) using (var bmp = new Bitmap(ms))
                    {
                        var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
                        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                        var desc0 = new Texture2DDescription
                        {
                            Width = bmp.Width, Height = bmp.Height, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm_SRgb,
                            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource
                        };
                        var tex0 = new Texture2D(dev, desc0, new DataRectangle(data.Scan0, data.Stride));
                        bmp.UnlockBits(data);
                        res = new Tex { T = tex0, V = new ShaderResourceView(dev, tex0), Aspect = (float)bmp.Width / bmp.Height, Used = Environment.TickCount };
                    }
                    b = null;
                }
                if (b != null && dev != null)
                {
                    if (b.Length > 128 && b[0] == 'D' && b[1] == 'D' && b[2] == 'S')
                    {
                        int hgt = BitConverter.ToInt32(b, 12), wid = BitConverter.ToInt32(b, 16), mips = Math.Max(1, BitConverter.ToInt32(b, 28));
                        string four = System.Text.Encoding.ASCII.GetString(b, 84, 4);
                        int off = 128, block = 16; Format f = Format.Unknown;
                        if (four == "DXT1") { f = Format.BC1_UNorm_SRgb; block = 8; }
                        else if (four == "DXT5") f = Format.BC3_UNorm_SRgb;
                        else if (four == "DXT3") f = Format.BC2_UNorm_SRgb;
                        else if (four == "DX10")
                        {
                            int dx = BitConverter.ToInt32(b, 128); off = 148;
                            if (dx == 98 || dx == 99) f = Format.BC7_UNorm_SRgb;
                            else if (dx == 71 || dx == 72) { f = Format.BC1_UNorm_SRgb; block = 8; }
                            else if (dx == 77 || dx == 78) f = Format.BC3_UNorm_SRgb;
                        }
                        if (f != Format.Unknown && wid % 4 == 0 && hgt % 4 == 0 && wid > 0 && hgt > 0)
                        {
                            var h = GCHandle.Alloc(b, GCHandleType.Pinned);
                            try
                            {
                                var rects = new DataRectangle[mips];
                                int mw = wid, mh = hgt, pos = off, used = 0;
                                for (int m = 0; m < mips; m++)
                                {
                                    int bw = Math.Max(1, (mw + 3) / 4), bh = Math.Max(1, (mh + 3) / 4), size = bw * bh * block;
                                    if (pos + size > b.Length) break;
                                    rects[m] = new DataRectangle(h.AddrOfPinnedObject() + pos, bw * block);
                                    pos += size; used++;
                                    if (mw <= 4 && mh <= 4) break;     // block compressed levels below 4x4 are not valid sizes to upload separately
                                    mw = Math.Max(1, mw / 2); mh = Math.Max(1, mh / 2);
                                    if (mw % 4 != 0 || mh % 4 != 0) break;
                                }
                                if (used > 0)
                                {
                                    var rr = new DataRectangle[used]; Array.Copy(rects, rr, used);
                                    var desc = new Texture2DDescription { Width = wid, Height = hgt, MipLevels = used, ArraySize = 1, Format = f, SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource };
                                    var tex = new Texture2D(dev, desc, rr);
                                    res = new Tex { T = tex, V = new ShaderResourceView(dev, tex), Aspect = (float)wid / hgt, Used = Environment.TickCount };
                                }
                            }
                            finally { h.Free(); }
                        }
                    }
                }
            }
            catch (Exception e) { Plugin.Log("icon " + rel + ": " + e.Message); }
            icons[rel] = res;
            return res;
        }

        static void DrawHands(DeviceContext ctx, MatrixD view, Matrix proj, MatrixD? left, MatrixD? right, float trigL, float trigR)
        {
            ctx.InputAssembler.InputLayout = ilM;
            ctx.VertexShader.Set(vsM); ctx.PixelShader.Set(psM);
            ctx.VertexShader.SetConstantBuffer(0, cbM);
            ctx.PixelShader.SetConstantBuffer(0, cbM);
            ctx.OutputMerger.SetBlendState(null);
            ctx.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(vbHand, 24, 0));
            for (int h = 0; h < 2; h++)
            {
                MatrixD? w = h == 0 ? left : right;
                if (!w.HasValue) continue;
                float trig = h == 0 ? trigL : trigR;
                Matrix wv = (Matrix)(w.Value * view);
                if (h == 0) wv = Matrix.CreateScale(-1f, 1f, 1f) * wv;      // the model is a right hand; mirror it for the left
                Matrix wvp = wv * proj;
                DataStream ds;
                ctx.MapSubresource(cbM, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None, out ds);
                ds.WriteRange(new[] { wvp.M11, wvp.M12, wvp.M13, wvp.M14, wvp.M21, wvp.M22, wvp.M23, wvp.M24, wvp.M31, wvp.M32, wvp.M33, wvp.M34, wvp.M41, wvp.M42, wvp.M43, wvp.M44,
                    wv.M11, wv.M12, wv.M13, wv.M14, wv.M21, wv.M22, wv.M23, wv.M24, wv.M31, wv.M32, wv.M33, wv.M34, wv.M41, wv.M42, wv.M43, wv.M44,
                    0.78f + 0.2f * trig, 0.72f - 0.3f * trig, 0.66f - 0.3f * trig, 1f });
                ctx.UnmapSubresource(cbM, 0);
                ctx.Draw(handVerts, 0);
            }
        }
    }
}


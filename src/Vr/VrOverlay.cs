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
float4 PS(PI i) : SV_Target { float4 c = tex.Sample(smp, i.uv); return float4(c.rgb * i.col.rgb * i.col.a, c.a * i.col.a); }";

        const string MeshHlsl = @"
cbuffer C : register(b0) { row_major float4x4 WVP; row_major float4x4 WV; float4 color; };
struct VI { float3 pos : POSITION; float3 nrm : NORMAL; };
struct PI { float4 pos : SV_POSITION; float3 nrm : TEXCOORD0; };
PI VS(VI i) { PI o; o.pos = mul(float4(i.pos, 1), WVP); o.nrm = mul(i.nrm, (float3x3)WV); return o; }
float4 PS(PI i) : SV_Target { float3 n = normalize(i.nrm); float l = saturate(dot(n, normalize(float3(0.3, 0.7, 0.6)))) * 0.55 + 0.45; return float4(color.rgb * l, 1); }";

        static bool initTried, ready;
        static Device dev;
        static VertexShader vsB, vsM; static PixelShader psB, psM;
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
            try { bmp.Save(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SeVr_icon_" + c.ToArgb() + ".png"), System.Drawing.Imaging.ImageFormat.Png); } catch { }
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
            foreach (var m in marks)
            {
                if (shown >= 24) break;
                Vector3D p = Vector3D.Transform(m.Pos, view);
                double d = p.Length();
                if (d < 0.5 || p.Z > -0.3) continue;                    // behind the viewer
                float r = (float)Math.Min(d, 60.0);                     // far markers sit at 60 m in their true direction
                Vector3 dir = (Vector3)(p / d);
                Vector3 c = dir * r;
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

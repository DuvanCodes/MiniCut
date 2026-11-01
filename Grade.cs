using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace MiniCut
{
    // Real-time GPU color grade + effects. HLSL is compiled at startup with the system's D3DCompiler (what CapCut ships too).
    // Look/effect ids match the key order of Opts.Filters / Opts.Effects.
    public class Grade : ShaderEffect
    {
        const string Hlsl = @"
sampler2D input : register(s0);
float4 P0 : register(c0); // brightness, contrast, saturation, look
float4 P1 : register(c1); // effect, texelW, texelH, seed
float4 P2 : register(c2); // look (id >= 9): mul rgb, saturation
float4 P3 : register(c3); // look (id >= 9): add rgb, contrast
float4 main(float2 uv : TEXCOORD) : COLOR {
  float fx = P1.x; float2 t = P1.yz; float2 u = uv;
  if (fx > 5.5 && fx < 6.5) { float2 g = t * 14; u = (floor(uv / g) + 0.5) * g; }
  if (fx > 6.5 && fx < 7.5) u.x = uv.x > 0.5 ? 1 - uv.x : uv.x;
  if (fx > 7.5 && fx < 8.5) u.x += sin(uv.y * 40 + P1.w * 3) * 0.012;
  if (fx > 8.5 && fx < 9.5) { float b = floor(uv.y * 24); float h = frac(sin(b * 91.7 + floor(P1.w * 8) * 13.1) * 437.58); if (h > 0.86) u.x += (h - 0.93) * 0.25; }
  float4 col = tex2D(input, u);
  if (fx > 0.5 && fx < 1.5) {
    float4 a = 0;
    for (int i = -2; i <= 2; i++) for (int j = -2; j <= 2; j++) a += tex2D(input, u + float2(i, j) * t * 2.5);
    col = a / 25;
  } else if (fx > 1.5 && fx < 2.5) {
    float4 n = tex2D(input, u + float2(t.x, 0)) + tex2D(input, u - float2(t.x, 0)) + tex2D(input, u + float2(0, t.y)) + tex2D(input, u - float2(0, t.y));
    col = lerp(col, col * 5 - n, 0.7);
  } else if (fx > 3.5 && fx < 4.5) {
    col.r = tex2D(input, u - float2(t.x * 4, 0)).r; col.b = tex2D(input, u + float2(t.x * 4, 0)).b;
  } else if (fx > 8.5 && fx < 9.5) {
    col.r = tex2D(input, u - float2(t.x * 5, 0)).r; col.b = tex2D(input, u + float2(t.x * 5, 0)).b;
  } else if (fx > 10.5 && fx < 11.5) {
    float4 a = 0;
    for (int i = -2; i <= 2; i++) for (int j = -2; j <= 2; j++) a += tex2D(input, u + float2(i, j) * t * 4);
    col = col + (a / 25) * 0.8;
  } else if (fx > 11.5) {
    float3 n1 = tex2D(input, u + float2(t.x, 0)).rgb - tex2D(input, u - float2(t.x, 0)).rgb;
    float3 n2 = tex2D(input, u + float2(0, t.y)).rgb - tex2D(input, u - float2(0, t.y)).rgb;
    col.rgb = lerp(col.rgb * 0.25, float3(1, 1, 1), saturate((length(n1) + length(n2)) * 4));
  }
  float3 c = col.rgb; float look = P0.w;
  float l = dot(c, float3(0.299, 0.587, 0.114));
  if (look > 0.5 && look < 1.5) c = l.xxx;
  else if (look < 2.5 && look > 1.5) c = float3(dot(c, float3(.393,.769,.189)), dot(c, float3(.349,.686,.168)), dot(c, float3(.272,.534,.131)));
  else if (look < 3.5 && look > 2.5) c += float3(0.07, 0.02, -0.07);
  else if (look < 4.5 && look > 3.5) c += float3(-0.06, 0.0, 0.08);
  else if (look < 5.5 && look > 4.5) { c = lerp(l.xxx, c, 1.5); c = (c - 0.5) * 1.1 + 0.5; }
  else if (look < 6.5 && look > 5.5) { c = lerp(l.xxx, c, 0.7) * float3(1.05, 0.98, 0.85) + 0.04; c = (c - 0.5) * 0.9 + 0.5; }
  else if (look < 7.5 && look > 6.5) { c = smoothstep(0.0, 1.0, c); c = lerp(l.xxx, c, 0.85); }
  else if (look > 7.5 && look < 8.5) c = 1 - c;
  else if (look > 8.5) { c = c * P2.rgb + P3.rgb; float lk = dot(c, float3(0.299, 0.587, 0.114)); c = lerp(lk.xxx, c, P2.w); c = (c - 0.5) * P3.w + 0.5; }
  c = (c - 0.5) * P0.y + 0.5 + P0.x;
  l = dot(c, float3(0.299, 0.587, 0.114)); c = lerp(l.xxx, c, P0.z);
  if (fx > 2.5 && fx < 3.5) { float2 d = uv - 0.5; c *= saturate(1 - dot(d, d) * 2.2); }
  if (fx > 9.5 && fx < 10.5) c *= 0.8 + 0.2 * step(0.5, frac(uv.y / (t.y * 4)));
  if (fx > 4.5 && fx < 5.5) c += (frac(sin(dot(uv * 1000 + P1.w, float2(12.9898, 78.233))) * 43758.5453) - 0.5) * 0.14;
  return float4(saturate(c) * col.a, col.a);
}";
        [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
        static extern int D3DCompile(IntPtr src, UIntPtr len, string name, IntPtr defs, IntPtr inc, string entry, string target, uint f1, uint f2, out IntPtr code, out IntPtr err);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr GetPtr(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate UIntPtr GetSize(IntPtr self);

        public static readonly PixelShader Shader = Compile();
        public static string Error;
        public static bool Ok => Shader != null;

        static PixelShader Compile()
        {
            try
            {
                var bytes = Encoding.ASCII.GetBytes(Hlsl); var h = Marshal.AllocHGlobal(bytes.Length); Marshal.Copy(bytes, 0, h, bytes.Length);
                var hr = D3DCompile(h, (UIntPtr)bytes.Length, "grade", IntPtr.Zero, IntPtr.Zero, "main", "ps_3_0", 0, 0, out var code, out var err);
                Marshal.FreeHGlobal(h);
                if (hr != 0)
                {
                    if (err != IntPtr.Zero) Error = Marshal.PtrToStringAnsi(Marshal.GetDelegateForFunctionPointer<GetPtr>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(err), 3 * IntPtr.Size))(err));
                    return null;
                }
                var vt = Marshal.ReadIntPtr(code);
                var p = Marshal.GetDelegateForFunctionPointer<GetPtr>(Marshal.ReadIntPtr(vt, 3 * IntPtr.Size))(code);
                var n = (int)Marshal.GetDelegateForFunctionPointer<GetSize>(Marshal.ReadIntPtr(vt, 4 * IntPtr.Size))(code);
                var buf = new byte[n]; Marshal.Copy(p, buf, 0, n);
                var ps = new PixelShader(); ps.SetStreamSource(new MemoryStream(buf)); return ps;
            }
            catch (Exception ex) { Error = ex.Message; return null; }
        }

        public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(Grade), 0);
        public static readonly DependencyProperty P0Property = DependencyProperty.Register("P0", typeof(Point4D), typeof(Grade), new UIPropertyMetadata(new Point4D(0, 1, 1, 0), PixelShaderConstantCallback(0)));
        public static readonly DependencyProperty P1Property = DependencyProperty.Register("P1", typeof(Point4D), typeof(Grade), new UIPropertyMetadata(new Point4D(0, 1.0 / 1280, 1.0 / 720, 0), PixelShaderConstantCallback(1)));

        public static readonly DependencyProperty P2Property = DependencyProperty.Register("P2", typeof(Point4D), typeof(Grade), new UIPropertyMetadata(new Point4D(1, 1, 1, 1), PixelShaderConstantCallback(2)));
        public static readonly DependencyProperty P3Property = DependencyProperty.Register("P3", typeof(Point4D), typeof(Grade), new UIPropertyMetadata(new Point4D(0, 0, 0, 1), PixelShaderConstantCallback(3)));

        public Grade() { PixelShader = Shader; UpdateShaderValue(InputProperty); UpdateShaderValue(P0Property); UpdateShaderValue(P1Property); UpdateShaderValue(P2Property); UpdateShaderValue(P3Property); }
        public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
        public void Set(double bright, double contrast, double sat, int look, int fx, double w, double h, double seed)
        {
            SetValue(P0Property, new Point4D(bright, contrast, sat, look)); SetValue(P1Property, new Point4D(fx, 1 / w, 1 / h, seed));
            var l = look >= 9 && look - 9 < Opts.NewLooks.Length ? Opts.NewLooks[look - 9] : null; // looks 9+ are plain numbers (Opts.NewLooks)
            SetValue(P2Property, l == null ? new Point4D(1, 1, 1, 1) : new Point4D(l.Mr, l.Mg, l.Mb, l.Sat));
            SetValue(P3Property, l == null ? new Point4D(0, 0, 0, 1) : new Point4D(l.Ar, l.Ag, l.Ab, l.Con));
        }
    }
}

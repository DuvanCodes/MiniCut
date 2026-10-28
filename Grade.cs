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
float4 main(float2 uv : TEXCOORD) : COLOR {
  float fx = P1.x; float2 t = P1.yz; float2 u = uv;
  if (fx > 5.5) { float2 g = t * 14; u = (floor(uv / g) + 0.5) * g; }
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
  else if (look > 7.5) c = 1 - c;
  c = (c - 0.5) * P0.y + 0.5 + P0.x;
  l = dot(c, float3(0.299, 0.587, 0.114)); c = lerp(l.xxx, c, P0.z);
  if (fx > 2.5 && fx < 3.5) { float2 d = uv - 0.5; c *= saturate(1 - dot(d, d) * 2.2); }
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

        public Grade() { PixelShader = Shader; UpdateShaderValue(InputProperty); UpdateShaderValue(P0Property); UpdateShaderValue(P1Property); }
        public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
        public void Set(double bright, double contrast, double sat, int look, int fx, double w, double h, double seed)
        { SetValue(P0Property, new Point4D(bright, contrast, sat, look)); SetValue(P1Property, new Point4D(fx, 1 / w, 1 / h, seed)); }
    }
}

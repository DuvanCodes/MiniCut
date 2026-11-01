using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace MiniCut
{
    // Blend mode against the clips below (sampler s1 = a VisualBrush of them). Per-channel formulas and the
    // alpha composition are the ones in CapCut's Resources\MixMode shaders: out = fg*fa*(1-ba) + bg*ba*(1-fa) + fa*ba*B(fg,bg).
    // Mode ids follow Opts.Blends: 0 normal, 1 multiply, 2 screen, 3 overlay, 4 darken, 5 lighten, 6 hard light, 7 soft light, 8 linear burn, 9 color burn, 10 color dodge.
    public class Blend : ShaderEffect
    {
        const string Hlsl = @"
sampler2D input : register(s0);
sampler2D back : register(s1);
float4 P0 : register(c0);
float3 Mix(float3 f, float3 b, float m) {
  if (m < 0.5) return f;
  else if (m < 1.5) return f * b;
  else if (m < 2.5) return f + b - f * b;
  else if (m < 3.5) return lerp(2 * b * f, 1 - 2 * (1 - b) * (1 - f), step(0.5, b));
  else if (m < 4.5) return min(f, b);
  else if (m < 5.5) return max(f, b);
  else if (m < 6.5) return lerp(2 * f * b, 1 - 2 * (1 - f) * (1 - b), step(0.5, f));
  else if (m < 7.5) return lerp(2 * b * f + b * b * (1 - 2 * f), sqrt(b) * (2 * f - 1) + 2 * b * (1 - f), step(0.5, f));
  else if (m < 8.5) return max(f + b - 1, 0);
  else if (m < 9.5) return max(1 - (1 - b) / max(f, 0.0001), 0);
  return min(b / max(1 - f, 0.0001), 1);
}
float4 main(float2 uv : TEXCOORD) : COLOR {
  float4 fp = tex2D(input, uv); float4 bp = tex2D(back, uv);
  float fa = fp.a, ba = bp.a;
  float3 f = fp.rgb / max(fa, 0.00001), b = bp.rgb / max(ba, 0.00001);
  float a = fa + ba - fa * ba;
  float3 c = f * fa * (1 - ba) + b * ba * (1 - fa) + fa * ba * Mix(f, b, P0.x);
  return float4(c, a);
}";
        public static readonly PixelShader Shader = Grade.Compile(Hlsl, out Error);
        public static string Error;
        public static bool Ok => Shader != null;
        public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(Blend), 0);
        public static readonly DependencyProperty BackProperty = RegisterPixelShaderSamplerProperty("Back", typeof(Blend), 1);
        public static readonly DependencyProperty P0Property = DependencyProperty.Register("P0", typeof(Point4D), typeof(Blend), new UIPropertyMetadata(new Point4D(0, 0, 0, 0), PixelShaderConstantCallback(0)));
        public Blend() { PixelShader = Shader; UpdateShaderValue(InputProperty); UpdateShaderValue(BackProperty); UpdateShaderValue(P0Property); }
        public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
        public Brush Back { get => (Brush)GetValue(BackProperty); set => SetValue(BackProperty, value); }
        public void Set(int mode) => SetValue(P0Property, new Point4D(mode, 0, 0, 0));
    }
}

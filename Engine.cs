using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace MiniCut
{
    public static class Opts
    {
        public static readonly string[] Canvases = { "16:9", "9:16", "1:1", "4:3" };
        public static (int w, int h) Size(string c) => c switch { "9:16" => (720, 1280), "1:1" => (1080, 1080), "4:3" => (960, 720), _ => (1280, 720) };
        // name -> ffmpeg filter (color looks / effects)
        public static readonly Dictionary<string, string> Filters = new()
        {
            ["Ninguno"] = "", ["Blanco y negro"] = "hue=s=0", ["Sepia"] = "colorchannelmixer=.393:.769:.189:0:.349:.686:.168:0:.272:.534:.131",
            ["Cálido"] = "colorbalance=rm=.15:bm=-.15", ["Frío"] = "colorbalance=rm=-.15:bm=.2", ["Vívido"] = "eq=saturation=1.5:contrast=1.1",
            ["Vintage"] = "curves=preset=vintage", ["Cine"] = "curves=preset=strong_contrast,eq=saturation=0.85", ["Negativo"] = "negate",
        };
        public static readonly Dictionary<string, string> Effects = new()
        {
            ["Ninguno"] = "", ["Desenfoque"] = "gblur=sigma=6", ["Nitidez"] = "unsharp=5:5:1.5", ["Viñeta"] = "vignette", ["Aberración"] = "rgbashift=rh=-4:bh=4",
            ["Ruido"] = "noise=alls=20:allf=t", ["Pixelar"] = "scale=iw/16:-2,scale=iw*16:-2:flags=neighbor",
            // the next ones are close ffmpeg approximations of the GPU preview shader (ids follow this order, see Grade.cs)
            ["Espejo"] = "format=yuv444p,geq=lum='lum(if(gt(X,W/2),W-X,X),Y)':cb='cb(if(gt(X,W/2),W-X,X),Y)':cr='cr(if(gt(X,W/2),W-X,X),Y)'",
            ["Onda"] = "format=yuv444p,geq=lum='lum(X+8*sin(Y/12+T*3),Y)':cb='cb(X+8*sin(Y/12+T*3),Y)':cr='cr(X+8*sin(Y/12+T*3),Y)'",
            ["Glitch"] = "rgbashift=rh=-8:bh=8,noise=alls=14:allf=t",
            ["Escaneo"] = "format=yuv444p,geq=lum='lum(X,Y)*(0.8+0.2*lt(mod(Y,4),2))':cb='cb(X,Y)':cr='cr(X,Y)'",
            ["Resplandor"] = "gblur=sigma=2.5,eq=brightness=0.05:contrast=1.08:saturation=1.15",
            ["Bordes"] = "edgedetect=low=0.1:high=0.4",
        };
        public static readonly string[] EffectCats = { "Básicos", "Distorsión", "Glitch" };
        public static readonly Dictionary<string, string> EffectCat = new()
        {
            ["Desenfoque"] = "Básicos", ["Nitidez"] = "Básicos", ["Viñeta"] = "Básicos", ["Ruido"] = "Básicos", ["Resplandor"] = "Básicos",
            ["Pixelar"] = "Distorsión", ["Espejo"] = "Distorsión", ["Onda"] = "Distorsión", ["Bordes"] = "Distorsión",
            ["Aberración"] = "Glitch", ["Glitch"] = "Glitch", ["Escaneo"] = "Glitch",
        };

        // color looks defined as numbers so the GPU shader and ffmpeg apply the exact same math: c = c*mul + add, then saturation, then contrast
        public record Look(string Name, string Cat, double Mr, double Mg, double Mb, double Ar, double Ag, double Ab, double Sat, double Con);
        public static readonly Look[] NewLooks =
        {
            new("Clarendon", "Cine", 1.05, 1.05, 1.12, 0, 0, 0, 1.25, 1.15), new("Nashville", "Cálidos", 1.1, 1.0, .88, .03, .02, .04, 1.1, 1.05),
            new("Teal y naranja", "Cine", 1.1, 1.0, .92, -.02, .01, .05, 1.2, 1.1), new("Mate", "Retro", 1, 1, 1, .05, .05, .06, .85, .88),
            new("Película", "Retro", 1.04, 1.0, .92, .02, .02, 0, .9, 1.1), new("Dorado", "Cálidos", 1.15, 1.05, .8, .03, .02, 0, 1.15, 1.05),
            new("Noir", "B/N", 1, 1, 1, -.05, -.05, -.05, 0, 1.45), new("Pastel", "Creativos", 1, 1, 1, .08, .07, .09, .75, .85),
            new("Drama", "Cine", .95, .97, 1.05, -.04, -.04, -.04, 1.1, 1.35), new("Fresco", "Fríos", .92, 1.02, 1.1, 0, .02, .03, 1.1, 1.05),
            new("Atardecer", "Cálidos", 1.2, .95, .8, .04, 0, -.02, 1.3, 1.05), new("Neón", "Creativos", 1.1, .9, 1.25, 0, 0, .04, 1.6, 1.2),
            new("Bosque", "Fríos", .9, 1.1, .9, 0, .02, 0, 1.1, 1.1), new("Rosa", "Creativos", 1.15, .95, 1.05, .05, .01, .04, 1.1, .95),
            new("Desvanecido", "Retro", 1, 1, 1, .1, .1, .1, .8, .8), new("Invierno", "Fríos", .88, .98, 1.15, 0, .01, .05, .9, 1.1),
            new("Verano", "Cálidos", 1.1, 1.05, .9, .03, .03, 0, 1.2, 1.0), new("Contraste B/N", "B/N", 1, 1, 1, 0, 0, 0, 0, 1.3),
        };
        // voice changer (no AI): a pitch shift in semitones plus an ffmpeg filter chain
        public record Voice(string Name, double Semi, string Af);
        public static readonly Voice[] Voices =
        {
            new("Original", 0, ""), new("Ardilla", 7, ""), new("Voz grave", -6, "lowpass=f=6000"), new("Monstruo", -9, "aecho=0.6:0.5:30:0.4"),
            new("Robot", 0, "afftfilt=real='hypot(re,im)*cos(0)':imag='hypot(re,im)*sin(0)':win_size=512:overlap=0.75,volume=5,alimiter=limit=0.95"),
            new("Alienígena", 4, "tremolo=f=9:d=0.7"), new("Eco", 0, "aecho=0.8:0.88:60:0.4"), new("Cueva", 0, "aecho=0.7:0.7:180|360:0.45|0.3"),
            new("Coro", 0, "chorus=0.5:0.9:50|60|40:0.4|0.32|0.3:0.25|0.4|0.3:2|2.3|1.3"), new("Teléfono", 0, "highpass=f=500,lowpass=f=3000,volume=1.8"),
            new("Megáfono", 0, "highpass=f=600,lowpass=f=4200,volume=2.2,alimiter=limit=0.9"),
        };
        public static Voice VoiceOf(string n) => Voices.FirstOrDefault(v => v.Name == n) ?? Voices[0];

        // speed curves as (position in the source 0..1, speed) points; applying one cuts the clip into pieces with constant speeds
        public record Curve(string Name, (double u, double v)[] Pts);
        public static readonly Curve[] Curves =
        {
            new("Normal", new[] { (0.0, 1.0), (1.0, 1.0) }), new("Montaje", new[] { (0.0, 2.0), (0.25, .5), (0.5, 2.0), (0.75, .5), (1.0, 2.0) }),
            new("Héroe", new[] { (0.0, 1.0), (0.3, 1.0), (0.5, .3), (0.7, 1.0), (1.0, 2.0) }), new("Bala", new[] { (0.0, 2.0), (0.35, .25), (0.65, .25), (1.0, 2.0) }),
            new("Salto", new[] { (0.0, 1.0), (0.5, 3.5), (1.0, 1.0) }), new("Flash entrada", new[] { (0.0, 3.5), (0.35, 1.0), (1.0, 1.0) }), new("Flash salida", new[] { (0.0, 1.0), (0.65, 1.0), (1.0, 3.5) }),
        };
        public static readonly Curve[] SpeedFx =
        {
            new("Cámara lenta", new[] { (0.0, .5), (1.0, .5) }), new("Súper lenta", new[] { (0.0, .25), (1.0, .25) }), new("Time-lapse", new[] { (0.0, 4.0), (1.0, 4.0) }),
            new("Aceleración", new[] { (0.0, .5), (1.0, 3.0) }), new("Frenazo", new[] { (0.0, 3.0), (1.0, .4) }), new("Pulso", new[] { (0.0, 1.0), (0.25, .4), (0.5, 1.0), (0.75, .4), (1.0, 1.0) }),
            new("Impacto", new[] { (0.0, 1.0), (0.4, 1.0), (0.5, .25), (0.6, 1.0), (1.0, 1.0) }),
        };
        public static double CurveAt((double u, double v)[] p, double u)
        {
            if (u <= p[0].u) return p[0].v;
            for (int i = 0; i < p.Length - 1; i++) if (u <= p[i + 1].u) return p[i].v + (u - p[i].u) / Math.Max(1e-9, p[i + 1].u - p[i].u) * (p[i + 1].v - p[i].v);
            return p[^1].v;
        }

        // blend modes read from CapCut's own MixMode shaders; Expr is the 8-bit formula (A = layer below, B = this layer) used by ffmpeg's blend filter,
        // Neutral is the colour that leaves the layer below unchanged (what the clip is padded with outside its edges)
        public record Blend(string Name, string Neutral, string Expr);
        public static readonly Blend[] Blends =
        {
            new("Normal", "", ""), new("Multiplicar", "white", "A*B/255"), new("Pantalla", "black", "A+B-A*B/255"),
            new("Superponer", "gray", "if(lt(A,128),2*A*B/255,255-2*(255-A)*(255-B)/255)"), new("Oscurecer", "white", "min(A,B)"), new("Aclarar", "black", "max(A,B)"),
            new("Luz fuerte", "gray", "if(lt(B,128),2*A*B/255,255-2*(255-A)*(255-B)/255)"),
            new("Luz suave", "gray", "255*if(lt(B,128),2*(A/255)*(B/255)+pow(A/255,2)*(1-2*B/255),sqrt(A/255)*(2*B/255-1)+2*(A/255)*(1-B/255))"),
            new("Quemado lineal", "white", "max(A+B-255,0)"), new("Quemado de color", "white", "if(eq(B,0),0,max(255-(255-A)*255/B,0))"), new("Sobreexponer color", "black", "if(eq(B,255),255,min(A*255/(255-B),255))"),
        };
        public static Blend BlendOf(string n) => Blends.FirstOrDefault(b => b.Name == n) ?? Blends[0];
        // CapCut masks (names found in videoeditor.dll): linear, mirror, circle, rectangle, heart; each with size W/H, rotation, feather, round corner
        public static readonly string[] Masks = { "Ninguna", "Lineal", "Espejo", "Círculo", "Rectángulo", "Corazón" };
        public static readonly string[] FilterCats = { "Todos", "Cine", "Cálidos", "Fríos", "B/N", "Retro", "Creativos", "Mis filtros" };
        public static readonly Dictionary<string, string> FilterCat = new()
        {
            ["Blanco y negro"] = "B/N", ["Sepia"] = "Retro", ["Cálido"] = "Cálidos", ["Frío"] = "Fríos", ["Vívido"] = "Cine", ["Vintage"] = "Retro", ["Cine"] = "Cine", ["Negativo"] = "Creativos",
        };
        static Opts()
        {
            foreach (var l in NewLooks)
            {
                string ch(double m, double a) => $"'clip(val*{F(m)}+{F(a)}*maxval,0,maxval)'";
                Filters[l.Name] = $"lutrgb=r={ch(l.Mr, l.Ar)}:g={ch(l.Mg, l.Ag)}:b={ch(l.Mb, l.Ab)},eq=saturation={F(l.Sat)}:contrast={F(l.Con)}"; FilterCat[l.Name] = l.Cat;
            }
        }
        static string F(double d) => d.ToString("0.#####", CultureInfo.InvariantCulture);

        // transitions: the incoming clip animates over `Dur` seconds with progress p 0..1: alpha (if Fade) = p, offset = (Dx,Dy)*(1-p) canvas sizes, scale = S0 + (1-S0)*p
        public record Tr(string Name, string Cat, string Glyph, bool Fade, double Dx, double Dy, double S0);
        public static readonly Tr[] Transitions =
        {
            new("Ninguna", "Básicas", "∅", false, 0, 0, 1), new("Fundido", "Básicas", "◐", true, 0, 0, 1), new("Pop", "Básicas", "●", true, 0, 0, .2),
            new("Deslizar ←", "Deslizar", "←", false, 1, 0, 1), new("Deslizar →", "Deslizar", "→", false, -1, 0, 1), new("Deslizar ↑", "Deslizar", "↑", false, 0, 1, 1), new("Deslizar ↓", "Deslizar", "↓", false, 0, -1, 1),
            new("Aparecer zoom", "Zoom", "⊕", true, 0, 0, .5), new("Acercar", "Zoom", "⊖", true, 0, 0, 1.5), new("Zoom fuerte", "Zoom", "⤢", true, 0, 0, 2.5),
        };
        public static Tr TrOf(string name) => Transitions.FirstOrDefault(t => t.Name == name) ?? Transitions[0];

        // sounds generated with ffmpeg (original synthesis, no third-party audio): name, category, lavfi source, extra filters
        public record Sfx(string Name, string Cat, string Src, string Af);
        public static readonly Sfx[] Sounds =
        {
            new("Pitido", "Sonidos", "sine=frequency=1000:duration=0.4", "afade=t=out:st=0.25:d=0.15,volume=0.5"),
            new("Ding", "Sonidos", "sine=frequency=1320:duration=1.2", "afade=t=out:st=0.05:d=1.1,aecho=0.8:0.7:60:0.4,volume=0.6"),
            new("Campana", "Sonidos", "aevalsrc='0.4*sin(2*PI*880*t)*exp(-3*t)+0.2*sin(2*PI*1760*t)*exp(-4*t)':d=2:s=44100", ""),
            new("Pop", "Sonidos", "aevalsrc='0.8*sin(2*PI*(300*t+3000*t*t))*exp(-25*t)':d=0.3:s=44100", ""),
            new("Whoosh", "Sonidos", "anoisesrc=d=1.2:c=pink:a=0.6", "lowpass=f=2500,afade=t=in:st=0:d=0.5,afade=t=out:st=0.5:d=0.7"),
            new("Impacto grave", "Sonidos", "aevalsrc='0.9*sin(2*PI*(80*t-30*t*t))*exp(-4*t)':d=1.5:s=44100", ""),
            new("Clic", "Sonidos", "aevalsrc='0.7*sin(2*PI*2000*t)*exp(-200*t)':d=0.1:s=44100", ""),
            new("Moneda", "Sonidos", "aevalsrc='0.5*sin(2*PI*if(lt(t,0.08),988,1319)*t)*exp(-4*t)':d=0.8:s=44100", ""),
            new("Aplauso", "Sonidos", "anoisesrc=d=2:c=white:a=0.7", "highpass=f=1000,lowpass=f=6000,tremolo=f=14:d=0.8,afade=t=in:d=0.2,afade=t=out:st=1.5:d=0.5"),
            new("Subida", "Sonidos", "aevalsrc='0.4*sin(2*PI*(200*t+150*t*t))*(t/3)':d=3:s=44100", "afade=t=out:st=2.8:d=0.2"),
            new("Ambiente acorde", "Música", "aevalsrc='0.12*(sin(2*PI*130.81*t)+sin(2*PI*164.81*t)+sin(2*PI*196*t)+sin(2*PI*246.94*t))*(0.8+0.2*sin(2*PI*0.25*t))':d=10:s=44100", "afade=t=in:d=1.5,afade=t=out:st=8:d=2"),
            new("Pulso 120 bpm", "Música", "aevalsrc='0.8*sin(2*PI*55*t)*exp(-12*mod(t,0.5))':d=8:s=44100", ""),
            new("Bajo menor", "Música", "aevalsrc='0.25*(sin(2*PI*110*t)+0.5*sin(2*PI*220*t)+0.4*sin(2*PI*261.63*t))*(0.7+0.3*sin(2*PI*0.5*t))':d=10:s=44100", "afade=t=in:d=1,afade=t=out:st=8:d=2"),
        };
    }

    public class Kf { public string Prop { get; set; } public double T { get; set; } public double V { get; set; } }

    // Flat segment: CapCut's SegmentVideo/Audio/Text + their materials, with per-property keyframes (T relative to segment start)
    public class Seg
    {
        public string Kind { get; set; } = "video"; // video | image | audio | text
        public string Path { get; set; } public bool HasAudio { get; set; }
        public double Src { get; set; } public double SrcLen { get; set; } public double Start { get; set; } public double Dur { get; set; } public int Track { get; set; }
        public double Speed { get; set; } = 1; public double Volume { get; set; } = 1; public double Opacity { get; set; } = 1;
        public double X { get; set; } public double Y { get; set; } public double Scale { get; set; } = 1; public double Rotation { get; set; }
        public double Brightness { get; set; } public double Contrast { get; set; } = 1; public double Saturation { get; set; } = 1;
        public string Filter { get; set; } = "Ninguno"; public string Effect { get; set; } = "Ninguno"; public bool Flip { get; set; }
        public double FadeIn { get; set; } public double FadeOut { get; set; }
        public string Trans { get; set; } = "Ninguna"; public double TransDur { get; set; } = 0.6;
        public string Blend { get; set; } = "Normal"; public string Voice { get; set; } = "Original"; public double Pitch { get; set; } public bool PitchWithSpeed { get; set; } public string Font { get; set; } = "";
        public string Mask { get; set; } = "Ninguna"; public double MaskW { get; set; } = .5; public double MaskH { get; set; } = .5; public double MaskRot { get; set; } public double MaskFeather { get; set; } public double MaskRound { get; set; } public bool MaskInvert { get; set; }

        // mask coverage 0..1 as an ffmpeg expression in pixels (X,Y,W,H of the clip frame); same maths as the preview shader in Grade.cs
        public string MaskExpr()
        {
            var a = F(MaskRot * Math.PI / 180); string nx = "((X-W/2)/W)", ny = "((Y-H/2)/H)";
            string qx = $"(({nx}*cos({a})+{ny}*sin({a}))/{F(MaskW / 2)})", qy = $"((-{nx}*sin({a})+{ny}*cos({a}))/{F(MaskH / 2)})", rr = F(Math.Clamp(MaskRound, 0, 1));
            string d = Mask switch
            {
                "Lineal" => qy, "Espejo" => $"(abs({qy})-1)", "Círculo" => $"(hypot({qx},{qy})-1)",
                "Rectángulo" => $"(hypot(max(abs({qx})-1+{rr},0),max(abs({qy})-1+{rr},0))+min(max(abs({qx})-1+{rr},abs({qy})-1+{rr}),0)-{rr})",
                _ => $"((({qx}*{qx}*1.69+({qy}*-1.3+.2)*({qy}*-1.3+.2)-1)^3)-{qx}*{qx}*1.69*({qy}*-1.3+.2)^3)"
            };
            string fe = F(Math.Max(MaskFeather * .5, .002)), m = $"clip(({fe}-{d})/{F(2 * Math.Max(MaskFeather * .5, .002))},0,1)";
            return MaskInvert ? $"(1-{m})" : m;
        }
        public string Text { get; set; } = ""; public int TextSize { get; set; } = 64; public string TextColor { get; set; } = "white";
        public List<Kf> Kfs { get; set; } = new();
        public string Name => Kind == "text" ? "T " + Text : System.IO.Path.GetFileName(Path);
        public double End => Start + Dur;

        public double Static(string p) => p switch { "x" => X, "y" => Y, "scale" => Scale, "rotation" => Rotation, _ => Volume };
        void SetStatic(string p, double v) { switch (p) { case "x": X = v; break; case "y": Y = v; break; case "scale": Scale = v; break; case "rotation": Rotation = v; break; default: Volume = v; break; } }
        public List<Kf> Of(string p) => Kfs.Where(k => k.Prop == p).OrderBy(k => k.T).ToList();
        public bool HasKf(string p, double rel) => Kfs.Any(k => k.Prop == p && Math.Abs(k.T - rel) < .05);

        // same math CapCut's KeyframeUtils::linear_interpolation exposes (found by disassembly)
        public double Get(string p, double rel)
        {
            var k = Of(p);
            if (k.Count == 0) return Static(p);
            if (rel <= k[0].T) return k[0].V;
            for (int i = 0; i < k.Count - 1; i++)
                if (rel < k[i + 1].T) return k[i].V + (rel - k[i].T) / (k[i + 1].T - k[i].T) * (k[i + 1].V - k[i].V);
            return k[^1].V;
        }
        public void Set(string p, double v, double rel)
        {
            if (!Kfs.Any(k => k.Prop == p)) { SetStatic(p, v); return; }
            var e = Kfs.FirstOrDefault(k => k.Prop == p && Math.Abs(k.T - rel) < .05);
            if (e != null) e.V = v; else Kfs.Add(new Kf { Prop = p, T = Math.Round(rel, 2), V = v });
        }
        public void ToggleKf(string p, double rel)
        {
            var e = Kfs.FirstOrDefault(k => k.Prop == p && Math.Abs(k.T - rel) < .05);
            if (e != null) { Kfs.Remove(e); if (!Kfs.Any(k => k.Prop == p)) SetStatic(p, e.V); }
            else Kfs.Add(new Kf { Prop = p, T = Math.Round(rel, 2), V = Get(p, rel) });
        }

        static string F(double d) => d.ToString("0.#####", CultureInfo.InvariantCulture);
        // piecewise-linear ffmpeg expression in `t`; off shifts keyframe times (abs timeline vs segment-relative)
        public string Expr(string p, double off, double mul = 1)
        {
            var k = Of(p);
            if (k.Count == 0) return F(Static(p) * mul);
            var e = F(k[^1].V * mul);
            for (int i = k.Count - 2; i >= 0; i--)
            {
                double t0 = off + k[i].T, t1 = off + k[i + 1].T;
                e = $"if(lt(t,{F(t1)}),{F(k[i].V * mul)}+({F((k[i + 1].V - k[i].V) * mul)})*(t-{F(t0)})/{F(t1 - t0)},{e})";
            }
            return $"if(lt(t,{F(off + k[0].T)}),{F(k[0].V * mul)},{e})";
        }
    }

    public class Project
    {
        public string Name { get; set; } public string Canvas { get; set; } = "16:9"; public List<Seg> Segs { get; set; } = new();
        public List<int> Hidden { get; set; } = new(); public List<int> Muted { get; set; } = new(); public List<int> Locked { get; set; } = new(); // per-track eye / speaker / lock (like CapCut's track header)
        // a clip whose successor starts a transition right at its end stays on screen (last frame) while the transition runs
        public double Hold(Seg s) => s.Kind is "video" or "image" ? Segs.Where(n => n != s && n.Track == s.Track && n.Trans != "Ninguna" && n.Kind is "video" or "image" && Math.Abs(n.Start - s.End) < .06).Select(n => Math.Min(n.TransDur, n.Dur)).DefaultIfEmpty(0).Max() : 0;
    }

    public static class Ff
    {
        static string F(double d) => d.ToString("0.#####", CultureInfo.InvariantCulture);

        public static readonly string Cache = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "minicut_cache");
        static string Key(string path) => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(path)))[..12];
        public static string Strip(string path) => System.IO.Path.Combine(Cache, "st_" + Key(path) + ".png");

        // one wide PNG with n evenly spaced frames (tile i ~ time i*step); the timeline crops it per clip
        public static (int n, double step) MakeStrip(string src, double dur)
        {
            int n = (int)Math.Clamp(Math.Ceiling(dur / 2), 1, 80); double step = dur / n; var png = Strip(src);
            if (!File.Exists(png)) Exec("ffmpeg", $"-y -v error -nostdin -i \"{src}\" -an -vf fps=1/{F(step)},scale=-2:80,tile={n}x1 -frames:v 1 \"{png}\"");
            return (n, step);
        }

        // bigger still frame for project/template cards on the home screen
        public static string Cover(string path, double at) => System.IO.Path.Combine(Cache, "cv_" + Key(path) + "_" + (int)at + ".jpg");
        public static string MakeCover(string path, double at, bool image)
        {
            var jpg = Cover(path, at); if (File.Exists(jpg)) return jpg;
            Exec("ffmpeg", $"-y -v error -nostdin {(image ? "" : $"-ss {F(at)} ")}-i \"{path}\" -frames:v 1 -vf scale=-2:240 \"{jpg}\"");
            return File.Exists(jpg) ? jpg : null;
        }

        public static string Raw(string path) => System.IO.Path.Combine(Cache, "au_" + Key(path) + ".raw");

        // decoded 16-bit stereo 44.1k PCM, read in real time by the preview mixer
        public static bool MakeRaw(string src)
        {
            var raw = Raw(src); if (File.Exists(raw)) return true;
            var tmp = raw + ".part";
            Exec("ffmpeg", $"-y -v error -nostdin -i \"{src}\" -vn -ac 2 -ar 44100 -f s16le \"{tmp}\"");
            if (File.Exists(tmp) && new FileInfo(tmp).Length > 0) { File.Move(tmp, raw, true); return true; }
            return false;
        }

        // 20 peaks per second (0-255) for the waveform, from the decoded PCM
        public static byte[] Peaks(string raw)
        {
            var list = new List<byte>(); var buf = new byte[2205 * 4];
            using var s = new FileStream(raw, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            while (true)
            {
                int got = 0; while (got < buf.Length) { int r = s.Read(buf, got, buf.Length - got); if (r <= 0) break; got += r; }
                if (got < 2) break;
                int mx = 0; for (int i = 0; i + 1 < got; i += 2) { int v = Math.Abs((short)(buf[i] | (buf[i + 1] << 8))); if (v > mx) mx = v; }
                list.Add((byte)Math.Min(255, mx / 128)); if (got < buf.Length) break;
            }
            return list.ToArray();
        }
        public static string Thumb(string path) => System.IO.Path.Combine(Cache, "th_" + Key(path) + ".png");

        // PATH first, then winget's install folder (a fresh install is not on an already-open terminal's PATH); resolved once
        static readonly Dictionary<string, string> bins = new();
        static string Bin(string exe)
        {
            lock (bins)
            {
                if (bins.TryGetValue(exe, out var b)) return b;
                b = exe;
                var onPath = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Any(dir => { try { return dir != "" && File.Exists(System.IO.Path.Combine(dir, exe + ".exe")); } catch { return false; } });
                if (!onPath)
                {
                    var pk = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
                    if (Directory.Exists(pk)) b = Directory.EnumerateFiles(pk, exe + ".exe", SearchOption.AllDirectories).FirstOrDefault() ?? exe;
                }
                return bins[exe] = b;
            }
        }
        public static string Exec(string exe, string args, Action<Process> started = null)
        {
            var p = Process.Start(new ProcessStartInfo(Bin(exe), args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            started?.Invoke(p);
            var err = p.StandardError.ReadToEndAsync(); var o = p.StandardOutput.ReadToEnd(); p.WaitForExit();
            return exe == "ffmpeg" ? err.Result : o;
        }

        // everything that happens to a clip's sound before volume/fades: pitch shift + voice effect (duration-preserving), then speed (tempo, or pitch-follows-speed)
        public static string AudioChain(Seg s)
        {
            var v = Opts.VoiceOf(s.Voice); var l = new List<string>(); double semi = s.Pitch + v.Semi;
            if (Math.Abs(semi) > .01) { double f = Math.Pow(2, semi / 12); l.Add($"asetrate={F(44100 * f)}"); l.Add("aresample=44100"); l.Add(Atempo(1 / f)); }
            if (v.Af != "") l.Add(v.Af);
            if (s.Speed != 1) l.Add(s.PitchWithSpeed ? $"asetrate={F(44100 * s.Speed)},aresample=44100" : Atempo(s.Speed));
            return string.Join(",", l);
        }
        // processed copy of a clip's decoded PCM (pitch / voice / speed baked in) that the live mixer plays instead of the original
        public static string ProcPath(string raw, string chain, int rs, int len) => System.IO.Path.Combine(Cache, "pr_" + Key(raw + "|" + chain + "|" + rs + "|" + len) + ".raw");
        // only the seconds [rs, rs+len) of the source are processed, so a clip cut into pieces does not re-process the whole file for each piece
        public static bool MakeProc(string raw, string chain, int rs, int len)
        {
            var o = ProcPath(raw, chain, rs, len); if (File.Exists(o)) return true; var tmp = o + ".part";
            Exec("ffmpeg", $"-y -v error -nostdin -f s16le -ar 44100 -ac 2 -ss {rs} -t {len} -i \"{raw}\" " + (chain != "" ? $"-af \"{chain}\" " : "") + $"-f s16le -ar 44100 -ac 2 \"{tmp}\"");
            if (File.Exists(tmp)) File.Move(tmp, o, true); return File.Exists(o);
        }

        static string Atempo(double s)
        {
            var l = new List<string>();
            while (s > 2) { l.Add("atempo=2"); s /= 2; }
            while (s < 0.5) { l.Add("atempo=0.5"); s /= 0.5; }
            l.Add("atempo=" + F(s)); return string.Join(",", l);
        }

        static string Esc(string t) => t.Replace("\\", "").Replace("'", "").Replace("\"", "").Replace("%", "").Replace(":", "\\:");

        // Builds "inputs + -filter_complex_script" args for the whole timeline. sc scales the canvas (preview).
        public static (string args, double total) Build(Project p, double sc, bool audio, string tmp, double? at = null)
        {
            bool On(Seg s) => at == null || (at.Value >= s.Start && at.Value < s.End + p.Hold(s)); // held clips stay under a running transition
            var (W0, H0) = Opts.Size(p.Canvas); int W = (int)(W0 * sc / 2) * 2, H = (int)(H0 * sc / 2) * 2;
            double total = p.Segs.Count == 0 ? 1 : Math.Max(.1, p.Segs.Max(s => s.End));
            var inp = new StringBuilder(); var g = new StringBuilder(); var idx = new Dictionary<Seg, int>(); int n = 0;
            foreach (var s in p.Segs.Where(s => s.Kind != "text" && On(s)))
            {
                idx[s] = n++;
                if (at != null) inp.Append(s.Kind == "image" ? $"-loop 1 -framerate 30 -t 0.5 -i \"{s.Path}\" " : $"-ss {F(s.Src + (Math.Min(at.Value, s.End - .05) - s.Start) * s.Speed)} -t 0.5 -i \"{s.Path}\" ");
                else inp.Append(s.Kind == "image" ? $"-loop 1 -framerate 30 -t {F(s.Dur)} -i \"{s.Path}\" " : $"-ss {F(s.Src)} -t {F(s.Dur * s.Speed)} -i \"{s.Path}\" ");
            }
            string prev = "b0"; int k = 0;
            g.Append(at == null ? $"color=c=black:s={W}x{H}:r=30:d={F(total)}[b0];" : $"color=c=black:s={W}x{H}:r=30:d=1,setpts=PTS+{F(at.Value)}/TB[b0];");
            foreach (var s in p.Segs.Where(s => s.Kind is "video" or "image" && On(s) && !p.Hidden.Contains(s.Track)).OrderByDescending(s => s.Track).ThenBy(s => s.Start))
            {
                int i = idx[s];
                var c = new List<string> { $"setpts=(PTS-STARTPTS)/{F(s.Speed)}", "fps=30", $"setpts=PTS+{F(at ?? s.Start)}/TB" };
                if (s.Flip) c.Add("hflip");
                c.Add($"scale={W}:{H}:force_original_aspect_ratio=decrease");
                if (s.Brightness != 0 || s.Contrast != 1 || s.Saturation != 1) c.Add($"eq=brightness={F(s.Brightness)}:contrast={F(s.Contrast)}:saturation={F(s.Saturation)}");
                if (Opts.Filters.GetValueOrDefault(s.Filter, "") != "") c.Add(Opts.Filters[s.Filter]);
                if (Opts.Effects.GetValueOrDefault(s.Effect, "") != "") c.Add(Opts.Effects[s.Effect]);
                c.Add("format=rgba");
                if (s.Opacity < 1) c.Add($"colorchannelmixer=aa={F(s.Opacity)}");
                if (s.Mask != "Ninguna") c.Add($"format=yuva444p,geq=lum='lum(X,Y)':cb='cb(X,Y)':cr='cr(X,Y)':a='alpha(X,Y)*{s.MaskExpr()}',format=rgba");
                var tr = Opts.TrOf(s.Trans); double td = Math.Max(.05, Math.Min(s.TransDur, s.Dur)); bool hasTr = tr.Name != "Ninguna";
                string tp = $"clip((t-{F(s.Start)})/{F(td)},0,1)";
                if (hasTr && tr.Fade) c.Add($"fade=t=in:st={F(s.Start)}:d={F(td)}:alpha=1");
                if (s.FadeIn > 0) c.Add($"fade=t=in:st={F(s.Start)}:d={F(s.FadeIn)}:alpha=1");
                if (s.FadeOut > 0) c.Add($"fade=t=out:st={F(Math.Max(s.Start, s.End - s.FadeOut))}:d={F(s.FadeOut)}:alpha=1");
                if (s.Of("scale").Count > 0 || (hasTr && tr.S0 != 1))
                {
                    var e = $"max(0.02,({s.Expr("scale", s.Start)})" + (hasTr && tr.S0 != 1 ? $"*({F(tr.S0)}+{F(1 - tr.S0)}*{tp})" : "") + ")";
                    c.Add($"scale=w='trunc(iw*{e}/2)*2':h='trunc(ih*{e}/2)*2':eval=frame");
                }
                else if (s.Scale != 1) c.Add($"scale=trunc(iw*{F(s.Scale)}/2)*2:trunc(ih*{F(s.Scale)}/2)*2");
                if (s.Of("rotation").Count > 0 || s.Rotation != 0) c.Add($"rotate=a='({s.Expr("rotation", s.Start)})*PI/180':ow='hypot(iw,ih)':oh='hypot(iw,ih)':c=none");
                g.Append($"[{i}:v]{string.Join(",", c)}[v{i}];");
                string tdx = hasTr && tr.Dx != 0 ? $"+{F(tr.Dx * W)}*(1-{tp})" : "", tdy = hasTr && tr.Dy != 0 ? $"+{F(tr.Dy * H)}*(1-{tp})" : "";
                var bl = Opts.BlendOf(s.Blend); string ox = $"x='(main_w-overlay_w)/2+{s.Expr("x", s.Start, sc)}{tdx}':y='(main_h-overlay_h)/2+{s.Expr("y", s.Start, sc)}{tdy}':eval=frame:shortest=0";
                string en = $"between(t,{F(s.Start)},{F(s.End + p.Hold(s))})";
                if (bl.Name == "Normal") g.Append($"[{prev}][v{i}]overlay={ox}:enable='{en}'[b{++k}];");
                else
                {   // blend: put the clip on a canvas filled with the mode's neutral colour, then combine it with what is below using CapCut's formula
                    g.Append($"color=c={bl.Neutral}:s={W}x{H}:r=30:d={F(total)}[n{i}];[n{i}][v{i}]overlay={ox}[c{i}];[{prev}]format=gbrp[pa{i}];[c{i}]format=gbrp[pb{i}];");
                    g.Append($"[pa{i}][pb{i}]blend=all_expr='{bl.Expr}':enable='{en}'[b{++k}];");
                }
                prev = "b" + k;
            }
            foreach (var s in p.Segs.Where(s => s.Kind == "text" && s.Text != "" && On(s) && !p.Hidden.Contains(s.Track)).OrderBy(s => s.Start))
            {
                g.Append($"[{prev}]drawtext=fontfile='{(s.Font != "" && File.Exists(s.Font) ? Library.FfPath(s.Font) : "C\\:/Windows/Fonts/arial.ttf")}':text='{Esc(s.Text)}':fontcolor={s.TextColor}:fontsize={F(s.TextSize * sc)}:box=1:boxcolor=black@0.5:boxborderw=10:x='(w-tw)/2+{s.Expr("x", s.Start, sc)}':y='(h-th)/2+{s.Expr("y", s.Start, sc)}':enable='between(t,{F(s.Start)},{F(s.End)})'[b{++k}];");
                prev = "b" + k;
            }
            g.Append($"[{prev}]format=yuv420p[vout]");
            if (audio)
            {
                // audio is read from the same decoded PCM the live preview mixes (exact sample positions, identical to what you heard);
                // asking ffmpeg to -ss into some AAC files shifts the audio by ~1 frame relative to a full decode
                var aidx = new Dictionary<Seg, int>();
                foreach (var s in p.Segs.Where(s => ((s.Kind == "video" && s.HasAudio) || s.Kind == "audio") && !p.Muted.Contains(s.Track)))
                {
                    if (MakeRaw(s.Path)) { aidx[s] = n++; inp.Append($"-f s16le -ar 44100 -ac 2 -ss {F(s.Src)} -t {F(s.Dur * s.Speed)} -i \"{Raw(s.Path)}\" "); }
                    else aidx[s] = idx[s];
                }
                var labels = new List<string>();
                foreach (var s in p.Segs.Where(s => ((s.Kind == "video" && s.HasAudio) || s.Kind == "audio") && !p.Muted.Contains(s.Track)))
                {
                    var a = new List<string>();
                    var chain = AudioChain(s); if (chain != "") a.Add(chain);
                    a.Add("aresample=44100"); a.Add("aformat=channel_layouts=stereo");
                    if (s.Of("volume").Count > 0 || s.Volume != 1) a.Add($"volume='{s.Expr("volume", 0)}':eval=frame");
                    if (s.FadeIn > 0) a.Add($"afade=t=in:st=0:d={F(s.FadeIn)}");
                    if (s.FadeOut > 0) a.Add($"afade=t=out:st={F(Math.Max(0, s.Dur - s.FadeOut))}:d={F(s.FadeOut)}");
                    a.Add($"adelay=delays={(int)(s.Start * 1000)}:all=1");
                    g.Append($";[{aidx[s]}:a]{string.Join(",", a)}[a{aidx[s]}]"); labels.Add($"[a{aidx[s]}]");
                }
                g.Append(labels.Count == 0 ? $";anullsrc=r=44100:cl=stereo:d={F(total)}[aout]" : $";{string.Concat(labels)}amix=inputs={labels.Count}:normalize=0:duration=longest[aout]");
            }
            var script = System.IO.Path.Combine(tmp, "graph_" + Guid.NewGuid().ToString("N")[..6] + ".txt");
            File.WriteAllText(script, g.ToString());
            return ($"-y -v error -nostdin {inp}-/filter_complex \"{script}\" -map [vout] " + (audio ? "-map [aout] " : ""), total);
        }

        public static string Export(Project p, string outPath, double sc = 1, string preset = "veryfast")
        {
            var tmp = Directory.CreateTempSubdirectory("minicut").FullName;
            try
            {
                var (a, total) = Build(p, sc, true, tmp);
                var log = Exec("ffmpeg", $"{a}-t {F(total)} -r 30 -c:v libx264 -preset {preset} {(sc < 1 ? "-crf 26 " : "")}-pix_fmt yuv420p -c:a aac \"{outPath}\"");
                return File.Exists(outPath) ? null : "Error: " + log.Split('\n').Reverse().FirstOrDefault(l => l.Trim() != "")  + "\n" + a + "\n" + File.ReadAllText(System.Text.RegularExpressions.Regex.Match(a, "-/filter_complex \"([^\"]+)\"").Groups[1].Value);
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }

        public static string Frame(Project p, double t, string png, Action<Process> started)
        {
            var tmp = Directory.CreateTempSubdirectory("minicut").FullName;
            try
            {
                var (a, tot) = Build(p, 0.5, false, tmp, Math.Max(0, Math.Min(t, p.Segs.Count == 0 ? 0 : p.Segs.Max(s => s.End) - 0.04)));
                var log = Exec("ffmpeg", $"{a}-frames:v 1 \"{png}\"", started);
                return File.Exists(png) ? null : log;
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }
    }
}













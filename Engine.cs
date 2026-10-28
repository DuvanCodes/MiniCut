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

    public class Project { public string Canvas { get; set; } = "16:9"; public List<Seg> Segs { get; set; } = new(); }

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

        // 20 peaks per second (0-255) from the audio track, for the waveform
        public static byte[] Peaks(string src)
        {
            var p = Process.Start(new ProcessStartInfo(Bin("ffmpeg"), $"-v error -nostdin -i \"{src}\" -vn -ac 1 -ar 8000 -f s16le -") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            var err = p.StandardError.ReadToEndAsync(); var list = new List<byte>(); var buf = new byte[800]; var s = p.StandardOutput.BaseStream;
            while (true)
            {
                int got = 0; while (got < buf.Length) { int r = s.Read(buf, got, buf.Length - got); if (r <= 0) break; got += r; }
                if (got < 2) break;
                int mx = 0; for (int i = 0; i + 1 < got; i += 2) { int v = Math.Abs((short)(buf[i] | (buf[i + 1] << 8))); if (v > mx) mx = v; }
                list.Add((byte)Math.Min(255, mx / 128)); if (got < buf.Length) break;
            }
            p.WaitForExit(); return list.ToArray();
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
            bool On(Seg s) => at == null || (at.Value >= s.Start && at.Value < s.End);
            var (W0, H0) = Opts.Size(p.Canvas); int W = (int)(W0 * sc / 2) * 2, H = (int)(H0 * sc / 2) * 2;
            double total = p.Segs.Count == 0 ? 1 : Math.Max(.1, p.Segs.Max(s => s.End));
            var inp = new StringBuilder(); var g = new StringBuilder(); var idx = new Dictionary<Seg, int>(); int n = 0;
            foreach (var s in p.Segs.Where(s => s.Kind != "text" && On(s)))
            {
                idx[s] = n++;
                if (at != null) inp.Append(s.Kind == "image" ? $"-loop 1 -framerate 30 -t 0.5 -i \"{s.Path}\" " : $"-ss {F(s.Src + (at.Value - s.Start) * s.Speed)} -t 0.5 -i \"{s.Path}\" ");
                else inp.Append(s.Kind == "image" ? $"-loop 1 -framerate 30 -t {F(s.Dur)} -i \"{s.Path}\" " : $"-ss {F(s.Src)} -t {F(s.Dur * s.Speed)} -i \"{s.Path}\" ");
            }
            string prev = "b0"; int k = 0;
            g.Append(at == null ? $"color=c=black:s={W}x{H}:r=30:d={F(total)}[b0];" : $"color=c=black:s={W}x{H}:r=30:d=1,setpts=PTS+{F(at.Value)}/TB[b0];");
            foreach (var s in p.Segs.Where(s => s.Kind is "video" or "image" && On(s)).OrderByDescending(s => s.Track).ThenBy(s => s.Start))
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
                if (s.FadeIn > 0) c.Add($"fade=t=in:st={F(s.Start)}:d={F(s.FadeIn)}:alpha=1");
                if (s.FadeOut > 0) c.Add($"fade=t=out:st={F(Math.Max(s.Start, s.End - s.FadeOut))}:d={F(s.FadeOut)}:alpha=1");
                if (s.Of("scale").Count > 0) { var e = $"max(0.02,{s.Expr("scale", s.Start)})"; c.Add($"scale=w='trunc(iw*{e}/2)*2':h='trunc(ih*{e}/2)*2':eval=frame"); }
                else if (s.Scale != 1) c.Add($"scale=trunc(iw*{F(s.Scale)}/2)*2:trunc(ih*{F(s.Scale)}/2)*2");
                if (s.Of("rotation").Count > 0 || s.Rotation != 0) c.Add($"rotate=a='({s.Expr("rotation", s.Start)})*PI/180':ow='hypot(iw,ih)':oh='hypot(iw,ih)':c=none");
                g.Append($"[{i}:v]{string.Join(",", c)}[v{i}];");
                g.Append($"[{prev}][v{i}]overlay=x='(main_w-overlay_w)/2+{s.Expr("x", s.Start, sc)}':y='(main_h-overlay_h)/2+{s.Expr("y", s.Start, sc)}':eval=frame:enable='between(t,{F(s.Start)},{F(s.End)})':shortest=0[b{++k}];");
                prev = "b" + k;
            }
            foreach (var s in p.Segs.Where(s => s.Kind == "text" && s.Text != "" && On(s)).OrderBy(s => s.Start))
            {
                g.Append($"[{prev}]drawtext=fontfile='C\\:/Windows/Fonts/arial.ttf':text='{Esc(s.Text)}':fontcolor={s.TextColor}:fontsize={F(s.TextSize * sc)}:box=1:boxcolor=black@0.5:boxborderw=10:x='(w-tw)/2+{s.Expr("x", s.Start, sc)}':y='(h-th)/2+{s.Expr("y", s.Start, sc)}':enable='between(t,{F(s.Start)},{F(s.End)})'[b{++k}];");
                prev = "b" + k;
            }
            g.Append($"[{prev}]format=yuv420p[vout]");
            if (audio)
            {
                var labels = new List<string>();
                foreach (var s in p.Segs.Where(s => (s.Kind == "video" && s.HasAudio) || s.Kind == "audio"))
                {
                    var a = new List<string>();
                    if (s.Speed != 1) a.Add(Atempo(s.Speed));
                    a.Add("aresample=44100"); a.Add("aformat=channel_layouts=stereo");
                    if (s.Of("volume").Count > 0 || s.Volume != 1) a.Add($"volume='{s.Expr("volume", 0)}':eval=frame");
                    if (s.FadeIn > 0) a.Add($"afade=t=in:st=0:d={F(s.FadeIn)}");
                    if (s.FadeOut > 0) a.Add($"afade=t=out:st={F(Math.Max(0, s.Dur - s.FadeOut))}:d={F(s.FadeOut)}");
                    a.Add($"adelay=delays={(int)(s.Start * 1000)}:all=1");
                    g.Append($";[{idx[s]}:a]{string.Join(",", a)}[a{idx[s]}]"); labels.Add($"[a{idx[s]}]");
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










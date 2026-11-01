using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MiniCut
{
    // A 3D LUT (.cube) packed as a 2D strip of N tiles so a pixel shader can sample it (WPF has no 3D textures).
    public class Lut
    {
        public string Name, Path; public int N; public BitmapSource Strip;
        ImageBrush brush; public ImageBrush Brush => brush ??= new ImageBrush(Strip) { Stretch = Stretch.Fill };
    }
    public record FontOpt(string Label, string File);

    // User-supplied libraries: filters (.cube), a music folder and fonts. Nothing here is downloaded from anywhere.
    public static class Library
    {
        public static readonly string Root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiniCut");
        public static string LutDir => System.IO.Path.Combine(Root, "Luts");
        public static string FontDir => System.IO.Path.Combine(Root, "Fonts");
        static string SettingsFile => System.IO.Path.Combine(Root, "settings.json");
        public static readonly Dictionary<string, Lut> Luts = new();
        public static string MusicDir { get; private set; }
        public class Cfg { public string MusicDir { get; set; } }

        public static void Load()
        {
            Directory.CreateDirectory(LutDir); Directory.CreateDirectory(FontDir);
            foreach (var f in Directory.GetFiles(LutDir, "*.cube")) try { Add(f); } catch { }
            try { if (File.Exists(SettingsFile)) MusicDir = JsonSerializer.Deserialize<Cfg>(File.ReadAllText(SettingsFile))?.MusicDir; } catch { }
        }
        public static void SetMusicDir(string dir) { MusicDir = dir; try { File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new Cfg { MusicDir = dir })); } catch { } }
        public static bool InMusic(string path) => MusicDir != null && path.StartsWith(MusicDir, StringComparison.OrdinalIgnoreCase);

        // ffmpeg filter-option path: forward slashes and an escaped drive colon, like the drawtext fontfile already used
        public static string FfPath(string p) => p.Replace('\\', '/').Replace(":", "\\:").Replace("'", "");

        public static string Import(string src)
        {
            var dest = System.IO.Path.Combine(LutDir, System.IO.Path.GetFileName(src)); Parse(src); // throws on a bad file before copying
            if (!string.Equals(System.IO.Path.GetFullPath(src), dest, StringComparison.OrdinalIgnoreCase)) File.Copy(src, dest, true);
            return Add(dest).Name;
        }
        static Lut Add(string cube)
        {
            var l = Parse(cube); Luts[l.Name] = l;
            Opts.Filters[l.Name] = $"lut3d=file='{FfPath(cube)}'"; Opts.FilterCat[l.Name] = "Mis filtros"; return l;
        }
        public static void Remove(string name)
        {
            if (!Luts.Remove(name, out var l)) return; Opts.Filters.Remove(name); Opts.FilterCat.Remove(name); try { File.Delete(l.Path); } catch { }
        }

        public static Lut Parse(string path)
        {
            int n = 0; double[] lo = { 0, 0, 0 }, hi = { 1, 1, 1 }; var data = new List<float>();
            double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
            foreach (var raw in File.ReadLines(path))
            {
                var l = raw.Trim(); if (l == "" || l[0] == '#') continue; var t = l.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (t[0] == "LUT_3D_SIZE") n = int.Parse(t[1]);
                else if (t[0] == "LUT_1D_SIZE") throw new FormatException("Solo se admiten LUT 3D (LUT_3D_SIZE).");
                else if (t[0] == "DOMAIN_MIN") lo = new[] { D(t[1]), D(t[2]), D(t[3]) };
                else if (t[0] == "DOMAIN_MAX") hi = new[] { D(t[1]), D(t[2]), D(t[3]) };
                else if (char.IsLetter(t[0][0])) continue; // TITLE and other headers
                else if (t.Length >= 3) for (int i = 0; i < 3; i++) data.Add((float)D(t[i]));
            }
            if (n < 2 || data.Count != n * n * n * 3) throw new FormatException("Archivo .cube no válido.");
            int m = Math.Min(n, 33); // keeps the strip (m*m wide) inside GPU texture limits; bigger LUTs are resampled
            float S(int r, int g, int b, int c) => data[((b * n + g) * n + r) * 3 + c];
            float Tri(float fr, float fg, float fb, int c)
            {
                int r0 = (int)fr, g0 = (int)fg, b0 = (int)fb, r1 = Math.Min(r0 + 1, n - 1), g1 = Math.Min(g0 + 1, n - 1), b1 = Math.Min(b0 + 1, n - 1); float dr = fr - r0, dg = fg - g0, db = fb - b0;
                float Lr(int g, int b) => S(r0, g, b, c) * (1 - dr) + S(r1, g, b, c) * dr;
                float Lg(int b) => Lr(g0, b) * (1 - dg) + Lr(g1, b) * dg;
                return Lg(b0) * (1 - db) + Lg(b1) * db;
            }
            var px = new byte[m * m * m * 4]; float k = (n - 1) / (float)(m - 1);
            for (int b = 0; b < m; b++) for (int g = 0; g < m; g++) for (int r = 0; r < m; r++)
            {
                int o = (g * m * m + b * m + r) * 4;
                for (int c = 0; c < 3; c++) px[o + 2 - c] = (byte)Math.Clamp((int)Math.Round((Tri(r * k, g * k, b * k, c) - lo[c]) / (hi[c] - lo[c]) * 255), 0, 255); // BGRA
                px[o + 3] = 255;
            }
            var bmp = BitmapSource.Create(m * m, m, 96, 96, PixelFormats.Bgra32, null, px, m * m * 4); bmp.Freeze();
            return new Lut { Name = System.IO.Path.GetFileNameWithoutExtension(path), Path = path, N = m, Strip = bmp };
        }

        // ---- fonts: a few system ones plus whatever .ttf/.otf the user adds to the Fonts folder (export uses the same file with drawtext)
        public static List<FontOpt> Fonts()
        {
            var sys = Environment.GetFolderPath(Environment.SpecialFolder.Fonts); var l = new List<FontOpt>();
            foreach (var (n, f) in new[] { ("Arial", "arial.ttf"), ("Impact", "impact.ttf"), ("Times", "times.ttf"), ("Georgia", "georgia.ttf"), ("Verdana", "verdana.ttf"), ("Comic Sans", "comic.ttf"), ("Courier", "cour.ttf"), ("Bahnschrift", "bahnschrift.ttf") })
                if (File.Exists(System.IO.Path.Combine(sys, f))) l.Add(new FontOpt(n, System.IO.Path.Combine(sys, f)));
            foreach (var f in Directory.GetFiles(FontDir).Where(f => f.EndsWith(".ttf", true, null) || f.EndsWith(".otf", true, null))) l.Add(new FontOpt(System.IO.Path.GetFileNameWithoutExtension(f), f));
            return l;
        }
        public static string ImportFont(string src) { var d = System.IO.Path.Combine(FontDir, System.IO.Path.GetFileName(src)); File.Copy(src, d, true); return d; }

        static readonly Dictionary<string, FontFamily> fams = new();
        public static FontFamily Family(string file)
        {
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return new FontFamily("Arial");
            if (fams.TryGetValue(file, out var f)) return f;
            try
            {
                var gt = new GlyphTypeface(new Uri(file)); var name = gt.Win32FamilyNames.Values.FirstOrDefault() ?? "Arial";
                return fams[file] = file.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), StringComparison.OrdinalIgnoreCase) ? new FontFamily(name) : new FontFamily(new Uri(System.IO.Path.GetDirectoryName(file) + "\\"), "./#" + name);
            }
            catch { return fams[file] = new FontFamily("Arial"); }
        }
    }
}

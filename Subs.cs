using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace MiniCut
{
    // Subtitles without AI: import an .srt/.vtt, paste a script (timed by reading speed) or export the text clips as .srt
    public partial class MainWindow
    {
        static readonly Regex SrtTime = new(@"(?:(\d+):)?(\d+):(\d+)[,.](\d+)\s*-->\s*(?:(\d+):)?(\d+):(\d+)[,.](\d+)", RegexOptions.Compiled);
        static double Sec(string h, string m, string s, string ms) => (h == "" ? 0 : int.Parse(h)) * 3600 + int.Parse(m) * 60 + int.Parse(s) + int.Parse(ms.PadRight(3, '0')[..3]) / 1000.0;

        // word-wraps to what fits the canvas (the export's drawtext does not wrap by itself)
        string Wrap(string t, int size)
        {
            var max = Math.Max(12, (int)(Opts.Size(proj.Canvas).w * 0.85 / (size * 0.5))); var sb = new StringBuilder(); int len = 0;
            foreach (var w in t.Split(' ', StringSplitOptions.RemoveEmptyEntries)) { if (len > 0 && len + 1 + w.Length > max) { sb.Append('\n'); len = 0; } else if (len > 0) { sb.Append(' '); len++; } sb.Append(w); len += w.Length; }
            return sb.ToString();
        }

        void AddSubs(List<(double start, double dur, string text)> subs)
        {
            if (subs.Count == 0) { Busy.Text = "No hay subtítulos que añadir"; return; }
            Push(); var (_, H) = Opts.Size(proj.Canvas); int size = H / 16; Seg first = null;
            foreach (var (st, du, tx) in subs) { var sg = new Seg { Kind = "text", Text = Wrap(tx, size), TextSize = size, TextColor = "white", Track = 0, Start = st, Dur = Math.Max(.3, du), Y = H * 0.36 }; proj.Segs.Add(sg); first ??= sg; }
            Select(first); Dirty(); Busy.Text = subs.Count + " subtítulos añadidos";
        }

        void ImportSubs()
        {
            var dlg = new OpenFileDialog { Filter = "Subtítulos|*.srt;*.vtt" }; if (dlg.ShowDialog() != true) return;
            var subs = new List<(double, double, string)>();
            foreach (var blk in Regex.Split(File.ReadAllText(dlg.FileName).Replace("\r", ""), @"\n\s*\n"))
            {
                var lines = blk.Split('\n'); int i = Array.FindIndex(lines, l => SrtTime.IsMatch(l)); if (i < 0) continue;
                var m = SrtTime.Match(lines[i]); var tx = Regex.Replace(string.Join(" ", lines.Skip(i + 1)), "<[^>]+>", "").Trim(); if (tx == "") continue;
                double a = Sec(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value), b = Sec(m.Groups[5].Value, m.Groups[6].Value, m.Groups[7].Value, m.Groups[8].Value);
                subs.Add((a, b - a, tx));
            }
            AddSubs(subs);
        }

        // one subtitle per line, back to back from the cursor; each lasts as long as it takes to read it (15 chars/s, min 1.5 s)
        void ScriptSubs()
        {
            var w = new Window { Title = "Pegar guion (una línea = un subtítulo)", Width = 520, Height = 380, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Br("#0f171b"), Foreground = Br("#e9e9ee") };
            w.Resources.MergedDictionaries.Add(Resources);
            var tb = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(16, 16, 16, 8) };
            var ok = new Button { Content = "Añadir en el cursor", Style = (Style)FindResource("Accent"), Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(0, 0, 16, 12), HorizontalAlignment = HorizontalAlignment.Right };
            string res = null; ok.Click += (_, _) => { res = tb.Text; w.Close(); };
            var dp = new DockPanel(); DockPanel.SetDock(ok, Dock.Bottom); dp.Children.Add(ok); dp.Children.Add(tb); w.Content = dp; w.Loaded += (_, _) => tb.Focus(); w.ShowDialog();
            if (res == null) return;
            double t = P; var subs = new List<(double, double, string)>();
            foreach (var l in res.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l != "")) { var d = Math.Max(1.5, l.Length / 15.0); subs.Add((t, d, l)); t += d; }
            AddSubs(subs);
        }

        void ExportSubs()
        {
            var texts = proj.Segs.Where(s => s.Kind == "text").OrderBy(s => s.Start).ToList(); if (texts.Count == 0) { Busy.Text = "No hay clips de texto"; return; }
            var dlg = new SaveFileDialog { Filter = "SubRip|*.srt", FileName = proj.Name + ".srt" }; if (dlg.ShowDialog() != true) return;
            static string Ts(double s) => TimeSpan.FromSeconds(s).ToString(@"hh\:mm\:ss\,fff");
            File.WriteAllText(dlg.FileName, string.Concat(texts.Select((s, i) => $"{i + 1}\n{Ts(s.Start)} --> {Ts(s.End)}\n{s.Text}\n\n")), new UTF8Encoding(false)); Busy.Text = "SRT exportado";
        }
    }
}

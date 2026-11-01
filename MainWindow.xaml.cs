using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rectangle = System.Windows.Shapes.Rectangle;
using System.Windows.Threading;
using Microsoft.Win32;

namespace MiniCut
{
    public class Media { public string Path, Kind; public double Dur; public bool HasAudio; public string Thumb, Strip; public int StripN; public double StripStep, TileW; public byte[] Peaks; public string Raw; }
    class RowUi { public Slider S; public TextBlock L; public Button K; public string Label, Fmt; }

    public partial class MainWindow : Window
    {
        static readonly string[] TrackNames = { "Texto", "Vídeo 2", "Vídeo 1", "Audio 1", "Audio 2" };
        const double RULER = 26;
        static readonly double[] TrackH = { 36, 64, 64, 48, 48 };
        static double TrackTop(int r) { double y = RULER; for (int i = 0; i < r; i++) y += TrackH[i]; return y; }
        static int RowAt(double y) { double t = RULER; for (int i = 0; i < TrackH.Length; i++) { t += TrackH[i]; if (y < t) return i; } return TrackH.Length - 1; }
        readonly AudioEngine audio = new();
        readonly Canvas Lower = new() { IsHitTestVisible = false }; VisualBrush lowerBrush; // Vídeo 1 layers; blend modes on Vídeo 2 read this as their backdrop
        readonly Dictionary<Seg, FrameworkElement> blocks = new();
        readonly Dictionary<(string, int), ImageBrush> cellBrushes = new();
        Media dragging; Rectangle ghost; bool monitorPlaying;
        static readonly string Tmp = Ff.Cache;
        static readonly string[] Tabs_ = { "Medios", "Audio", "Texto", "Stickers", "Efectos", "Transic.", "Subtít.", "Filtros", "Ajuste" };
        static readonly string[] TabIcons = { "▣", "♪", "T", "☺", "✦", "⋈", "▤", "◐", "☼" };
        // sidebar categories per tab (like CapCut's Import / Media / Yours… list)
        static readonly string[][] Cats = {
            new[] { "Todos", "Vídeos", "Imágenes" }, new[] { "Sonidos", "Música", "Mis audios" }, new[] { "Básico", "Colores", "Títulos" },
            new[] { "Formas", "Símbolos", "Flechas" }, Opts.EffectCats, new[] { "Básicas", "Deslizar", "Zoom", "Fundidos" },
            new[] { "Subtítulos" }, Opts.FilterCats, new[] { "Preajustes" } };

        Project proj = new();
        readonly List<Media> media = new();
        readonly Stack<string> undo = new(), redo = new();
        readonly Dictionary<string, RowUi> rows = new();
        readonly Dictionary<string, FrameworkElement> secs = new();
        readonly DispatcherTimer playTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        Seg sel; double P, Px = 50; int tab, cat; bool updating, playing; string lastKey, projFile;
        DateTime lastT; Rectangle head; TextBox tbText;

        static void Log(string m) { try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "minicut.log"), DateTime.Now.ToString("HH:mm:ss.fff ") + m + "\n"); } catch { } }
        double Total => proj.Segs.Count == 0 ? 0 : proj.Segs.Max(s => s.End);
        double Rel => sel == null ? 0 : Math.Clamp(P - sel.Start, 0, sel.Dur);
        static string T(double s) => TimeSpan.FromSeconds(Math.Max(0, s)).ToString(@"m\:ss\.f");
        static string Tcf(double t) { t = Math.Max(0, t); return $"{(int)(t / 3600):00}:{(int)(t % 3600 / 60):00}:{(int)(t % 60):00}:{(int)(t % 1 * 30):00}"; }
        string Tc() => Tcf(P) + " / " + Tcf(Total);

        public MainWindow()
        {
            InitializeComponent(); Stage.Children.Add(Lower); Directory.CreateDirectory(Tmp);
            foreach (var old in Directory.GetFiles(Tmp, "p*_*.mp4").Concat(Directory.GetFiles(Tmp, "frame_*.png"))) try { File.Delete(old); } catch { }
            playTimer.Tick += (_, _) => Tick(); audio.Init();
            TL.AllowDrop = true; TL.DragOver += TlDragOver; TL.DragLeave += (_, _) => { if (ghost != null) ghost.Visibility = Visibility.Collapsed; }; TL.Drop += TlDrop;
            BuildDetails(); CanvasStyle(); ShowTab(0); RefreshDetails(); Redraw(); Sync(true); InitHome();
            Log("Grade shader ok=" + Grade.Ok + " " + Grade.Error);
            // dev hook: MINICUT_AUTOPLAY=<project.minicut> loads it and starts playback
            Loaded += async (_, _) => { var f = Environment.GetEnvironmentVariable("MINICUT_AUTOPLAY"); if (f == null) return; proj = JsonSerializer.Deserialize<Project>(File.ReadAllText(f)); await ProbeAll(proj.Segs.Where(x => x.Kind != "text").Select(x => x.Path).Distinct().ToList()); HideHome(); ShowTab(0); Redraw(); if (Environment.GetEnvironmentVariable("MINICUT_SELECT") != null) Select(proj.Segs[0]); Play();
            };
        }

        static Brush Br(string hex) => (Brush)new BrushConverter().ConvertFromString(hex);
        static readonly Dictionary<string, ImageSource> thumbs = new();
        static ImageSource Thumb(string path) { if (!thumbs.TryGetValue(path, out var i)) thumbs[path] = i = Img(path); return i; }
        static ImageSource Img(string path)
        {
            var b = new BitmapImage(); b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.CreateOptions = BitmapCreateOptions.IgnoreImageCache; b.UriSource = new Uri(path); b.EndInit(); b.Freeze(); return b;
        }
        void CanvasStyle() { var (sw, sh) = Opts.Size(proj.Canvas); Stage.Width = sw; Stage.Height = sh; Lower.Width = sw; Lower.Height = sh; lowerBrush = new VisualBrush(Lower) { Viewbox = new Rect(0, 0, sw, sh), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill }; foreach (var k in layers.Keys.ToList()) DropLayer(k); AspectBtn.Content = proj.Canvas + " ▾"; DrawGrid(sw, sh); }

        // ---------- player controls: rule-of-thirds grid, quick aspect ratio, fullscreen ----------
        void DrawGrid(int w, int h)
        {
            GridCv.Width = w; GridCv.Height = h; GridCv.Children.Clear();
            for (int i = 1; i <= 2; i++)
            {
                GridCv.Children.Add(new System.Windows.Shapes.Line { X1 = w * i / 3.0, X2 = w * i / 3.0, Y1 = 0, Y2 = h, Stroke = Br("#99ffffff"), StrokeThickness = 2 });
                GridCv.Children.Add(new System.Windows.Shapes.Line { X1 = 0, X2 = w, Y1 = h * i / 3.0, Y2 = h * i / 3.0, Stroke = Br("#99ffffff"), StrokeThickness = 2 });
            }
        }
        void ToggleGrid(object s, RoutedEventArgs e) { GridOv.Visibility = GridOv.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; GridBtn.Foreground = Br(GridOv.Visibility == Visibility.Visible ? "#25f4ee" : "#e9e9ee"); }
        void AspectMenu(object s, RoutedEventArgs e)
        {
            var m = new ContextMenu { PlacementTarget = AspectBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
            foreach (var c in Opts.Canvases) { var cc = c; var i = new MenuItem { Header = c, IsChecked = c == proj.Canvas }; i.Click += (_, _) => { Push(); proj.Canvas = cc; CanvasStyle(); Dirty(); }; m.Items.Add(i); }
            m.IsOpen = true;
        }
        bool full;
        void ToggleFull(object s = null, RoutedEventArgs e = null)
        {
            full = !full; var v = full ? Visibility.Collapsed : Visibility.Visible;
            foreach (UIElement c in Editor.Children) if (Grid.GetRow(c) != 1) c.Visibility = v;
            foreach (UIElement c in Mid.Children) if (Grid.GetColumn(c) != 1) c.Visibility = v;
            WindowStyle = full ? WindowStyle.None : WindowStyle.SingleBorderWindow; WindowState = full ? WindowState.Maximized : WindowState.Normal;
        }

        // ---------- undo ----------
        string Json() => JsonSerializer.Serialize(proj);
        void Push() { undo.Push(Json()); redo.Clear(); Touch(); }
        void Commit(string key) { if (key != lastKey || (DateTime.Now - lastT).TotalSeconds > 1) Push(); lastKey = key; lastT = DateTime.Now; }
        void Restore(string json) { int i = sel == null ? -1 : proj.Segs.IndexOf(sel); proj = JsonSerializer.Deserialize<Project>(json); sel = i >= 0 && i < proj.Segs.Count ? proj.Segs[i] : null; CanvasStyle(); RefreshDetails(); Dirty(); }
        void Undo(object s, RoutedEventArgs e) { if (undo.Count == 0) return; redo.Push(Json()); Restore(undo.Pop()); }
        void Redo(object s, RoutedEventArgs e) { if (redo.Count == 0) return; undo.Push(Json()); Restore(redo.Pop()); }

        // ---------- media ----------
        // runs off the UI thread; callers add the result to `media`
        static Media Probe(string f)
        {
            var ext = Path.GetExtension(f).ToLower();
            var kind = Array.IndexOf(new[] { ".png", ".jpg", ".jpeg", ".bmp", ".webp" }, ext) >= 0 ? "image" : Array.IndexOf(new[] { ".mp3", ".wav", ".m4a", ".aac", ".ogg", ".flac" }, ext) >= 0 ? "audio" : "video";
            double dur = 5; bool aud = false;
            if (kind != "image")
            {
                var lines = Ff.Exec("ffprobe", $"-v error -show_entries format=duration:stream=codec_type -of csv=p=0 \"{f}\"").Split('\n').Select(l => l.Trim()).ToList();
                aud = kind == "video" && lines.Contains("audio");
                foreach (var l in lines) if (double.TryParse(l, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) dur = v;
            }
            string thumb = null;
            if (kind != "audio")
            {
                thumb = Ff.Thumb(f);
                if (!File.Exists(thumb)) Ff.Exec("ffmpeg", $"-y -v error -ss {(dur > 1 ? "0.5" : "0")} -i \"{f}\" -frames:v 1 -vf scale=-2:90 \"{thumb}\"");
                if (!File.Exists(thumb)) thumb = null;
            }
            return new Media { Path = f, Kind = kind, Dur = Math.Round(dur, 2), HasAudio = aud, Thumb = thumb };
        }

        async Task ProbeAll(IEnumerable<string> files)
        {
            var todo = files.Where(f => media.All(m => m.Path != f)).ToList(); if (todo.Count == 0) return;
            Busy.Text = "Importando…";
            var res = await Task.WhenAll(todo.Select(f => Task.Run(() => Probe(f))));
            media.AddRange(res); Busy.Text = "";
            _ = Visuals(res.ToList());
        }

        // filmstrip + waveform, generated in the background; the timeline redraws as each media finishes
        async Task Visuals(List<Media> ms)
        {
            foreach (var m in ms)
            {
                await Task.Run(() =>
                {
                    if (m.Kind == "video") { var (n, step) = Ff.MakeStrip(m.Path, m.Dur); if (File.Exists(Ff.Strip(m.Path))) { m.StripN = n; m.StripStep = step; m.Strip = Ff.Strip(m.Path); } }
                    if ((m.Kind == "audio" || m.HasAudio) && Ff.MakeRaw(m.Path)) { m.Raw = Ff.Raw(m.Path); m.Peaks = Ff.Peaks(m.Raw); }
                });
                if (m.Strip != null) m.TileW = ((BitmapSource)Thumb(m.Strip)).PixelWidth / (double)m.StripN;
                Redraw(); RebuildAudio();
            }
        }

        async void Import(object s = null, RoutedEventArgs e = null)
        {
            var dlg = new OpenFileDialog { Multiselect = true, Filter = "Medios|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.mp3;*.wav;*.m4a;*.aac;*.ogg;*.flac" };
            if (dlg.ShowDialog() != true) return;
            await ProbeAll(dlg.FileNames); ShowTab(tab);
        }
        double TrackEnd(int tr) => proj.Segs.Where(x => x.Track == tr).Select(x => x.End).DefaultIfEmpty(0).Max();

        void AddMediaAt(Media m, double start, int row)
        {
            Push(); CloseMonitor(null, null);
            if (!Compatible(m.Kind, row)) row = m.Kind == "audio" ? 3 : 2;
            var sg = new Seg { Kind = m.Kind, Path = m.Path, HasAudio = m.HasAudio, SrcLen = m.Dur, Dur = m.Dur, Track = row, Start = Math.Max(0, start) };
            proj.Segs.Add(sg); Select(sg); Dirty();
        }

        // ---------- source monitor: click on a media = preview only; dragging it to the timeline is what edits ----------
        void PreviewMedia(Media m)
        {
            if (playing) StopPlay();
            Monitor.Visibility = Visibility.Visible; MonitorImg.Source = null; MonitorPlayer.Source = null; monitorPlaying = false;
            MonitorLbl.Text = (m.Kind == "audio" ? "♪ " : "") + "Vista previa · " + Path.GetFileName(m.Path) + "   — arrástralo a la línea de tiempo para editarlo";
            if (m.Kind == "image") MonitorImg.Source = Img(m.Path);
            else { MonitorPlayer.Source = new Uri(m.Path); MonitorPlayer.Play(); monitorPlaying = true; }
        }
        void CloseMonitor(object s, RoutedEventArgs e)
        {
            if (Monitor.Visibility != Visibility.Visible) return;
            MonitorPlayer.Stop(); MonitorPlayer.Source = null; MonitorImg.Source = null; Monitor.Visibility = Visibility.Collapsed; monitorPlaying = false;
        }
        void ToggleMonitor() { if (monitorPlaying) MonitorPlayer.Pause(); else MonitorPlayer.Play(); monitorPlaying = !monitorPlaying; }

        // ---------- drag from media pool to the timeline ----------
        void TlDragOver(object s, DragEventArgs e)
        {
            if (dragging == null) return;
            e.Effects = DragDropEffects.Copy; e.Handled = true;
            var p = e.GetPosition(TL); int row = RowAt(p.Y); if (!Compatible(dragging.Kind, row)) row = dragging.Kind == "audio" ? 3 : 2;
            if (ghost == null) ghost = new Rectangle { Fill = Br("#5525f4ee"), Stroke = Br("#25f4ee"), StrokeThickness = 1, RadiusX = 5, RadiusY = 5, IsHitTestVisible = false };
            if (!TL.Children.Contains(ghost)) TL.Children.Add(ghost);
            ghost.Visibility = Visibility.Visible; ghost.Width = Math.Max(12, dragging.Dur * Px); ghost.Height = TrackH[row] - 6;
            Canvas.SetLeft(ghost, Math.Max(0, p.X)); Canvas.SetTop(ghost, TrackTop(row) + 2);
        }
        void TlDrop(object s, DragEventArgs e)
        {
            if (ghost != null) ghost.Visibility = Visibility.Collapsed;
            if (dragging == null) return;
            var p = e.GetPosition(TL); var m = dragging; dragging = null;
            AddMediaAt(m, p.X / Px, RowAt(p.Y)); e.Handled = true;
        }
        void AddText(string txt, int size, string color)
        {
            Push(); var (_, H) = Opts.Size(proj.Canvas);
            var sg = new Seg { Kind = "text", Text = txt, TextSize = size, TextColor = color, Track = 0, Start = P, Dur = 3, Y = H * 0.3 };
            proj.Segs.Add(sg); Select(sg); Dirty();
        }

        // ---------- left panel ----------
        static string SfxDir => Path.Combine(Ff.Cache, "sfx");
        bool IsLib(Media m) => m.Path.StartsWith(SfxDir, StringComparison.OrdinalIgnoreCase);
        bool sfxBusy;
        string PreviewThumb() => (sel != null ? media.FirstOrDefault(m => m.Path == sel.Path)?.Thumb : null) ?? media.FirstOrDefault(m => m.Kind != "audio" && m.Thumb != null)?.Thumb;
        static System.Windows.Media.Effects.Effect Prev(int look, int fx) { if (!Grade.Ok) return null; var g = new Grade(); g.Set(0, 1, 1, look, fx, 142, 80, 2.3); return g; }

        // built-in sounds are synthesized once with ffmpeg (original, license-free) and added to the media pool
        async void EnsureSfx()
        {
            if (sfxBusy) return; sfxBusy = true;
            var files = await Task.Run(() =>
            {
                Directory.CreateDirectory(SfxDir); var l = new List<string>();
                foreach (var d in Opts.Sounds)
                {
                    var f = Path.Combine(SfxDir, d.Name + ".wav");
                    if (!File.Exists(f)) Ff.Exec("ffmpeg", $"-y -v error -nostdin -f lavfi -i \"{d.Src}\" " + (d.Af != "" ? $"-af \"{d.Af}\" " : "") + $"-ac 2 -ar 44100 \"{f}\"");
                    if (File.Exists(f)) l.Add(f);
                }
                return l;
            });
            await ProbeAll(files); if (tab == 1) ShowTab(1);
        }

        void ShowTab(int i, int c = -1)
        {
            if (c < 0) c = i == tab ? cat : 0;
            tab = i; cat = Math.Min(c, Cats[i].Length - 1); Tabs.Children.Clear(); LeftBody.Children.Clear(); Side.Children.Clear();
            for (int k = 0; k < Tabs_.Length; k++)
            {
                int kk = k; bool on = k == i;
                var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                content.Children.Add(new TextBlock { Text = TabIcons[k], FontSize = 17, HorizontalAlignment = HorizontalAlignment.Center, FontFamily = new FontFamily("Segoe UI Symbol") });
                content.Children.Add(new TextBlock { Text = Tabs_[k], FontSize = 10, Margin = new Thickness(0, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
                var b = new Button { Content = content, Padding = new Thickness(0, 7, 0, 7), Margin = new Thickness(1, 0, 1, 0), Background = Br(on ? "#1c2a30" : "transparent"), Foreground = Br(on ? "#25f4ee" : "#aab6bb") };
                b.Click += (_, _) => ShowTab(kk); Tabs.Children.Add(b);
            }
            for (int k = 0; k < Cats[i].Length; k++)
            {
                int kk = k; bool on = k == cat;
                var b = new Button { Content = Cats[i][k], HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 3), FontSize = 12.5, Background = Br(on ? "#1c2a30" : "transparent"), Foreground = Br(on ? "#25f4ee" : "#aab6bb") };
                b.Click += (_, _) => ShowTab(i, kk); Side.Children.Add(b);
            }
            void Add(Button b) => LeftBody.Children.Add(b);
            var cn = Cats[i][cat]; var pt = PreviewThumb();
            switch (i)
            {
                case 0:
                    Add(Card("＋ Importar", "", null, "#1c282d", () => Import()));
                    foreach (var m in media.Where(m => m.Kind != "audio" && (cat == 0 || (cat == 1) == (m.Kind == "video")))) { var mm = m; Add(Card(Path.GetFileName(m.Path), m.Kind == "image" ? "imagen" : T(m.Dur), m.Thumb, "#142026", () => PreviewMedia(mm), mm)); }
                    break;
                case 1:
                    if (cat == 2) Add(Card("＋ Audio", "", null, "#1c282d", () => Import())); else EnsureSfx();
                    foreach (var m in media.Where(m => m.Kind == "audio" && (cat == 2 ? !IsLib(m) : IsLib(m) && Opts.Sounds.Any(d => d.Name == Path.GetFileNameWithoutExtension(m.Path) && d.Cat == cn))))
                    { var mm = m; Add(Card(Path.GetFileNameWithoutExtension(m.Path), T(m.Dur), null, "#142026", () => PreviewMedia(mm), mm, glyph: "♪", gcol: "#7ee2a8")); }
                    break;
                case 2:
                    var texts = cat switch
                    {
                        0 => new[] { ("Texto", 64, "white"), ("Título", 110, "yellow"), ("Subtítulo", 40, "white"), ("Pie de foto", 32, "#cfd8dc"), ("¡Atención!", 80, "red"), ("Nuevo", 72, "#25f4ee") },
                        1 => new[] { ("Amarillo", 72, "#ffd60a"), ("Rojo", 72, "#ff3b30"), ("Cian", 72, "#25f4ee"), ("Verde", 72, "#30d158"), ("Naranja", 72, "#ff9f0a"), ("Rosa", 72, "#ff6fb5"), ("Violeta", 72, "#bf5af2"), ("Azul", 72, "#0a84ff") },
                        _ => new[] { ("GRAN TÍTULO", 150, "white"), ("Capítulo 1", 90, "#ffd60a"), ("“Una cita”", 56, "#e5e5ea"), ("OFERTA", 110, "#ff3b30"), ("Tu nombre", 48, "white"), ("Gracias por ver", 70, "#30d158"), ("Fin", 120, "white"), ("Suscríbete", 64, "#ff3b30") },
                    };
                    foreach (var (n, sz, col) in texts) Add(Card(n, "texto", null, "#2b2a2f", () => AddText(n, sz, col), glyph: "Aa", gcol: col));
                    break;
                case 3:
                    // only glyphs Arial has, because the export draws text with arial.ttf
                    foreach (var ch in cat switch { 0 => "■□▪▫▬▲►▼◄○●◘◙", 1 => "☺☻☼♀♂♠♣♥♦♪♫∞≈≠≤≥√Ωπ°±½€£©®™", _ => "←↑→↓↔↕↨" }) { var cs = ch.ToString(); Add(Card(cs, "sticker", null, "#2a2a30", () => AddText(cs, 160, "white"), glyph: cs)); }
                    break;
                case 4:
                    foreach (var n in Opts.Effects.Keys.Where(n => n == "Ninguno" ? cat == 0 : Opts.EffectCat[n] == cn)) { var nn = n; int ix = Opts.Effects.Keys.ToList().IndexOf(n); Add(Card(n, "efecto", pt, Hue(n), () => Edit(sg => sg.Effect = nn, true), fx: pt == null ? null : () => Prev(0, ix))); }
                    break;
                case 5:
                    if (cat < 3)
                        foreach (var t in Opts.Transitions.Where(t => t.Cat == cn))
                        {
                            var tn = t.Name;
                            Add(Card(tn, tn == "Ninguna" ? "sin transición" : "al inicio del clip", null, "#1f2f3a", () => { Edit(sg => sg.Trans = tn, true); if (sel != null && sel.Trans != "Ninguna") { Seek(Math.Max(0, sel.Start - .4)); if (!playing) Play(); } }, glyph: t.Glyph, gcol: "#25f4ee"));
                        }
                    else
                        foreach (var (n, a, b) in new[] { ("Entrada 0.5s", 0.5, -1.0), ("Entrada 1s", 1.0, -1.0), ("Salida 0.5s", -1.0, 0.5), ("Salida 1s", -1.0, 1.0), ("Entrada y salida", 1.0, 1.0), ("Quitar", 0.0, 0.0) })
                        { var aa = a; var bb = b; Add(Card(n, "fundido", null, "#1f2f3a", () => Edit(sg => { if (aa >= 0) sg.FadeIn = aa; if (bb >= 0) sg.FadeOut = bb; }, false), glyph: "◐", gcol: "#25f4ee")); }
                    break;
                case 6:
                    Add(Card("＋ Importar SRT", "subtítulos", null, "#1c282d", ImportSubs));
                    Add(Card("＋ Pegar guion", "una línea = un subtítulo", null, "#1c282d", ScriptSubs));
                    Add(Card("Exportar SRT", "clips de texto", null, "#1c282d", ExportSubs, glyph: "⤓", gcol: "#25f4ee"));
                    break;
                case 7:
                    foreach (var n in Opts.Filters.Keys.Where(n => cat == 0 || Opts.FilterCat.GetValueOrDefault(n) == cn)) { var nn = n; int ix = Opts.Filters.Keys.ToList().IndexOf(n); Add(Card(n, "filtro", pt, Hue(n), () => Edit(sg => sg.Filter = nn, true), fx: pt == null ? null : () => Prev(ix, 0))); }
                    break;
                case 8:
                    foreach (var (n, br, co, sa) in new[] { ("Brillante", .1, 1.1, 1.2), ("Contraste", 0, 1.4, 1.0), ("Apagado", -.05, .9, .6), ("Luminoso", .15, 1.0, 1.1), ("Oscuro", -.1, 1.1, 1.0), ("Desaturado", 0, 1.0, .4), ("Saturado", 0, 1.0, 1.6), ("Alto contraste", 0, 1.3, 1.1), ("Restablecer", 0, 1.0, 1.0) })
                    { var b1 = br; var c1 = co; var s1 = sa; Add(Card(n, "ajuste", null, "#2a2a30", () => Edit(sg => { sg.Brightness = b1; sg.Contrast = c1; sg.Saturation = s1; }, true), glyph: "☼", gcol: "#ffd60a")); }
                    break;
            }
        }

        static string Hue(string n) { var h = Math.Abs(n.GetHashCode()) % 360; var c = new HslToRgb(h); return c.Hex; }
        record HslToRgb(int H) { public string Hex { get { double s = .35, l = .25, a = s * Math.Min(l, 1 - l); double f(int nn) { var k = (nn + H / 30.0) % 12; return l - a * Math.Max(-1, Math.Min(Math.Min(k - 3, 9 - k), 1)); } return $"#{(int)(f(0) * 255):x2}{(int)(f(8) * 255):x2}{(int)(f(4) * 255):x2}"; } } }

        Button Card(string title, string sub, string thumb, string bg, Action click, Media drag = null, Func<System.Windows.Media.Effects.Effect> fx = null, string glyph = null, string gcol = "#e9e9ee")
        {
            var st = new StackPanel { Width = 108 };
            var box = new Border { Height = 66, CornerRadius = new CornerRadius(4), Background = Br(bg), ClipToBounds = true };
            if (thumb != null) { var im = new Image { Source = Thumb(thumb), Stretch = Stretch.UniformToFill }; if (fx != null) im.Effect = fx(); box.Child = im; }
            else if (glyph != null) box.Child = new TextBlock { Text = glyph, FontSize = glyph.Length > 1 ? 24 : 30, Foreground = Br(gcol), FontFamily = new FontFamily("Segoe UI Symbol"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            else if (title.StartsWith("＋")) box.Child = new TextBlock { Text = "＋", FontSize = 30, Foreground = Br("#25f4ee"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            st.Children.Add(box);
            st.Children.Add(new TextBlock { Text = title, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) });
            st.Children.Add(new TextBlock { Text = sub, Style = (Style)FindResource("Lbl") });
            var b = new Button { Content = st, Padding = new Thickness(4), Margin = new Thickness(0, 0, 6, 6), Background = Br("transparent") }; b.Click += (_, _) => click();
            if (drag != null)
            {
                Point? down = null; b.ToolTip = "Clic: vista previa · Arrastra a la línea de tiempo para editar";
                b.PreviewMouseLeftButtonDown += (_, e) => down = e.GetPosition(b);
                b.PreviewMouseLeftButtonUp += (_, _) => down = null;
                b.PreviewMouseMove += (_, e) =>
                {
                    if (down == null || e.LeftButton != MouseButtonState.Pressed) return;
                    var p = e.GetPosition(b); if (Math.Abs(p.X - down.Value.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - down.Value.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                    down = null; dragging = drag; DragDrop.DoDragDrop(b, new DataObject("minicut-media", drag.Path), DragDropEffects.Copy); dragging = null; if (ghost != null) ghost.Visibility = Visibility.Collapsed;
                };
            }
            return b;
        }

        // applies a change to the selected clip
        void Edit(Action<Seg> a, bool visualOnly)
        {
            if (sel == null || (visualOnly && sel.Kind is "audio" or "text")) { Busy.Text = "Selecciona un clip de vídeo/imagen"; return; }
            Push(); a(sel); RefreshDetails(); Dirty();
        }

        // ---------- details panel ----------
        void Section(string key, string title, params string[] _) { var p = new StackPanel { Margin = new Thickness(0, 6, 0, 8) }; p.Children.Add(new TextBlock { Text = title.ToUpper(), Style = (Style)FindResource("Lbl"), Margin = new Thickness(0, 6, 0, 0), Foreground = Br("#25f4ee") }); secs[key] = p; Details.Children.Add(p); }
        void Row(string sec, string key, string label, double min, double max, bool kf = false, string fmt = "0.##")
        {
            var g = new Grid(); g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Style = (Style)FindResource("Lbl"), Margin = new Thickness(0, 6, 0, 0) }; Grid.SetColumnSpan(l, 2);
            var s = new Slider { Minimum = min, Maximum = max }; s.ValueChanged += (_, e) => SliderChanged(key, e.NewValue); Grid.SetRow(s, 1);
            g.Children.Add(l); g.Children.Add(s);
            Button kb = null;
            if (kf) { kb = new Button { Content = "◇", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(6, 0, 0, 0), Background = Br("transparent"), Foreground = Br("#25f4ee"), ToolTip = "Keyframe en el cursor" }; kb.Click += (_, _) => ToggleKf(key); Grid.SetRow(kb, 1); Grid.SetColumn(kb, 1); g.Children.Add(kb); }
            ((Panel)secs[sec]).Children.Add(g); rows[key] = new RowUi { S = s, L = l, K = kb, Label = label, Fmt = fmt };
        }

        WrapPanel blendPanel, maskPanel; TextBlock blendHint;
        void BuildDetails()
        {
            Section("text", "Texto");
            tbText = new TextBox { AcceptsReturn = false, Margin = new Thickness(0, 6, 0, 6) };
            tbText.TextChanged += (_, _) => { if (updating || sel == null) return; Commit("text"); sel.Text = tbText.Text; Dirty(); };
            ((Panel)secs["text"]).Children.Add(tbText);
            var cols = new WrapPanel();
            foreach (var c in new[] { "white", "yellow", "red", "cyan", "lime", "black" })
            { var cc = c; var b = new Button { Width = 24, Height = 24, Padding = new Thickness(0), Background = Br(c == "lime" ? "#00ff00" : c) }; b.Click += (_, _) => { if (sel == null) return; Push(); sel.TextColor = cc; Dirty(); }; cols.Children.Add(b); }
            ((Panel)secs["text"]).Children.Add(cols);
            Row("text", "textsize", "Tamaño", 16, 200, false, "0");
            Section("xform", "Posición"); Row("xform", "x", "Posición X", -1000, 1000, true, "0"); Row("xform", "y", "Posición Y", -1000, 1000, true, "0");
            Section("xform2", "Transformar"); Row("xform2", "scale", "Escala", 0.1, 4, true); Row("xform2", "rotation", "Rotación °", -180, 180, true, "0"); Row("xform2", "opacity", "Opacidad", 0, 1, false);
            var fl = new CheckBox { Content = "Espejo horizontal", Foreground = Br("#e9e9ee"), Margin = new Thickness(0, 8, 0, 0) };
            fl.Click += (_, _) => { if (sel == null) return; Push(); sel.Flip = fl.IsChecked == true; Dirty(); }; ((Panel)secs["xform2"]).Children.Add(fl); secs["xform2"].Tag = fl;
            Section("adjust", "Ajuste"); Row("adjust", "brightness", "Brillo", -.5, .5, false); Row("adjust", "contrast", "Contraste", 0, 2, false); Row("adjust", "saturation", "Saturación", 0, 3, false);
            var info = new TextBlock { Style = (Style)FindResource("Lbl"), Margin = new Thickness(0, 8, 0, 0), Name = "FxInfo" }; ((Panel)secs["adjust"]).Children.Add(info); secs["adjust"].Tag = info;
            Section("blend", "Mezcla"); blendPanel = new WrapPanel(); ((Panel)secs["blend"]).Children.Add(blendPanel);
            foreach (var b in Opts.Blends) { var bn = b.Name; var bt = new Button { Content = bn, Tag = bn, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 4, 4, 0), FontSize = 11.5 }; bt.Click += (_, _) => { if (sel?.Track == 2) { Busy.Text = "La mezcla funciona en clips de Vídeo 2 (sobre Vídeo 1)"; return; } Edit(sg => sg.Blend = bn, true); }; blendPanel.Children.Add(bt); }
            blendHint = new TextBlock { Style = (Style)FindResource("Lbl"), Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, Text = "Combina este clip con el que hay debajo usando las fórmulas de CapCut. Colócalo en Vídeo 2 sobre un clip de Vídeo 1." };
            ((Panel)secs["blend"]).Children.Add(blendHint);
            Section("mask", "Máscara"); maskPanel = new WrapPanel(); ((Panel)secs["mask"]).Children.Add(maskPanel);
            foreach (var mn in Opts.Masks) { var m1 = mn; var mt = new Button { Content = mn, Tag = mn, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 4, 4, 0), FontSize = 11.5 }; mt.Click += (_, _) => Edit(sg => sg.Mask = m1, true); maskPanel.Children.Add(mt); }
            Row("mask", "maskw", "Ancho", 0.05, 2); Row("mask", "maskh", "Alto", 0.05, 2); Row("mask", "maskrot", "Rotación °", -180, 180, false, "0"); Row("mask", "maskfeather", "Difuminado", 0, 1); Row("mask", "maskround", "Esquinas redondeadas", 0, 1);
            var inv = new CheckBox { Content = "Invertir máscara", Foreground = Br("#e9e9ee"), Margin = new Thickness(0, 8, 0, 0) };
            inv.Click += (_, _) => { if (sel == null) return; Push(); sel.MaskInvert = inv.IsChecked == true; Dirty(); }; ((Panel)secs["mask"]).Children.Add(inv); secs["mask"].Tag = inv;
            Section("speed", "Velocidad"); Row("speed", "speed", "Velocidad ×", 0.25, 4);
            Section("vol", "Volumen"); Row("vol", "volume", "Volumen", 0, 2, true);
            Section("fade", "Fundidos"); Row("fade", "fadein", "Entrada (s)", 0, 5, false, "0.0"); Row("fade", "fadeout", "Salida (s)", 0, 5, false, "0.0");
        }

        double ReadStatic(string k) => k switch
        {
            "textsize" => sel.TextSize, "opacity" => sel.Opacity, "brightness" => sel.Brightness, "contrast" => sel.Contrast, "saturation" => sel.Saturation,
            "speed" => sel.Speed, "fadein" => sel.FadeIn, "fadeout" => sel.FadeOut, "maskw" => sel.MaskW, "maskh" => sel.MaskH, "maskrot" => sel.MaskRot, "maskfeather" => sel.MaskFeather, "maskround" => sel.MaskRound, _ => sel.Get(k, Rel)
        };

        // like CapCut's property tabs: only the pages that make sense for the selected clip type
        static readonly Dictionary<string, string[]> TabSections = new() { ["Video"] = new[] { "xform", "xform2", "blend" }, ["Máscara"] = new[] { "mask" }, ["Ajuste"] = new[] { "adjust" }, ["Audio"] = new[] { "vol" }, ["Velocidad"] = new[] { "speed" }, ["Animación"] = new[] { "fade" }, ["Texto"] = new[] { "text", "xform" } };
        static string[] TabsFor(string kind) => kind switch { "video" => new[] { "Video", "Ajuste", "Máscara", "Audio", "Velocidad", "Animación" }, "image" => new[] { "Video", "Ajuste", "Máscara", "Animación" }, "audio" => new[] { "Audio", "Velocidad", "Animación" }, "text" => new[] { "Texto" }, _ => Array.Empty<string>() };
        string detailTab = "Video", detailKind;

        void BuildDetailTabs(string kind)
        {
            detailKind = kind; DetailTabs.Children.Clear(); var tabs = TabsFor(kind);
            if (!tabs.Contains(detailTab) && tabs.Length > 0) detailTab = tabs[0];
            foreach (var t in tabs)
            {
                var tt = t; bool on = t == detailTab;
                var b = new Button { Content = t, Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(0, 0, 4, 4), FontSize = 12, Background = Br(on ? "#1c3a40" : "#1c282d"), Foreground = Br(on ? "#25f4ee" : "#cfd8db") };
                b.Click += (_, _) => { detailTab = tt; BuildDetailTabs(kind); RefreshDetails(); }; DetailTabs.Children.Add(b);
            }
        }

        void RefreshDetails()
        {
            updating = true;
            bool has = sel != null; var k = sel?.Kind;
            if (k != detailKind) BuildDetailTabs(k);
            var active = TabSections.TryGetValue(detailTab, out var sl) && has ? sl : Array.Empty<string>();
            void Vis(string key, bool v) => secs[key].Visibility = v && active.Contains(key) ? Visibility.Visible : Visibility.Collapsed;
            Vis("text", k == "text"); Vis("xform", k is "video" or "image" or "text"); Vis("xform2", k is "video" or "image"); Vis("adjust", k is "video" or "image");
            Vis("blend", k is "video" or "image"); Vis("mask", k is "video" or "image"); Vis("speed", k is "video" or "audio"); Vis("vol", k is "video" or "audio"); Vis("fade", k is "video" or "image" or "audio");
            if (has)
            {
                foreach (var (key, r) in rows) { var v = ReadStatic(key); r.S.Value = v; r.L.Text = $"{r.Label}   {v.ToString(r.Fmt, CultureInfo.InvariantCulture)}"; if (r.K != null) { var at = sel.HasKf(key, Rel); r.K.Content = at ? "◆" : "◇"; } }
                tbText.Text = sel.Text; ((CheckBox)secs["xform2"].Tag).IsChecked = sel.Flip; ((CheckBox)secs["mask"].Tag).IsChecked = sel.MaskInvert;
                foreach (Button b in blendPanel.Children) { bool on = (string)b.Tag == sel.Blend; b.Background = Br(on ? "#1c3a40" : "#1c282d"); b.Foreground = Br(on ? "#25f4ee" : "#cfd8db"); }
                foreach (Button b in maskPanel.Children) { bool on = (string)b.Tag == sel.Mask; b.Background = Br(on ? "#1c3a40" : "#1c282d"); b.Foreground = Br(on ? "#25f4ee" : "#cfd8db"); }
                blendHint.Visibility = sel.Track == 2 ? Visibility.Visible : Visibility.Collapsed;
                ((TextBlock)secs["adjust"].Tag).Text = $"Filtro: {sel.Filter}   ·   Efecto: {sel.Effect}";
            }
            updating = false;
        }

        void SliderChanged(string key, double v)
        {
            if (updating || sel == null) return;
            Commit(key);
            switch (key)
            {
                case "textsize": sel.TextSize = (int)v; break;
                case "opacity": sel.Opacity = v; break;
                case "brightness": sel.Brightness = v; break;
                case "contrast": sel.Contrast = v; break;
                case "saturation": sel.Saturation = v; break;
                case "fadein": sel.FadeIn = v; break;
                case "fadeout": sel.FadeOut = v; break;
                case "maskw": sel.MaskW = v; break; case "maskh": sel.MaskH = v; break; case "maskrot": sel.MaskRot = v; break; case "maskfeather": sel.MaskFeather = v; break; case "maskround": sel.MaskRound = v; break;
                case "speed": { var used = sel.Dur * sel.Speed; sel.Speed = v; sel.Dur = used / v; break; }
                default: sel.Set(key, v, Rel); break;
            }
            var r = rows[key]; r.L.Text = $"{r.Label}   {v.ToString(r.Fmt, CultureInfo.InvariantCulture)}"; if (r.K != null) r.K.Content = sel.HasKf(key, Rel) ? "◆" : "◇";
            // only what really changes on screen: speed moves the clip end, volume reshapes the waveform; everything else is just a re-sync
            if (key == "speed") Dirty();
            else { RebuildAudio(); Sync(!playing); if (key == "volume") RedrawBlock(sel); }
        }

        void ToggleKf(string key) { if (sel == null) return; Push(); sel.ToggleKf(key, Rel); RefreshDetails(); Dirty(); }

        // ---------- selection / timeline ----------
        void Select(Seg s) { sel = s; RefreshDetails(); Redraw(); }

        static string KindColor(string k) => k switch { "video" => "#27474f", "image" => "#363f63", "audio" => "#28503a", _ => "#6a5326" };

        // per-track eye / speaker / lock like CapCut's track header: hide the picture, mute the sound, protect the clips from edits
        void BuildTrackHeaders()
        {
            TrackLabels.Children.Clear();
            for (int i = 0; i < TrackNames.Length; i++)
            {
                int t = i; var g = new Grid { Height = TrackH[i] };
                g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.Children.Add(new TextBlock { Text = TrackNames[i], Foreground = Br("#8d8d98"), FontSize = 11, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                var ic = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) }; Grid.SetColumn(ic, 1); g.Children.Add(ic);
                void Btn(List<int> list, string on, string off, string tip)
                {
                    bool flag = list.Contains(t);
                    var b = new Button { Content = flag ? off : on, ToolTip = tip, Padding = new Thickness(3, 1, 3, 1), Margin = new Thickness(0), Background = Br("transparent"), Foreground = Br(flag ? "#25f4ee" : "#6b7a80"), FontSize = 12, FontFamily = new FontFamily("Segoe UI Symbol") };
                    b.Click += (_, _) => { Push(); if (!list.Remove(t)) list.Add(t); RebuildAudio(); Dirty(); }; ic.Children.Add(b);
                }
                if (t <= 2) Btn(proj.Hidden, "👁", "◌", "Mostrar / ocultar la pista");
                if (t >= 1) Btn(proj.Muted, "🔊", "🔇", "Silenciar / activar el sonido de la pista");
                Btn(proj.Locked, "🔓", "🔒", "Bloquear / desbloquear la pista");
                TrackLabels.Children.Add(g);
            }
        }

        void Redraw()
        {
            BuildTrackHeaders();
            TL.Children.Clear(); ghost = null; blocks.Clear(); cellBrushes.Clear();
            double w = Math.Max(1200, (Total + 12) * Px); TL.Width = w; TL.Height = TrackTop(TrackNames.Length);
            int step = Px >= 80 ? 1 : Px >= 35 ? 2 : 5;
            for (int t = 0; t * Px < w; t += step)
            {
                var ln = new Rectangle { Width = 1, Height = 8, Fill = Br("#55555f") }; Canvas.SetLeft(ln, t * Px); Canvas.SetTop(ln, RULER - 8); TL.Children.Add(ln);
                var tx = new TextBlock { Text = T(t).Substring(0, T(t).Length - 2), FontSize = 10, Foreground = Br("#8d8d98") }; Canvas.SetLeft(tx, t * Px + 3); Canvas.SetTop(tx, 4); TL.Children.Add(tx);
            }
            for (int r = 0; r < TrackNames.Length; r++)
            { var bg = new Rectangle { Width = w, Height = TrackH[r] - 2, Fill = Br(r % 2 == 0 ? "#0f171b" : "#121c20") }; Canvas.SetTop(bg, TrackTop(r)); TL.Children.Add(bg); }
            foreach (var sg in proj.Segs) { var b = Block(sg); blocks[sg] = b; TL.Children.Add(b); }
            head = new Rectangle { Width = 2, Height = TL.Height, Fill = Br("#ffffff"), IsHitTestVisible = false }; Canvas.SetLeft(head, P * Px); TL.Children.Add(head);
            Clock.Text = Tc(); PlayerInfo.Text = $"{proj.Canvas}  ·  {Opts.Size(proj.Canvas).w}×{Opts.Size(proj.Canvas).h}";
        }

        // live volume change (drag on the clip): keeps playing, takes effect in the mixer immediately
        void SetVolumeLive(Seg sg, double v) { sg.Set("volume", v, Rel); RefreshDetails(); RebuildAudio(); Sync(false); }

        // rebuild a single clip block (volume/waveform change) without touching the rest of the timeline
        void RedrawBlock(Seg s)
        {
            if (!blocks.TryGetValue(s, out var old)) { Redraw(); return; }
            int i = TL.Children.IndexOf(old); if (i < 0) { Redraw(); return; }
            var nb = Block(s); blocks[s] = nb; TL.Children.RemoveAt(i); TL.Children.Insert(i, nb); // indexer assignment throws "index already in use" on a UIElementCollection
        }

        static string Db(double vol) => vol <= 0.001 ? "-∞ dB" : $"{20 * Math.Log10(vol):+0.0;-0.0;0.0} dB";

        // clip block: filmstrip of scene thumbnails on top, audio waveform below with a draggable volume line (like CapCut)
        FrameworkElement Block(Seg sg)
        {
            double h = TrackH[sg.Track] - 6, w = Math.Max(12, sg.Dur * Px);
            var g = new Grid { Width = w, Height = h, Cursor = Cursors.SizeAll, ToolTip = sg.Name };
            g.Children.Add(new Border { CornerRadius = new CornerRadius(5), Background = Br(KindColor(sg.Kind)) });
            var m = media.FirstOrDefault(x => x.Path == sg.Path);
            var inner = new Canvas { Width = w, Height = h, ClipToBounds = true, IsHitTestVisible = false }; g.Children.Add(inner);
            double thumbH = sg.Kind is "video" or "image" ? 34 : 0;
            if (thumbH > 0 && m != null)
            {
                if (sg.Kind == "video" && m.Strip != null)
                {
                    var sprite = Thumb(m.Strip); double cellW = Math.Max(24, m.TileW * thumbH / 80);
                    for (double x = 0; x < w; x += cellW)
                    {
                        int idx = Math.Clamp((int)((sg.Src + (x + cellW / 2) / Px * sg.Speed) / m.StripStep), 0, m.StripN - 1);
                        if (!cellBrushes.TryGetValue((m.Strip, idx), out var br)) { br = new ImageBrush(sprite) { Viewbox = new Rect(idx / (double)m.StripN, 0, 1.0 / m.StripN, 1), Stretch = Stretch.Fill }; br.Freeze(); cellBrushes[(m.Strip, idx)] = br; }
                        var r = new Rectangle { Width = cellW, Height = thumbH, Fill = br };
                        Canvas.SetLeft(r, x); inner.Children.Add(r);
                    }
                }
                else if (m.Thumb != null)
                {
                    var im = Thumb(m.Thumb); var bw = ((BitmapSource)im).PixelWidth * thumbH / 90.0;
                    inner.Children.Add(new Rectangle { Width = w, Height = thumbH, Fill = new ImageBrush(im) { TileMode = TileMode.Tile, Stretch = Stretch.Fill, Viewport = new Rect(0, 0, bw, thumbH), ViewportUnits = BrushMappingMode.Absolute } });
                }
            }
            bool wave = (sg.Kind == "audio" || sg.HasAudio) && m?.Peaks != null; double wy = thumbH, wh = h - thumbH;
            if (wave)
            {
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                    for (double x = 0; x < w; x += 2)
                    {
                        int bin = (int)((sg.Src + x / Px * sg.Speed) * 20); if (bin >= m.Peaks.Length) break;
                        double half = wh / 2 * Math.Min(1, Math.Sqrt(m.Peaks[bin] / 255.0) * sg.Volume);
                        ctx.BeginFigure(new Point(x, wy + wh / 2 - half), false, false); ctx.LineTo(new Point(x, wy + wh / 2 + Math.Max(.5, half)), true, false);
                    }
                geo.Freeze(); inner.Children.Add(new System.Windows.Shapes.Path { Data = geo, Stroke = Br("#bfe6ff"), StrokeThickness = 1.4, Opacity = .85 });
            }
            inner.Children.Add(new TextBlock { Text = (sg.Kind == "audio" ? "♪ " : "") + sg.Name, Foreground = Brushes.White, FontSize = 10, Margin = new Thickness(6, 2, 6, 0), Background = Br("#88000000"), Padding = new Thickness(3, 0, 3, 0) });
            foreach (var kf in sg.Kfs.Select(k => k.T).Distinct()) { var d = new TextBlock { Text = "◆", Foreground = Br("#25f4ee"), FontSize = 9 }; Canvas.SetLeft(d, kf * Px - 4); Canvas.SetTop(d, h - 14); inner.Children.Add(d); }
            if (sg.Trans != "Ninguna") { var tb = new TextBlock { Text = "⋈ " + sg.Trans, Foreground = Br("#25f4ee"), FontSize = 9.5, Background = Br("#aa000000"), Padding = new Thickness(3, 0, 3, 0) }; Canvas.SetLeft(tb, 2); Canvas.SetTop(tb, h - 28); inner.Children.Add(tb); }
            g.Children.Add(new Border { CornerRadius = new CornerRadius(5), BorderBrush = Br(sg == sel ? "#25f4ee" : "transparent"), BorderThickness = new Thickness(sg == sel ? 2 : 0), IsHitTestVisible = false });
            Canvas.SetLeft(g, sg.Start * Px); Canvas.SetTop(g, TrackTop(sg.Track) + 2);

            // volume line: only on the selected clip, drag up/down to change gain (0..2 = -inf..+6 dB)
            if (wave && sg == sel && !proj.Locked.Contains(sg.Track))
            {
                double ly = wy + wh * (1 - Math.Clamp(sg.Volume, 0, 2) / 2);
                var line = new Rectangle { Width = w, Height = 2, Fill = Br("#ffe27a"), IsHitTestVisible = false }; Canvas.SetTop(line, ly - 1); inner.Children.Add(line);
                var lbl = new TextBlock { Text = Db(sg.Volume), FontSize = 10, Foreground = Br("#ffe27a"), Background = Br("#aa000000"), Padding = new Thickness(3, 0, 3, 0), IsHitTestVisible = false }; Canvas.SetLeft(lbl, 4); Canvas.SetTop(lbl, Math.Clamp(ly - 16, 0, h - 14)); inner.Children.Add(lbl);
                var hit = new Rectangle { Width = w, Height = 14, Fill = Brushes.Transparent, Cursor = Cursors.SizeNS, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, ly - 7, 0, 0), ToolTip = "Arrastra para subir/bajar el volumen" };
                bool drag = false;
                hit.MouseLeftButtonDown += (_, e) => { drag = true; hit.CaptureMouse(); Push(); e.Handled = true; };
                hit.MouseMove += (_, e) =>
                {
                    if (!drag) return;
                    var v = Math.Round(Math.Clamp(2 * (1 - (e.GetPosition(g).Y - wy) / wh), 0, 2), 2); if (Math.Abs(v - 1) < 0.04) v = 1;
                    SetVolumeLive(sg, v); double ny = wy + wh * (1 - v / 2);
                    Canvas.SetTop(line, ny - 1); lbl.Text = Db(v); Canvas.SetTop(lbl, Math.Clamp(ny - 16, 0, h - 14)); hit.Margin = new Thickness(0, ny - 7, 0, 0);
                };
                hit.MouseLeftButtonUp += (_, _) => { if (!drag) return; drag = false; hit.ReleaseMouseCapture(); RebuildAudio(); Sync(false); RedrawBlock(sg); };
                g.Children.Add(hit);
            }

            bool moved = false, resize = false; double sx = 0, sy = 0, oS = 0, oD = 0;
            if (proj.Locked.Contains(sg.Track)) { g.Cursor = Cursors.Arrow; g.MouseLeftButtonDown += (_, e) => { Select(sg); e.Handled = true; }; inner.Children.Add(new TextBlock { Text = "🔒", FontSize = 10, Foreground = Br("#25f4ee"), Margin = new Thickness(w - 18, 2, 0, 0) }); return g; }
            g.MouseLeftButtonDown += (_, e) =>
            {
                Select(sg); var p = e.GetPosition(TL); sx = p.X; sy = p.Y; oS = sg.Start; oD = sg.Dur; moved = false; resize = e.GetPosition(g).X > g.Width - 9;
                g.CaptureMouse(); e.Handled = true;
            };
            g.MouseMove += (_, e) =>
            {
                if (e.GetPosition(g).X > g.Width - 9) g.Cursor = Cursors.SizeWE; else g.Cursor = Cursors.SizeAll;
                if (!g.IsMouseCaptured) return;
                var p = e.GetPosition(TL); if (!moved && Math.Abs(p.X - sx) < 3 && Math.Abs(p.Y - sy) < 3) return;
                if (!moved) { Push(); moved = true; }
                if (resize)
                {
                    var max = sg.Kind is "video" or "audio" ? (sg.SrcLen - sg.Src) / sg.Speed : 600.0;
                    sg.Dur = Math.Clamp(oD + (p.X - sx) / Px, .2, max); g.Width = Math.Max(12, sg.Dur * Px);
                }
                else
                {
                    var ns = Math.Max(0, oS + (p.X - sx) / Px);
                    foreach (var edge in proj.Segs.Where(o => o != sg).SelectMany(o => new[] { o.Start, o.End }).Append(P))
                    { if (Math.Abs(ns - edge) * Px < 8) ns = edge; else if (Math.Abs(ns + sg.Dur - edge) * Px < 8) ns = edge - sg.Dur; }
                    sg.Start = Math.Max(0, ns);
                    int row = RowAt(p.Y);
                    if (Compatible(sg.Kind, row) && Math.Abs(TrackH[row] - TrackH[sg.Track]) < 1) sg.Track = row;
                    Canvas.SetLeft(g, sg.Start * Px); Canvas.SetTop(g, TrackTop(sg.Track) + 2);
                }
            };
            g.MouseLeftButtonUp += (_, _) => { if (!g.IsMouseCaptured) return; g.ReleaseMouseCapture(); if (moved) { RefreshDetails(); Dirty(); } };
            return g;
        }

        static bool Compatible(string kind, int row) => kind == "text" ? row == 0 : kind == "audio" ? row >= 3 : row is 1 or 2;

        bool scrubbing;
        void TlDown(object s, MouseButtonEventArgs e)
        {
            var p = e.GetPosition(TL); scrubbing = true; TL.CaptureMouse();
            if (p.Y > RULER) { sel = null; RefreshDetails(); }
            Seek(p.X / Px);
            TL.MouseMove -= TlMove; TL.MouseMove += TlMove; TL.MouseLeftButtonUp -= TlUp; TL.MouseLeftButtonUp += TlUp;
        }
        void TlMove(object s, MouseEventArgs e) { if (scrubbing) Seek(e.GetPosition(TL).X / Px); }
        void TlUp(object s, MouseButtonEventArgs e) { scrubbing = false; TL.ReleaseMouseCapture(); Redraw(); }
        void ZoomChanged(object s, RoutedPropertyChangedEventArgs<double> e) { Px = Zoom.Value; if (TL != null) Redraw(); }

        // ---------- live preview: original files decoded in real time and composited on a Canvas, no pre-render ----------
        class Layer { public Seg S; public FrameworkElement El, Top; public Blend Bl; public MediaElement Me; public TextBlock Tb; public Grade Fx; public ScaleTransform Sc; public RotateTransform Ro; public TranslateTransform Tr; public bool Ready, Playing, WasActive, Primed; }
        readonly Dictionary<Seg, Layer> layers = new();
        readonly Stopwatch clock = new(); double p0;

        Layer Make(Seg s)
        {
            var (W, H) = Opts.Size(proj.Canvas); var l = new Layer { S = s };
            if (s.Kind == "text")
            {
                l.Tb = new TextBlock { TextAlignment = TextAlignment.Center, FontFamily = new FontFamily("Arial"), TextWrapping = TextWrapping.Wrap, MaxWidth = W * 0.9 };
                var bd = new Border { Child = l.Tb, Background = Br("#80000000"), CornerRadius = new CornerRadius(4), Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                l.El = new Grid { Width = W, Height = H, Children = { bd } };
            }
            else if (s.Kind == "image") l.El = new Image { Source = Img(s.Path), Stretch = Stretch.Uniform, Width = W, Height = H };
            else
            {
                var me = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, ScrubbingEnabled = true, Stretch = Stretch.Uniform, Width = s.Kind == "audio" ? 1 : W, Height = s.Kind == "audio" ? 1 : H, Source = new Uri(s.Path) };
                me.MediaOpened += (_, _) => { l.Ready = true; Sync(true); };
                me.MediaFailed += (_, e) => Busy.Text = "No se puede previsualizar " + Path.GetFileName(s.Path) + ": " + e.ErrorException?.Message;
                l.Me = me; l.El = me; me.Pause(); // Manual mode only loads once Play/Pause is called
            }
            l.El.RenderTransformOrigin = new Point(.5, .5);
            l.Sc = new ScaleTransform(); l.Ro = new RotateTransform(); l.Tr = new TranslateTransform();
            var tg = new TransformGroup(); tg.Children.Add(l.Sc); tg.Children.Add(l.Ro); tg.Children.Add(l.Tr); l.El.RenderTransform = tg;
            if (s.Kind == "audio") { l.Top = l.El; l.El.Opacity = 0; }
            else { var wrap = new Grid { Width = W, Height = H, IsHitTestVisible = false }; wrap.Children.Add(l.El); l.Top = wrap; }
            Panel.SetZIndex(l.Top, 10 - s.Track);
            (s.Track == 2 && s.Kind != "text" ? (Panel)Lower : Stage).Children.Add(l.Top); layers[s] = l; return l;
        }

        void DropLayer(Seg s)
        {
            if (!layers.Remove(s, out var l)) return;
            (l.Top.Parent as Panel)?.Children.Remove(l.Top); if (l.Me != null) { l.Me.Stop(); l.Me.Source = null; }
        }

        static double FadeF(Seg s, double rel) => Math.Clamp(Math.Min(s.FadeIn > 0 ? rel / s.FadeIn : 1, s.FadeOut > 0 ? (s.Dur - rel) / s.FadeOut : 1), 0, 1);

        // puts every layer in the state for time P; force = also re-seek media (scrub / edit), otherwise only fix drift
        void Sync(bool force)
        {
            var (W, H) = Opts.Size(proj.Canvas);
            foreach (var dead in layers.Keys.Where(k => !proj.Segs.Contains(k) || P < k.Start - 3 || P >= k.End + proj.Hold(k) + 0.5).ToList()) DropLayer(dead);
            foreach (var s in proj.Segs)
            {
                double hold = proj.Hold(s);
                if (P < s.Start - 2.5 || P >= s.End + hold + 0.3) continue;
                if (!layers.TryGetValue(s, out var l)) l = Make(s);
                bool active = P >= s.Start && P < s.End, held = !active && P >= s.End && P < s.End + hold; double rel = Math.Clamp(P - s.Start, 0, s.Dur);
                if (s.Kind != "audio")
                {
                    l.El.Opacity = active ? (s.Kind == "text" ? 1 : s.Opacity) * FadeF(s, rel) : held ? s.Opacity : 0;
                    Panel.SetZIndex(l.Top, (10 - s.Track) * 100000 + (int)(s.Start * 10));
                    int bm = Array.FindIndex(Opts.Blends, b => b.Name == s.Blend);
                    if (bm > 0 && s.Track != 2 && s.Kind != "text" && Blend.Ok) { l.Bl ??= new Blend { Back = lowerBrush }; l.Bl.Set(bm); l.Top.Effect = l.Bl; } else l.Top.Effect = null;
                    var sc = s.Kind == "text" ? 1 : Math.Max(0.02, s.Get("scale", rel));
                    l.Sc.ScaleX = (s.Flip ? -sc : sc); l.Sc.ScaleY = sc; l.Ro.Angle = s.Kind == "text" ? 0 : s.Get("rotation", rel); l.Tr.X = s.Get("x", rel); l.Tr.Y = s.Get("y", rel);
                    if (s.Kind == "text") { l.Tb.Text = s.Text; l.Tb.FontSize = s.TextSize; l.Tb.Foreground = Br(s.TextColor == "lime" ? "#00ff00" : s.TextColor); }
                    else ApplyGrade(l, s, W, H);
                    if (active && s.Kind != "text" && s.Trans != "Ninguna") // incoming clip animates over TransDur (same numbers the export uses)
                    {
                        var tr = Opts.TrOf(s.Trans); double pr = Math.Clamp(rel / Math.Max(.05, Math.Min(s.TransDur, s.Dur)), 0, 1), k = tr.S0 + (1 - tr.S0) * pr;
                        if (tr.Fade) l.El.Opacity *= pr;
                        l.Sc.ScaleX *= k; l.Sc.ScaleY *= k; l.Tr.X += tr.Dx * W * (1 - pr); l.Tr.Y += tr.Dy * H * (1 - pr);
                    }
                    if (proj.Hidden.Contains(s.Track)) l.El.Opacity = 0;
                }
                if (l.Me == null || !l.Ready) continue;
                var me = l.Me; double target = s.Src + rel * s.Speed;
                me.SpeedRatio = s.Speed; bool viaMixer = UsesEngine(s); me.IsMuted = viaMixer || !active || proj.Muted.Contains(s.Track); // the mixer plays this clip; MediaElement only supplies the picture
                me.Volume = active && !viaMixer ? Math.Min(1, s.Get("volume", rel) * FadeF(s, rel)) : 0;
                if (!active)
                {
                    if (l.Playing) { me.Pause(); l.Playing = false; }
                    l.WasActive = false;
                    if (held) { if (force) me.Position = TimeSpan.FromSeconds(s.Src + Math.Max(0, s.Dur - .04) * s.Speed); } // stays on its last frame under the transition
                    else if (force || !l.Primed) { me.Position = TimeSpan.FromSeconds(s.Src); l.Primed = true; }
                    continue;
                }
                if (force || !l.WasActive || (playing && Math.Abs(me.Position.TotalSeconds - target) > 0.35)) me.Position = TimeSpan.FromSeconds(target);
                l.WasActive = true;
                if (playing && !l.Playing) { me.Play(); l.Playing = true; } else if (!playing && l.Playing) { me.Pause(); l.Playing = false; }
            }
        }

        void ApplyGrade(Layer l, Seg s, int W, int H)
        {
            int look = Opts.Filters.Keys.ToList().IndexOf(s.Filter), fx = Opts.Effects.Keys.ToList().IndexOf(s.Effect);
            if (!Grade.Ok || (look <= 0 && fx <= 0 && s.Brightness == 0 && s.Contrast == 1 && s.Saturation == 1 && s.Mask == "Ninguna")) { l.El.Effect = null; return; }
            l.Fx ??= new Grade(); l.El.Effect = l.Fx;
            l.Fx.Set(s.Brightness, s.Contrast, s.Saturation, Math.Max(0, look), Math.Max(0, fx), W, H, P % 7); l.Fx.SetMask(s);
        }

        // ---------- playback ----------
        void Seek(double t)
        {
            CloseMonitor(null, null);
            P = Math.Max(0, t); if (head != null) Canvas.SetLeft(head, P * Px); Clock.Text = Tc();
            if (playing) { p0 = P; clock.Restart(); audio.Seek(P); }
            if (sel != null) RefreshDetails();
            Sync(true);
        }
        void ToStart(object s, RoutedEventArgs e) => Seek(0);

        // edits keep playing (like CapCut); only a stopped player re-seeks everything
        void Dirty() { Redraw(); RebuildAudio(); Sync(!playing); Touch(); }

        // clips whose audio is decoded and at normal speed go through the mixer (gain up to +6 dB); the rest fall back to MediaElement.Volume
        void RebuildAudio()
        {
            audio.SetClips(proj.Segs.Where(s => s.Speed == 1 && !proj.Muted.Contains(s.Track) && (s.Kind == "audio" || (s.Kind == "video" && s.HasAudio)))
                .Select(s => (s, m: media.FirstOrDefault(x => x.Path == s.Path))).Where(x => x.m?.Raw != null).Select(x => AClip.From(x.s, x.m.Raw)).ToArray());
        }
        bool UsesEngine(Seg s) => audio.Ready && s.Speed == 1 && media.Any(m => m.Path == s.Path && m.Raw != null);

        void Play(object s = null, RoutedEventArgs e = null)
        {
            CloseMonitor(null, null);
            if (playing) { StopPlay(); return; }
            if (proj.Segs.Count == 0) return;
            if (P >= Total - .05) P = 0;
            playing = true; p0 = P; clock.Restart(); PlayBtn.Content = "❚❚"; Busy.Text = ""; RebuildAudio(); audio.Start(P); Sync(true); playTimer.Start();
        }
        void StopPlay() { playing = false; audio.Stop(); playTimer.Stop(); PlayBtn.Content = "▶"; Sync(true); }

        void Tick()
        {
            if (!playing) return;
            P = Math.Max(p0, p0 + clock.Elapsed.TotalSeconds - (audio.Ready ? AudioEngine.Latency : 0)); // video waits for the audio device latency
            if (P >= Total) { P = Total; StopPlay(); Redraw(); return; }
            Sync(false); Canvas.SetLeft(head, P * Px); Clock.Text = Tc();
            if (P * Px > TlScroll.HorizontalOffset + TlScroll.ViewportWidth - 40) TlScroll.ScrollToHorizontalOffset(P * Px - 40);
        }
        // ---------- editing actions ----------
        void Split(object s = null, RoutedEventArgs e = null)
        {
            var sg = sel != null && P > sel.Start + .1 && P < sel.End - .1 ? sel : proj.Segs.FirstOrDefault(x => P > x.Start + .1 && P < x.End - .1);
            if (sg == null) { Busy.Text = "Coloca el cursor dentro de un clip"; return; }
            Push();
            var cut = P - sg.Start; var b = JsonSerializer.Deserialize<Seg>(JsonSerializer.Serialize(sg));
            foreach (var prop in sg.Kfs.Select(k => k.Prop).Distinct().ToList())
            {
                var v = sg.Get(prop, cut);
                sg.Kfs.RemoveAll(k => k.Prop == prop && k.T > cut); sg.Kfs.Add(new Kf { Prop = prop, T = cut, V = v });
                b.Kfs.RemoveAll(k => k.Prop == prop && k.T < cut); foreach (var k in b.Kfs.Where(k => k.Prop == prop)) k.T -= cut;
                if (!b.Kfs.Any(k => k.Prop == prop && k.T <= 0)) b.Kfs.Add(new Kf { Prop = prop, T = 0, V = v });
            }
            b.Start = P; b.Src = sg.Src + cut * sg.Speed; b.Dur = sg.Dur - cut; sg.Dur = cut; sg.FadeOut = 0; b.FadeIn = 0;
            proj.Segs.Insert(proj.Segs.IndexOf(sg) + 1, b); Select(sg); Dirty();
        }

        void Rotate90(object s, RoutedEventArgs e) => Edit(sg =>
        {
            var v = sg.Get("rotation", Rel) + 90; if (v > 180) v -= 360; sg.Set("rotation", v, Rel);
            // a quarter turn swaps width/height: scale to cover the canvas so no black bars (only if the scale was untouched); back to 1 on 0°/180°
            var (cw, ch) = Opts.Size(proj.Canvas); double cover = Math.Max((double)cw / ch, (double)ch / cw);
            if (!sg.Kfs.Any(k => k.Prop == "scale") && (sg.Scale == 1 || Math.Abs(sg.Scale - cover) < 1e-6)) sg.Scale = Math.Abs(v) == 90 ? cover : 1;
        }, true);
        void Mirror(object s, RoutedEventArgs e) => Edit(sg => sg.Flip = !sg.Flip, true);

        // splits the video at the cursor and inserts a 3 s still of that frame between the halves (later clips on the track shift right)
        async void Freeze(object s, RoutedEventArgs e)
        {
            var sg = sel != null && sel.Kind == "video" && P > sel.Start + .1 && P < sel.End - .1 ? sel : proj.Segs.FirstOrDefault(x => x.Kind == "video" && P > x.Start + .1 && P < x.End - .1);
            if (sg == null) { Busy.Text = "Coloca el cursor dentro de un clip de vídeo"; return; }
            var at = (sg.Src + (P - sg.Start) * sg.Speed).ToString("0.###", CultureInfo.InvariantCulture); var png = Path.Combine(Tmp, $"fz_{DateTime.Now.Ticks}.png");
            Busy.Text = "Congelando fotograma…"; var src = sg.Path;
            await Task.Run(() => Ff.Exec("ffmpeg", $"-y -v error -nostdin -ss {at} -i \"{src}\" -frames:v 1 \"{png}\""));
            if (!File.Exists(png)) { Busy.Text = "No se pudo congelar el fotograma"; return; }
            await ProbeAll(new[] { png }); Busy.Text = "";
            if (!proj.Segs.Contains(sg) || P <= sg.Start + .1 || P >= sg.End - .1) return;
            Select(sg); Split();
            var fz = new Seg { Kind = "image", Path = png, SrcLen = 5, Dur = 3, Track = sg.Track, Start = P };
            foreach (var x in proj.Segs.Where(x => x.Track == sg.Track && x.Start >= P - 1e-6)) x.Start += fz.Dur;
            proj.Segs.Add(fz); Select(fz); Dirty();
        }

        void Delete(object s = null, RoutedEventArgs e = null) { if (sel == null || proj.Locked.Contains(sel.Track)) return; Push(); proj.Segs.Remove(sel); sel = null; RefreshDetails(); Dirty(); }

        void OnKey(object s, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBox) return;
            var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
            if (e.Key == Key.F11 || (e.Key == Key.Escape && full)) ToggleFull(); else if (e.Key == Key.Space) { if (Monitor.Visibility == Visibility.Visible) ToggleMonitor(); else Play(); } else if (e.Key == Key.Delete) Delete(); else if (e.Key == Key.S && !ctrl) Split();
            else if (e.Key == Key.Z && ctrl) Undo(null, null); else if (e.Key == Key.Y && ctrl) Redo(null, null);
            else if (e.Key == Key.Left) Seek(P - 0.1); else if (e.Key == Key.Right) Seek(P + 0.1); else return;
            e.Handled = true;
        }

        // ---------- project / export ----------
        void Menu(object s, RoutedEventArgs e)
        {
            var m = new ContextMenu();
            void Add(string t, Action a) { var i = new MenuItem { Header = t }; i.Click += (_, _) => a(); m.Items.Add(i); }
            Add("Inicio", () => ShowHome()); Add("Nuevo proyecto", () => _ = NewProject("16:9"));
            Add("Importar medios…", () => Import()); Add("Guardar como plantilla…", SaveAsTemplate);
            Add("Abrir archivo .minicut…", OpenProj); Add("Exportar copia del proyecto…", SaveProj);
            m.PlacementTarget = (UIElement)s; m.IsOpen = true;
        }
        void SaveProj()
        {
            var dlg = new SaveFileDialog { Filter = "Proyecto MiniCut|*.minicut", FileName = projFile ?? "proyecto.minicut" };
            if (dlg.ShowDialog() != true) return;
            File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(proj, new JsonSerializerOptions { WriteIndented = true })); projFile = dlg.FileName;
        }
        async void OpenProj()
        {
            var dlg = new OpenFileDialog { Filter = "Proyecto MiniCut|*.minicut" };
            if (dlg.ShowDialog() != true) return;
            try { var p = JsonSerializer.Deserialize<Project>(File.ReadAllText(dlg.FileName)); await NewProject(p.Canvas, p, string.IsNullOrWhiteSpace(p.Name) ? Path.GetFileNameWithoutExtension(dlg.FileName) : p.Name); }
            catch (Exception ex) { MessageBox.Show("No se pudo abrir: " + ex.Message, "MiniCut"); }
        }

        async void Export(object s, RoutedEventArgs e)
        {
            if (proj.Segs.Count == 0) return;
            var dlg = new SaveFileDialog { Filter = "MP4|*.mp4", FileName = "export.mp4" };
            if (dlg.ShowDialog() != true) return;
            var copy = JsonSerializer.Deserialize<Project>(Json()); Busy.Text = "Exportando…";
            // ponytail: single ffmpeg pass, no progress bar; add -progress parsing if long exports need feedback
            var err = await Task.Run(() => Ff.Export(copy, dlg.FileName));
            Busy.Text = err ?? "Exportado: " + dlg.FileName;
        }
    }
}





































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
        Media dragging; Rectangle ghost; bool monitorPlaying;
        static readonly string Tmp = Ff.Cache;
        static readonly string[] Tabs_ = { "Multimedia", "Audio", "Texto", "Filtros", "Efectos", "Transiciones", "Ajuste" };

        Project proj = new();
        readonly List<Media> media = new();
        readonly Stack<string> undo = new(), redo = new();
        readonly Dictionary<string, RowUi> rows = new();
        readonly Dictionary<string, FrameworkElement> secs = new();
        readonly DispatcherTimer playTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        Seg sel; double P, Px = 50; int tab; bool updating, playing; string lastKey, projFile;
        DateTime lastT; Rectangle head; TextBox tbText;

        static void Log(string m) { try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "minicut.log"), DateTime.Now.ToString("HH:mm:ss.fff ") + m + "\n"); } catch { } }
        double Total => proj.Segs.Count == 0 ? 0 : proj.Segs.Max(s => s.End);
        double Rel => sel == null ? 0 : Math.Clamp(P - sel.Start, 0, sel.Dur);
        static string T(double s) => TimeSpan.FromSeconds(Math.Max(0, s)).ToString(@"m\:ss\.f");

        public MainWindow()
        {
            InitializeComponent(); Directory.CreateDirectory(Tmp);
            foreach (var old in Directory.GetFiles(Tmp, "p*_*.mp4").Concat(Directory.GetFiles(Tmp, "frame_*.png"))) try { File.Delete(old); } catch { }
            playTimer.Tick += (_, _) => Tick(); audio.Init();
            TL.AllowDrop = true; TL.DragOver += TlDragOver; TL.DragLeave += (_, _) => { if (ghost != null) ghost.Visibility = Visibility.Collapsed; }; TL.Drop += TlDrop;
            foreach (var c in Opts.Canvases) { var cc = c; var b = new Button { Content = c, Padding = new Thickness(8, 4, 8, 4), Tag = c }; b.Click += (_, _) => { Push(); proj.Canvas = cc; CanvasStyle(); Dirty(); }; CanvasBtns.Children.Add(b); }
            for (int i = 0; i < TrackNames.Length; i++) TrackLabels.Children.Add(new TextBlock { Text = TrackNames[i], Height = TrackH[i], Foreground = (Brush)new BrushConverter().ConvertFromString("#8d8d98"), FontSize = 11, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            BuildDetails(); CanvasStyle(); ShowTab(0); RefreshDetails(); Redraw(); Sync(true);
            Log("Grade shader ok=" + Grade.Ok + " " + Grade.Error);
            // dev hook: MINICUT_AUTOPLAY=<project.minicut> loads it and starts playback
            Loaded += async (_, _) => { var f = Environment.GetEnvironmentVariable("MINICUT_AUTOPLAY"); if (f == null) return; proj = JsonSerializer.Deserialize<Project>(File.ReadAllText(f)); await ProbeAll(proj.Segs.Where(x => x.Kind != "text").Select(x => x.Path).Distinct().ToList()); ShowTab(0); Redraw(); if (Environment.GetEnvironmentVariable("MINICUT_SELECT") != null) Select(proj.Segs[0]); Play(); };
        }

        static Brush Br(string hex) => (Brush)new BrushConverter().ConvertFromString(hex);
        static readonly Dictionary<string, ImageSource> thumbs = new();
        static ImageSource Thumb(string path) { if (!thumbs.TryGetValue(path, out var i)) thumbs[path] = i = Img(path); return i; }
        static ImageSource Img(string path)
        {
            var b = new BitmapImage(); b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.CreateOptions = BitmapCreateOptions.IgnoreImageCache; b.UriSource = new Uri(path); b.EndInit(); b.Freeze(); return b;
        }
        void CanvasStyle() { var (sw, sh) = Opts.Size(proj.Canvas); Stage.Width = sw; Stage.Height = sh; foreach (var k in layers.Keys.ToList()) DropLayer(k); foreach (Button b in CanvasBtns.Children) { b.Background = Br((string)b.Tag == proj.Canvas ? "#25f4ee" : "#26262b"); b.Foreground = Br((string)b.Tag == proj.Canvas ? "#031211" : "#e9e9ee"); } }

        // ---------- undo ----------
        string Json() => JsonSerializer.Serialize(proj);
        void Push() { undo.Push(Json()); redo.Clear(); }
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
        void ShowTab(int i)
        {
            tab = i; Tabs.Children.Clear(); LeftBody.Children.Clear();
            for (int k = 0; k < Tabs_.Length; k++)
            {
                int kk = k; var b = new Button { Content = Tabs_[k], Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 4, 4), Background = Br(k == i ? "#2f2f36" : "transparent"), Foreground = Br(k == i ? "#25f4ee" : "#b8b8c2") };
                b.Click += (_, _) => ShowTab(kk); Tabs.Children.Add(b);
            }
            switch (i)
            {
                case 0: case 1:
                    var imp = Card(i == 0 ? "＋ Importar" : "＋ Audio", "", null, "#26262b", () => Import()); LeftBody.Children.Add(imp);
                    foreach (var m in media.Where(m => i == 0 ? m.Kind != "audio" : m.Kind == "audio")) { var mm = m; LeftBody.Children.Add(Card(Path.GetFileName(m.Path), m.Kind == "image" ? "imagen" : T(m.Dur), m.Thumb, "#1d1d22", () => PreviewMedia(mm), mm)); }
                    break;
                case 2:
                    LeftBody.Children.Add(Card("Texto", "predeterminado", null, "#3a3320", () => AddText("Texto", 64, "white")));
                    LeftBody.Children.Add(Card("Título", "grande amarillo", null, "#3a3320", () => AddText("Título", 110, "yellow")));
                    LeftBody.Children.Add(Card("Subtítulo", "pequeño", null, "#3a3320", () => AddText("Subtítulo", 40, "white")));
                    LeftBody.Children.Add(Card("Rojo", "énfasis", null, "#3a2020", () => AddText("¡Atención!", 80, "red")));
                    break;
                case 3: foreach (var n in Opts.Filters.Keys) { var nn = n; LeftBody.Children.Add(Card(n, "filtro", null, Hue(n), () => Edit(sg => sg.Filter = nn, true))); } break;
                case 4: foreach (var n in Opts.Effects.Keys) { var nn = n; LeftBody.Children.Add(Card(n, "efecto", null, Hue(n), () => Edit(sg => sg.Effect = nn, true))); } break;
                case 5:
                    foreach (var (n, a, b) in new[] { ("Entrada 0.5s", 0.5, -1.0), ("Entrada 1s", 1.0, -1.0), ("Salida 0.5s", -1.0, 0.5), ("Salida 1s", -1.0, 1.0), ("Entrada y salida", 1.0, 1.0), ("Quitar", 0.0, 0.0) })
                    { var aa = a; var bb = b; LeftBody.Children.Add(Card(n, "fundido", null, "#1f2f3a", () => Edit(sg => { if (aa >= 0) sg.FadeIn = aa; if (bb >= 0) sg.FadeOut = bb; }, false))); }
                    break;
                case 6:
                    foreach (var (n, br, co, sa) in new[] { ("Brillante", .1, 1.1, 1.2), ("Contraste", 0, 1.4, 1.0), ("Apagado", -.05, .9, .6), ("Restablecer", 0, 1.0, 1.0) })
                    { var b1 = br; var c1 = co; var s1 = sa; LeftBody.Children.Add(Card(n, "ajuste", null, "#2a2a30", () => Edit(sg => { sg.Brightness = b1; sg.Contrast = c1; sg.Saturation = s1; }, true))); }
                    break;
            }
        }

        static string Hue(string n) { var h = Math.Abs(n.GetHashCode()) % 360; var c = new HslToRgb(h); return c.Hex; }
        record HslToRgb(int H) { public string Hex { get { double s = .35, l = .25, a = s * Math.Min(l, 1 - l); double f(int nn) { var k = (nn + H / 30.0) % 12; return l - a * Math.Max(-1, Math.Min(Math.Min(k - 3, 9 - k), 1)); } return $"#{(int)(f(0) * 255):x2}{(int)(f(8) * 255):x2}{(int)(f(4) * 255):x2}"; } } }

        Button Card(string title, string sub, string thumb, string bg, Action click, Media drag = null)
        {
            var st = new StackPanel { Width = 108 };
            var box = new Border { Height = 66, CornerRadius = new CornerRadius(4), Background = Br(bg), ClipToBounds = true };
            if (thumb != null) box.Child = new Image { Source = Thumb(thumb), Stretch = Stretch.UniformToFill };
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
            Section("speed", "Velocidad"); Row("speed", "speed", "Velocidad ×", 0.25, 4);
            Section("vol", "Volumen"); Row("vol", "volume", "Volumen", 0, 2, true);
            Section("fade", "Fundidos"); Row("fade", "fadein", "Entrada (s)", 0, 5, false, "0.0"); Row("fade", "fadeout", "Salida (s)", 0, 5, false, "0.0");
        }

        double ReadStatic(string k) => k switch
        {
            "textsize" => sel.TextSize, "opacity" => sel.Opacity, "brightness" => sel.Brightness, "contrast" => sel.Contrast, "saturation" => sel.Saturation,
            "speed" => sel.Speed, "fadein" => sel.FadeIn, "fadeout" => sel.FadeOut, _ => sel.Get(k, Rel)
        };

        void RefreshDetails()
        {
            updating = true;
            bool has = sel != null; var k = sel?.Kind;
            void Vis(string key, bool v) => secs[key].Visibility = v ? Visibility.Visible : Visibility.Collapsed;
            Vis("text", k == "text"); Vis("xform", k is "video" or "image" or "text"); Vis("xform2", k is "video" or "image"); Vis("adjust", k is "video" or "image");
            Vis("speed", k is "video" or "audio"); Vis("vol", k is "video" or "audio"); Vis("fade", k is "video" or "image" or "audio");
            if (has)
            {
                foreach (var (key, r) in rows) { var v = ReadStatic(key); r.S.Value = v; r.L.Text = $"{r.Label}   {v.ToString(r.Fmt, CultureInfo.InvariantCulture)}"; if (r.K != null) { var at = sel.HasKf(key, Rel); r.K.Content = at ? "◆" : "◇"; } }
                tbText.Text = sel.Text; ((CheckBox)secs["xform2"].Tag).IsChecked = sel.Flip;
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
                case "speed": { var used = sel.Dur * sel.Speed; sel.Speed = v; sel.Dur = used / v; break; }
                default: sel.Set(key, v, Rel); break;
            }
            var r = rows[key]; r.L.Text = $"{r.Label}   {v.ToString(r.Fmt, CultureInfo.InvariantCulture)}"; if (r.K != null) r.K.Content = sel.HasKf(key, Rel) ? "◆" : "◇";
            Dirty();
        }

        void ToggleKf(string key) { if (sel == null) return; Push(); sel.ToggleKf(key, Rel); RefreshDetails(); Dirty(); }

        // ---------- selection / timeline ----------
        void Select(Seg s) { sel = s; RefreshDetails(); Redraw(); }

        static string KindColor(string k) => k switch { "video" => "#27474f", "image" => "#363f63", "audio" => "#28503a", _ => "#6a5326" };

        void Redraw()
        {
            TL.Children.Clear(); ghost = null;
            double w = Math.Max(1200, (Total + 12) * Px); TL.Width = w; TL.Height = TrackTop(TrackNames.Length);
            int step = Px >= 80 ? 1 : Px >= 35 ? 2 : 5;
            for (int t = 0; t * Px < w; t += step)
            {
                var ln = new Rectangle { Width = 1, Height = 8, Fill = Br("#55555f") }; Canvas.SetLeft(ln, t * Px); Canvas.SetTop(ln, RULER - 8); TL.Children.Add(ln);
                var tx = new TextBlock { Text = T(t).Substring(0, T(t).Length - 2), FontSize = 10, Foreground = Br("#8d8d98") }; Canvas.SetLeft(tx, t * Px + 3); Canvas.SetTop(tx, 4); TL.Children.Add(tx);
            }
            for (int r = 0; r < TrackNames.Length; r++)
            { var bg = new Rectangle { Width = w, Height = TrackH[r] - 2, Fill = Br(r % 2 == 0 ? "#15151a" : "#18181d") }; Canvas.SetTop(bg, TrackTop(r)); TL.Children.Add(bg); }
            foreach (var sg in proj.Segs) TL.Children.Add(Block(sg));
            head = new Rectangle { Width = 2, Height = TL.Height, Fill = Br("#ffffff"), IsHitTestVisible = false }; Canvas.SetLeft(head, P * Px); TL.Children.Add(head);
            Clock.Text = $"{T(P)} / {T(Total)}"; PlayerInfo.Text = $"{proj.Canvas}  ·  {Opts.Size(proj.Canvas).w}×{Opts.Size(proj.Canvas).h}";
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
                        var r = new Rectangle { Width = cellW, Height = thumbH, Fill = new ImageBrush(sprite) { Viewbox = new Rect(idx / (double)m.StripN, 0, 1.0 / m.StripN, 1), Stretch = Stretch.Fill } };
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
            g.Children.Add(new Border { CornerRadius = new CornerRadius(5), BorderBrush = Br(sg == sel ? "#25f4ee" : "transparent"), BorderThickness = new Thickness(sg == sel ? 2 : 0), IsHitTestVisible = false });
            Canvas.SetLeft(g, sg.Start * Px); Canvas.SetTop(g, TrackTop(sg.Track) + 2);

            // volume line: only on the selected clip, drag up/down to change gain (0..2 = -inf..+6 dB)
            if (wave && sg == sel)
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
                    sg.Set("volume", v, Rel); double ny = wy + wh * (1 - v / 2);
                    Canvas.SetTop(line, ny - 1); lbl.Text = Db(v); Canvas.SetTop(lbl, Math.Clamp(ny - 16, 0, h - 14)); hit.Margin = new Thickness(0, ny - 7, 0, 0);
                    RefreshDetails(); RebuildAudio(); Sync(false);
                };
                hit.MouseLeftButtonUp += (_, _) => { if (!drag) return; drag = false; hit.ReleaseMouseCapture(); Dirty(); };
                g.Children.Add(hit);
            }

            bool moved = false, resize = false; double sx = 0, sy = 0, oS = 0, oD = 0;
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
        class Layer { public Seg S; public FrameworkElement El; public MediaElement Me; public TextBlock Tb; public Grade Fx; public ScaleTransform Sc; public RotateTransform Ro; public TranslateTransform Tr; public bool Ready, Playing, WasActive, Primed; }
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
            Panel.SetZIndex(l.El, 10 - s.Track); if (s.Kind == "audio") l.El.Opacity = 0;
            Stage.Children.Add(l.El); layers[s] = l; return l;
        }

        void DropLayer(Seg s)
        {
            if (!layers.Remove(s, out var l)) return;
            Stage.Children.Remove(l.El); if (l.Me != null) { l.Me.Stop(); l.Me.Source = null; }
        }

        static double FadeF(Seg s, double rel) => Math.Clamp(Math.Min(s.FadeIn > 0 ? rel / s.FadeIn : 1, s.FadeOut > 0 ? (s.Dur - rel) / s.FadeOut : 1), 0, 1);

        // puts every layer in the state for time P; force = also re-seek media (scrub / edit), otherwise only fix drift
        void Sync(bool force)
        {
            var (W, H) = Opts.Size(proj.Canvas);
            foreach (var dead in layers.Keys.Where(k => !proj.Segs.Contains(k) || P < k.Start - 3 || P >= k.End + 0.5).ToList()) DropLayer(dead);
            foreach (var s in proj.Segs)
            {
                if (P < s.Start - 2.5 || P >= s.End + 0.3) continue;
                if (!layers.TryGetValue(s, out var l)) l = Make(s);
                bool active = P >= s.Start && P < s.End; double rel = Math.Clamp(P - s.Start, 0, s.Dur);
                if (s.Kind != "audio")
                {
                    l.El.Opacity = active ? (s.Kind == "text" ? 1 : s.Opacity) * FadeF(s, rel) : 0;
                    var sc = s.Kind == "text" ? 1 : Math.Max(0.02, s.Get("scale", rel));
                    l.Sc.ScaleX = (s.Flip ? -sc : sc); l.Sc.ScaleY = sc; l.Ro.Angle = s.Kind == "text" ? 0 : s.Get("rotation", rel); l.Tr.X = s.Get("x", rel); l.Tr.Y = s.Get("y", rel);
                    if (s.Kind == "text") { l.Tb.Text = s.Text; l.Tb.FontSize = s.TextSize; l.Tb.Foreground = Br(s.TextColor == "lime" ? "#00ff00" : s.TextColor); }
                    else ApplyGrade(l, s, W, H);
                }
                if (l.Me == null || !l.Ready) continue;
                var me = l.Me; double target = s.Src + rel * s.Speed;
                me.SpeedRatio = s.Speed; me.Volume = active && !UsesEngine(s) ? Math.Min(1, s.Get("volume", rel) * FadeF(s, rel)) : 0;
                if (!active)
                {
                    if (l.Playing) { me.Pause(); l.Playing = false; }
                    l.WasActive = false; if (force || !l.Primed) { me.Position = TimeSpan.FromSeconds(s.Src); l.Primed = true; }
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
            if (!Grade.Ok || (look <= 0 && fx <= 0 && s.Brightness == 0 && s.Contrast == 1 && s.Saturation == 1)) { l.El.Effect = null; return; }
            l.Fx ??= new Grade(); l.El.Effect = l.Fx;
            l.Fx.Set(s.Brightness, s.Contrast, s.Saturation, Math.Max(0, look), Math.Max(0, fx), W, H, P % 7);
        }

        // ---------- playback ----------
        void Seek(double t)
        {
            CloseMonitor(null, null);
            P = Math.Max(0, t); if (head != null) Canvas.SetLeft(head, P * Px); Clock.Text = $"{T(P)} / {T(Total)}";
            if (playing) { p0 = P; clock.Restart(); audio.Seek(P); }
            if (sel != null) RefreshDetails();
            Sync(true);
        }
        void ToStart(object s, RoutedEventArgs e) => Seek(0);

        // edits keep playing (like CapCut); only a stopped player re-seeks everything
        void Dirty() { Redraw(); RebuildAudio(); Sync(!playing); }

        // clips whose audio is decoded and at normal speed go through the mixer (gain up to +6 dB); the rest fall back to MediaElement.Volume
        void RebuildAudio()
        {
            audio.SetClips(proj.Segs.Where(s => s.Speed == 1 && (s.Kind == "audio" || (s.Kind == "video" && s.HasAudio)))
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
            P = Math.Max(p0, p0 + clock.Elapsed.TotalSeconds - (audio.Ready ? 0.1 : 0)); // video waits for the audio device latency
            if (P >= Total) { P = Total; StopPlay(); Redraw(); return; }
            Sync(false); Canvas.SetLeft(head, P * Px); Clock.Text = $"{T(P)} / {T(Total)}";
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

        void Delete(object s = null, RoutedEventArgs e = null) { if (sel == null) return; Push(); proj.Segs.Remove(sel); sel = null; RefreshDetails(); Dirty(); }

        void OnKey(object s, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBox) return;
            var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
            if (e.Key == Key.Space) { if (Monitor.Visibility == Visibility.Visible) ToggleMonitor(); else Play(); } else if (e.Key == Key.Delete) Delete(); else if (e.Key == Key.S && !ctrl) Split();
            else if (e.Key == Key.Z && ctrl) Undo(null, null); else if (e.Key == Key.Y && ctrl) Redo(null, null);
            else if (e.Key == Key.Left) Seek(P - 0.1); else if (e.Key == Key.Right) Seek(P + 0.1); else return;
            e.Handled = true;
        }

        // ---------- project / export ----------
        void Menu(object s, RoutedEventArgs e)
        {
            var m = new ContextMenu();
            void Add(string t, Action a) { var i = new MenuItem { Header = t }; i.Click += (_, _) => a(); m.Items.Add(i); }
            Add("Nuevo proyecto", () => { Push(); proj = new Project(); sel = null; projFile = null; Title.Text = "Sin título"; CanvasStyle(); RefreshDetails(); Dirty(); });
            Add("Importar medios…", () => Import()); Add("Abrir proyecto…", OpenProj); Add("Guardar proyecto…", SaveProj);
            m.PlacementTarget = (UIElement)s; m.IsOpen = true;
        }
        void SaveProj()
        {
            var dlg = new SaveFileDialog { Filter = "Proyecto MiniCut|*.minicut", FileName = projFile ?? "proyecto.minicut" };
            if (dlg.ShowDialog() != true) return;
            File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(proj, new JsonSerializerOptions { WriteIndented = true })); projFile = dlg.FileName; Title.Text = Path.GetFileNameWithoutExtension(projFile);
        }
        async void OpenProj()
        {
            var dlg = new OpenFileDialog { Filter = "Proyecto MiniCut|*.minicut" };
            if (dlg.ShowDialog() != true) return;
            proj = JsonSerializer.Deserialize<Project>(File.ReadAllText(dlg.FileName)); projFile = dlg.FileName; Title.Text = Path.GetFileNameWithoutExtension(projFile);
            await ProbeAll(proj.Segs.Where(x => x.Kind != "text" && File.Exists(x.Path)).Select(x => x.Path).Distinct().ToList());
            undo.Clear(); redo.Clear(); sel = null; CanvasStyle(); ShowTab(tab); RefreshDetails(); Dirty();
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


















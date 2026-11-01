using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MiniCut
{
    class PInfo { public string File, Name, Canvas; public DateTime Modified; public int Clips; public double Dur; public Project P; }

    // Start screen (like CapCut's Home / Templates): recent projects with covers, trash, format templates and your own templates.
    // Projects are auto-saved as .minicut files in %LOCALAPPDATA%\MiniCut\Projects (CapCut keeps one draft folder per project the same way).
    public partial class MainWindow
    {
        static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiniCut");
        static readonly string ProjDir = Path.Combine(Root, "Projects"), TrashDir = Path.Combine(Root, "Trash"), TplDir = Path.Combine(Root, "Templates");
        static readonly (string canvas, string name, string sub)[] Formats = { ("16:9", "Horizontal 16:9", "YouTube · TV"), ("9:16", "Vertical 9:16", "TikTok · Reels · Shorts"), ("1:1", "Cuadrado 1:1", "Instagram · Feed"), ("4:3", "Clásico 4:3", "Presentaciones") };

        string curFile, homeTab = "home", tplCat = "Todas", query = ""; bool unsaved, showTrash;
        readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
        WrapPanel projPanel, tplPanel;

        void InitHome()
        {
            foreach (var dir in new[] { ProjDir, TrashDir, TplDir }) Directory.CreateDirectory(dir);
            saveTimer.Tick += (_, _) => { saveTimer.Stop(); SaveNow(); };
            Closing += (_, _) => SaveNow();
            NavHome.Click += (_, _) => { homeTab = "home"; RenderHome(); };
            NavTpl.Click += (_, _) => { homeTab = "tpl"; RenderHome(); };
            if (Environment.GetEnvironmentVariable("MINICUT_HOMETAB") == "tpl") homeTab = "tpl"; // dev hook for screenshots
            ShowHome();
            var hook = Environment.GetEnvironmentVariable("MINICUT_HOMETAB");
            if (hook == "new") Loaded += async (_, _) => { await NewProject("9:16"); await Task.Delay(2500); ShowHome(); }; // dev hook: create -> editor -> back home
        }

        // ---------- autosave ----------
        void Touch() { if (curFile == null) return; unsaved = true; saveTimer.Stop(); saveTimer.Start(); }
        void SaveNow()
        {
            if (curFile == null || !unsaved) return;
            try { File.WriteAllText(curFile, JsonSerializer.Serialize(proj)); unsaved = false; } catch { }
        }

        // ---------- navigation ----------
        void HideHome() { HomeView.Visibility = Visibility.Collapsed; Editor.Visibility = Visibility.Visible; }

        void ShowHome()
        {
            SaveNow(); if (playing) StopPlay(); CloseMonitor(null, null);
            foreach (var k in layers.Keys.ToList()) DropLayer(k);
            Editor.Visibility = Visibility.Collapsed; HomeView.Visibility = Visibility.Visible; RenderHome();
        }
        void GoHome(object s, RoutedEventArgs e) => ShowHome();

        void OpenEditor()
        {
            HideHome(); Title.Text = proj.Name; undo.Clear(); redo.Clear(); sel = null; P = 0; CanvasStyle(); ShowTab(0); RefreshDetails(); Redraw(); Sync(true);
        }

        async Task NewProject(string canvas, Project from = null, string name = null)
        {
            proj = from == null ? new Project { Canvas = canvas } : JsonSerializer.Deserialize<Project>(JsonSerializer.Serialize(from));
            proj.Name = name ?? NextName(); curFile = Path.Combine(ProjDir, Guid.NewGuid().ToString("N") + ".minicut"); media.Clear();
            unsaved = true; SaveNow();
            await ProbeAll(proj.Segs.Where(x => x.Kind != "text" && File.Exists(x.Path)).Select(x => x.Path).Distinct().ToList());
            OpenEditor();
        }

        async Task OpenProject(string file)
        {
            try { proj = JsonSerializer.Deserialize<Project>(File.ReadAllText(file)); } catch (Exception ex) { MessageBox.Show("No se pudo abrir el proyecto: " + ex.Message, "MiniCut"); return; }
            if (string.IsNullOrWhiteSpace(proj.Name)) proj.Name = Path.GetFileNameWithoutExtension(file);
            curFile = file; unsaved = false; media.Clear();
            await ProbeAll(proj.Segs.Where(x => x.Kind != "text" && File.Exists(x.Path)).Select(x => x.Path).Distinct().ToList());
            OpenEditor();
        }

        string NextName() { int n = Directory.GetFiles(ProjDir, "*.minicut").Length + 1; return "Proyecto " + n; }

        // ---------- small dialogs ----------
        string Ask(string title, string def)
        {
            var w = new Window { Title = title, Width = 380, Height = 150, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Background = Br("#0f171b"), Foreground = Br("#e9e9ee") };
            w.Resources.MergedDictionaries.Add(Resources);
            var tb = new TextBox { Text = def, Margin = new Thickness(16, 18, 16, 10) }; var ok = new Button { Content = "Aceptar", IsDefault = true, Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(0, 0, 16, 0), HorizontalAlignment = HorizontalAlignment.Right };
            string res = null; ok.Click += (_, _) => { res = tb.Text.Trim(); w.Close(); };
            var sp = new StackPanel(); sp.Children.Add(tb); sp.Children.Add(ok); w.Content = sp; w.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
            w.ShowDialog(); return string.IsNullOrEmpty(res) ? null : res;
        }

        void RenameProj(object s, MouseButtonEventArgs e)
        {
            if (curFile == null) return;
            var n = Ask("Renombrar proyecto", proj.Name); if (n == null) return;
            proj.Name = n; Title.Text = n; Touch();
        }

        void SaveAsTemplate()
        {
            var n = Ask("Guardar como plantilla", proj.Name); if (n == null) return;
            var copy = JsonSerializer.Deserialize<Project>(JsonSerializer.Serialize(proj)); copy.Name = n;
            File.WriteAllText(Path.Combine(TplDir, Guid.NewGuid().ToString("N") + ".minicut"), JsonSerializer.Serialize(copy)); Busy.Text = "Plantilla guardada: " + n;
        }

        // ---------- scanning ----------
        static List<PInfo> Scan(string dir)
        {
            var l = new List<PInfo>(); if (!Directory.Exists(dir)) return l;
            foreach (var f in Directory.GetFiles(dir, "*.minicut"))
                try
                {
                    var p = JsonSerializer.Deserialize<Project>(File.ReadAllText(f));
                    l.Add(new PInfo { File = f, Name = string.IsNullOrWhiteSpace(p.Name) ? Path.GetFileNameWithoutExtension(f) : p.Name, Canvas = p.Canvas, Modified = File.GetLastWriteTime(f), Clips = p.Segs.Count, Dur = p.Segs.Count == 0 ? 0 : p.Segs.Max(s => s.End), P = p });
                }
                catch { }
            return l.OrderByDescending(x => x.Modified).ToList();
        }

        static string Ago(DateTime t)
        {
            var s = DateTime.Now - t;
            return s.TotalMinutes < 1 ? "ahora" : s.TotalHours < 1 ? $"hace {(int)s.TotalMinutes} min" : s.TotalDays < 1 ? $"hace {(int)s.TotalHours} h" : s.TotalDays < 30 ? $"hace {(int)s.TotalDays} d" : t.ToString("dd/MM/yyyy");
        }

        // ---------- rendering ----------
        void RenderHome()
        {
            foreach (var (b, on) in new[] { (NavHome, homeTab == "home"), (NavTpl, homeTab == "tpl") }) { b.Background = Br(on ? "#1c2a30" : "transparent"); b.Foreground = Br(on ? "#ffffff" : "#aab6bb"); }
            HomeBody.Children.Clear();
            if (homeTab == "home") BuildHomePage(); else BuildTemplatesPage();
        }

        TextBlock H(string t, double size = 15) => new() { Text = t, FontSize = size, FontWeight = FontWeights.SemiBold, Foreground = Br("#f1f5f6"), Margin = new Thickness(0, 0, 0, 12) };

        Border FormatTile(string canvas, string name, string sub, double w, double h, Action click)
        {
            var (cw, ch) = Opts.Size(canvas); double s = 46.0 / Math.Max(cw, ch);
            var shape = new Border { Width = cw * s * 1.1, Height = ch * s * 1.1, CornerRadius = new CornerRadius(4), Background = new LinearGradientBrush(Br2("#2db9cc"), Br2("#69dbe6"), 45), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var cover = new Border { Height = h - 54, CornerRadius = new CornerRadius(8), Background = Br("#142026"), Child = shape };
            var st = new StackPanel(); st.Children.Add(cover);
            st.Children.Add(new TextBlock { Text = name, FontSize = 12, Margin = new Thickness(2, 7, 0, 0), Foreground = Br("#eef3f4") });
            st.Children.Add(new TextBlock { Text = sub, Style = (Style)FindResource("Lbl"), Margin = new Thickness(2, 0, 0, 0) });
            return Clickable(st, w, click);
        }
        static Color Br2(string hex) => (Color)ColorConverter.ConvertFromString(hex);

        Border Clickable(UIElement content, double w, Action click)
        {
            var b = new Border { Width = w, Margin = new Thickness(0, 0, 14, 16), Padding = new Thickness(6), CornerRadius = new CornerRadius(10), Background = Br("#0f171b"), Cursor = Cursors.Hand, Child = content };
            b.MouseEnter += (_, _) => b.Background = Br("#16232a"); b.MouseLeave += (_, _) => b.Background = Br("#0f171b");
            b.MouseLeftButtonUp += (_, e) => { if (e.ChangedButton == MouseButton.Left) click(); };
            return b;
        }

        Border ProjectCard(PInfo i, bool trash)
        {
            var (cw, ch) = Opts.Size(i.Canvas ?? "16:9");
            var img = new Image { Stretch = Stretch.UniformToFill }; var cover = new Border { Height = 124, CornerRadius = new CornerRadius(8), Background = Br("#142026"), ClipToBounds = true };
            var g = new Grid(); var firstText = i.P.Segs.FirstOrDefault(s => s.Kind == "text" && s.Text != "");
            g.Children.Add(new TextBlock { Text = i.Clips == 0 ? "＋ vacío" : firstText?.Text ?? "✂", Foreground = firstText != null ? Br("#e9d36b") : Br("#3b5560"), FontSize = firstText != null ? 22 : 18, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            g.Children.Add(img);
            g.Children.Add(new Border { Background = Br("#aa000000"), CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Child = new TextBlock { Text = T(i.Dur).Substring(0, T(i.Dur).Length - 2), FontSize = 10, Foreground = Brushes.White } });
            cover.Child = g;
            var first = i.P.Segs.Where(s => s.Kind is "video" or "image" && File.Exists(s.Path)).OrderBy(s => s.Start).FirstOrDefault();
            if (first != null) Task.Run(() => Ff.MakeCover(first.Path, first.Src, first.Kind == "image")).ContinueWith(r => Dispatcher.Invoke(() => { if (r.Result != null) img.Source = Img(r.Result); }));
            var st = new StackPanel(); st.Children.Add(cover);
            st.Children.Add(new TextBlock { Text = i.Name, FontSize = 12, Margin = new Thickness(2, 7, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Br("#eef3f4") });
            st.Children.Add(new TextBlock { Text = $"{i.Clips} clips · {i.Canvas} · {Ago(i.Modified)}", Style = (Style)FindResource("Lbl"), Margin = new Thickness(2, 0, 0, 0) });
            var card = Clickable(st, 224, () => { if (!trash) _ = OpenProject(i.File); });
            var m = new ContextMenu();
            void Add(string t, Action a) { var mi = new MenuItem { Header = t }; mi.Click += (_, _) => a(); m.Items.Add(mi); }
            if (!trash)
            {
                Add("Abrir", () => _ = OpenProject(i.File));
                Add("Renombrar", () => { var n = Ask("Renombrar proyecto", i.Name); if (n == null) return; i.P.Name = n; File.WriteAllText(i.File, JsonSerializer.Serialize(i.P)); RenderHome(); });
                Add("Duplicar", () => { var c = JsonSerializer.Deserialize<Project>(JsonSerializer.Serialize(i.P)); c.Name = i.Name + " (copia)"; File.WriteAllText(Path.Combine(ProjDir, Guid.NewGuid().ToString("N") + ".minicut"), JsonSerializer.Serialize(c)); RenderHome(); });
                Add("Eliminar (a la papelera)", () => { File.Move(i.File, Path.Combine(TrashDir, Path.GetFileName(i.File)), true); RenderHome(); });
            }
            else
            {
                Add("Restaurar", () => { File.Move(i.File, Path.Combine(ProjDir, Path.GetFileName(i.File)), true); RenderHome(); });
                Add("Eliminar para siempre", () => { if (MessageBox.Show($"¿Eliminar \"{i.Name}\" para siempre?", "MiniCut", MessageBoxButton.YesNo) == MessageBoxResult.Yes) { File.Delete(i.File); RenderHome(); } });
            }
            card.ContextMenu = m; return card;
        }

        void BuildHomePage()
        {
            var banner = new Button { Height = 150, Margin = new Thickness(0, 0, 0, 22), Padding = new Thickness(0), Background = new LinearGradientBrush(Br2("#2fb9cb"), Br2("#7fe3ec"), 20) };
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            row.Children.Add(new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(6), Background = Br("#0b2a31"), Margin = new Thickness(0, 0, 14, 0), Child = new TextBlock { Text = "＋", Foreground = Br("#25f4ee"), FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
            row.Children.Add(new TextBlock { Text = "Crear proyecto", FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = Br("#06222a"), VerticalAlignment = VerticalAlignment.Center });
            banner.Content = row; banner.Click += async (_, _) => await NewProject("16:9");
            HomeBody.Children.Add(banner);

            HomeBody.Children.Add(H("Empezar con un formato"));
            var quick = new WrapPanel();
            foreach (var f in Formats) { var ff = f; quick.Children.Add(FormatTile(f.canvas, f.name, f.sub, 172, 128, () => _ = NewProject(ff.canvas))); }
            var open = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; open.Children.Add(new TextBlock { Text = "📂", FontSize = 30, HorizontalAlignment = HorizontalAlignment.Center });
            open.Children.Add(new TextBlock { Text = "Abrir archivo .minicut", FontSize = 11, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, Foreground = Br("#aab6bb") });
            quick.Children.Add(Clickable(new Border { Height = 74, Child = open }, 172, OpenProj));
            HomeBody.Children.Add(quick);

            var head = new DockPanel { Margin = new Thickness(0, 14, 0, 12) };
            var tools = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(tools, Dock.Right);
            var search = new TextBox { Width = 190, Text = query, ToolTip = "Buscar proyectos", Margin = new Thickness(0, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center };
            search.TextChanged += (_, _) => { query = search.Text; FillProjects(); };
            var trashBtn = new Button { Content = showTrash ? "← Proyectos" : "🗑 Papelera", Margin = new Thickness(0) }; trashBtn.Click += (_, _) => { showTrash = !showTrash; RenderHome(); };
            tools.Children.Add(search); tools.Children.Add(trashBtn); head.Children.Add(tools);
            head.Children.Add(new TextBlock { Text = showTrash ? "Papelera" : "Proyectos", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Br("#f1f5f6"), VerticalAlignment = VerticalAlignment.Center });
            HomeBody.Children.Add(head);
            projPanel = new WrapPanel(); HomeBody.Children.Add(projPanel); FillProjects();
        }

        void FillProjects()
        {
            projPanel.Children.Clear();
            var list = Scan(showTrash ? TrashDir : ProjDir).Where(i => query == "" || i.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
            if (list.Count == 0) projPanel.Children.Add(new TextBlock { Text = showTrash ? "La papelera está vacía." : query != "" ? "Sin resultados." : "Aún no tienes proyectos. Crea uno con el botón de arriba.", Foreground = Br("#7d8c92"), Margin = new Thickness(4, 10, 0, 0) });
            foreach (var i in list) projPanel.Children.Add(ProjectCard(i, showTrash));
        }

        void BuildTemplatesPage()
        {
            HomeBody.Children.Add(H("Plantillas", 20));
            var search = new TextBox { Margin = new Thickness(0, 0, 0, 14), Text = query, ToolTip = "Buscar plantillas", Padding = new Thickness(10, 7, 10, 7) };
            search.TextChanged += (_, _) => { query = search.Text; FillTemplates(); }; HomeBody.Children.Add(search);
            var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 16) };
            foreach (var c in new[] { "Todas", "Formatos", "Mis plantillas" })
            {
                var cc = c; var b = new Button { Content = c, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0), Background = Br(tplCat == c ? "#1c3a40" : "#1c282d"), Foreground = Br(tplCat == c ? "#25f4ee" : "#cfd8db") };
                b.Click += (_, _) => { tplCat = cc; RenderHome(); }; chips.Children.Add(b);
            }
            HomeBody.Children.Add(chips); tplPanel = new WrapPanel(); HomeBody.Children.Add(tplPanel); FillTemplates();
        }

        void FillTemplates()
        {
            tplPanel.Children.Clear(); int n = 0;
            bool Match(string name) => query == "" || name.Contains(query, StringComparison.CurrentCultureIgnoreCase);
            if (tplCat != "Mis plantillas")
                foreach (var f in Formats.Where(f => Match(f.name))) { var ff = f; tplPanel.Children.Add(FormatTile(f.canvas, f.name, f.sub, 190, 170, () => _ = NewProject(ff.canvas))); n++; }
            if (tplCat != "Formatos")
                foreach (var i in Scan(TplDir).Where(i => Match(i.Name)))
                {
                    tplPanel.Children.Add(TemplateCard(i)); n++;
                }
            if (n == 0) tplPanel.Children.Add(new TextBlock { Text = tplCat == "Mis plantillas" ? "Aún no tienes plantillas. En el editor: Menú → Guardar como plantilla." : "Sin resultados.", Foreground = Br("#7d8c92"), Margin = new Thickness(4, 10, 0, 0) });
        }

        Border TemplateCard(PInfo i)
        {
            var img = new Image { Stretch = Stretch.UniformToFill }; var firstText = i.P.Segs.FirstOrDefault(s => s.Kind == "text" && s.Text != ""); var tg = new Grid(); tg.Children.Add(new TextBlock { Text = firstText?.Text ?? "", Foreground = Br("#e9d36b"), FontSize = 22, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }); tg.Children.Add(img);
            var cover = new Border { Height = 124, CornerRadius = new CornerRadius(8), Background = Br("#142026"), ClipToBounds = true, Child = tg };
            var first = i.P.Segs.Where(s => s.Kind is "video" or "image" && File.Exists(s.Path)).OrderBy(s => s.Start).FirstOrDefault();
            if (first != null) Task.Run(() => Ff.MakeCover(first.Path, first.Src, first.Kind == "image")).ContinueWith(r => Dispatcher.Invoke(() => { if (r.Result != null) img.Source = Img(r.Result); }));
            var st = new StackPanel(); st.Children.Add(cover);
            st.Children.Add(new TextBlock { Text = i.Name, FontSize = 12, Margin = new Thickness(2, 7, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Br("#eef3f4") });
            st.Children.Add(new TextBlock { Text = $"Mi plantilla · {i.Clips} clips · {i.Canvas}", Style = (Style)FindResource("Lbl"), Margin = new Thickness(2, 0, 0, 0) });
            var card = Clickable(st, 224, () => _ = NewProject(i.P.Canvas, i.P, i.Name + " (proyecto)"));
            var m = new ContextMenu(); var mi = new MenuItem { Header = "Eliminar plantilla" };
            mi.Click += (_, _) => { File.Delete(i.File); RenderHome(); }; m.Items.Add(mi); card.ContextMenu = m; return card;
        }
    }
}




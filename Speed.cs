using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MiniCut
{
    // Speed ramps: a curve over the clip's source, applied by cutting the clip into pieces with constant speeds (so preview and export agree).
    public partial class MainWindow
    {
        (double u, double v)[] curvePts = Opts.Curves[0].Pts.ToArray();
        Canvas curveCv; Polyline curveLine; readonly List<Ellipse> curveDots = new(); const double CW = 330, CH = 120;

        double CurveY(double v) => CH / 2 - Math.Log2(Math.Clamp(v, .25, 4)) / 2 * (CH / 2 - 8);
        double CurveV(double y) => Math.Clamp(Math.Round(Math.Pow(2, -(y - CH / 2) / (CH / 2 - 8) * 2), 2), .25, 4);
        double CurveX(double u) => 10 + u * (CW - 20);

        void LayoutCurve()
        {
            curveLine.Points = new PointCollection(curvePts.Select(p => new Point(CurveX(p.u), CurveY(p.v))));
            for (int i = 0; i < curveDots.Count; i++) { Canvas.SetLeft(curveDots[i], CurveX(curvePts[i].u) - 6); Canvas.SetTop(curveDots[i], CurveY(curvePts[i].v) - 6); }
        }
        void SetCurve((double u, double v)[] pts)
        {
            curvePts = pts.ToArray(); foreach (var d in curveDots) curveCv.Children.Remove(d); curveDots.Clear();
            for (int i = 0; i < curvePts.Length; i++)
            {
                int ix = i; var d = new Ellipse { Width = 12, Height = 12, Fill = Brushes.White, Stroke = Br("#25f4ee"), StrokeThickness = 2, Cursor = Cursors.SizeNS };
                d.MouseLeftButtonDown += (_, e) => { d.CaptureMouse(); e.Handled = true; };
                d.MouseMove += (_, e) => { if (!d.IsMouseCaptured) return; curvePts[ix].v = CurveV(e.GetPosition(curveCv).Y); LayoutCurve(); };
                d.MouseLeftButtonUp += (_, _) => d.ReleaseMouseCapture();
                curveDots.Add(d); curveCv.Children.Add(d);
            }
            LayoutCurve();
        }

        void BuildCurveUi()
        {
            var sec = (Panel)secs["curve"]; var pre = new WrapPanel();
            foreach (var c in Opts.Curves) { var pts = c.Pts; var b = new Button { Content = c.Name, Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(0, 4, 4, 0), FontSize = 12 }; b.Click += (_, _) => SetCurve(pts); pre.Children.Add(b); }
            sec.Children.Add(pre);
            curveCv = new Canvas { Width = CW, Height = CH, Background = Br("#121c20"), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, ClipToBounds = true };
            foreach (var (v, lbl) in new[] { (.25, "0.25x"), (1.0, "1x"), (4.0, "4x") })
            {
                curveCv.Children.Add(new Line { X1 = 0, X2 = CW, Y1 = CurveY(v), Y2 = CurveY(v), Stroke = Br(v == 1 ? "#3a4a50" : "#232f34"), StrokeThickness = 1 });
                var t = new TextBlock { Text = lbl, FontSize = 9, Foreground = Br("#6b7a80") }; Canvas.SetLeft(t, 2); Canvas.SetTop(t, CurveY(v) - (v == 4 ? 0 : 13)); curveCv.Children.Add(t);
            }
            curveLine = new Polyline { Stroke = Br("#25f4ee"), StrokeThickness = 2 }; curveCv.Children.Add(curveLine); sec.Children.Add(curveCv);
            SetCurve(Opts.Curves[0].Pts);
            sec.Children.Add(new TextBlock { Style = (Style)FindResource("Lbl"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Text = "Elige una curva, arrastra los puntos arriba/abajo (0.25x–4x) y aplícala. El clip se corta en 12 trozos con velocidad constante." });
            var ap = new Button { Content = "Aplicar curva", Style = (Style)FindResource("Accent"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) }; ap.Click += (_, _) => ApplyCurve(curvePts); sec.Children.Add(ap);
        }
        void BuildSpeedFxUi()
        {
            var fx = new WrapPanel();
            foreach (var c in Opts.SpeedFx) { var pts = c.Pts; var b = new Button { Content = c.Name, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 4, 4, 0) }; b.Click += (_, _) => ApplyCurve(pts); fx.Children.Add(b); }
            ((Panel)secs["speedfx"]).Children.Add(fx);
            ((Panel)secs["speedfx"]).Children.Add(new TextBlock { Style = (Style)FindResource("Lbl"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Text = "Efectos listos: se aplican al clip seleccionado de una vez." });
        }

        void ApplyCurve((double u, double v)[] pts)
        {
            var s0 = sel; if (s0 == null || s0.Kind is not ("video" or "audio")) { Busy.Text = "Selecciona un clip de vídeo o audio"; return; }
            if (proj.Locked.Contains(s0.Track)) { Busy.Text = "La pista está bloqueada"; return; }
            Push(); int n = 12; double used = s0.Dur * s0.Speed, oldEnd = s0.End, t = s0.Start, ss = used / n; var json = JsonSerializer.Serialize(s0); var pieces = new List<Seg>();
            for (int i = 0; i < n; i++)
            {
                var p = JsonSerializer.Deserialize<Seg>(json); p.Kfs.Clear(); p.Speed = Math.Round(Math.Clamp(Opts.CurveAt(pts, (i + .5) / n), .25, 4), 2);
                p.Src = s0.Src + i * ss; p.Dur = ss / p.Speed; p.Start = t; t += p.Dur; if (i > 0) { p.FadeIn = 0; p.Trans = "Ninguna"; } if (i < n - 1) p.FadeOut = 0; pieces.Add(p);
            }
            foreach (var x in proj.Segs.Where(x => x != s0 && x.Track == s0.Track && x.Start >= oldEnd - 1e-6)) x.Start += t - oldEnd; // ripple the clips after it
            int idx = proj.Segs.IndexOf(s0); proj.Segs.RemoveAt(idx); proj.Segs.InsertRange(idx, pieces); Select(pieces[0]); Dirty();
        }
    }
}

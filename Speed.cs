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
                int ix = i; var d = new Ellipse { Width = 12, Height = 12, Fill = Brushes.White, Stroke = Br("#25f4ee"), StrokeThickness = 2, Cursor = Cursors.SizeAll, ToolTip = "Arrastra · clic derecho para quitar" };
                d.MouseLeftButtonDown += (_, e) => { d.CaptureMouse(); e.Handled = true; };
                d.MouseMove += (_, e) =>
                {
                    if (!d.IsMouseCaptured) return; var p = e.GetPosition(curveCv); curvePts[ix].v = CurveV(p.Y);
                    if (ix > 0 && ix < curvePts.Length - 1) curvePts[ix].u = Math.Clamp((p.X - 10) / (CW - 20), curvePts[ix - 1].u + .03, curvePts[ix + 1].u - .03); // end points keep their place
                    LayoutCurve();
                };
                d.MouseLeftButtonUp += (_, _) => d.ReleaseMouseCapture();
                d.MouseRightButtonDown += (_, e) => { e.Handled = true; if (curvePts.Length > 2 && ix > 0 && ix < curvePts.Length - 1) SetCurve(curvePts.Where((_, k) => k != ix).ToArray()); };
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
            curveCv.MouseLeftButtonDown += (_, e) => // click on an empty spot adds a point there
            {
                var p = e.GetPosition(curveCv); double u = Math.Clamp((p.X - 10) / (CW - 20), .02, .98);
                if (curvePts.Any(q => Math.Abs(q.u - u) < .03)) return;
                SetCurve(curvePts.Append((u, CurveV(p.Y))).OrderBy(q => q.u).ToArray());
            };
            foreach (var (v, lbl) in new[] { (.25, "0.25x"), (1.0, "1x"), (4.0, "4x") })
            {
                curveCv.Children.Add(new Line { X1 = 0, X2 = CW, Y1 = CurveY(v), Y2 = CurveY(v), Stroke = Br(v == 1 ? "#3a4a50" : "#232f34"), StrokeThickness = 1, IsHitTestVisible = false });
                var t = new TextBlock { Text = lbl, FontSize = 9, Foreground = Br("#6b7a80"), IsHitTestVisible = false }; Canvas.SetLeft(t, 2); Canvas.SetTop(t, CurveY(v) - (v == 4 ? 0 : 13)); curveCv.Children.Add(t);
            }
            curveLine = new Polyline { Stroke = Br("#25f4ee"), StrokeThickness = 2, IsHitTestVisible = false }; curveCv.Children.Add(curveLine); sec.Children.Add(curveCv);
            SetCurve(Opts.Curves[0].Pts);
            sec.Children.Add(new TextBlock { Style = (Style)FindResource("Lbl"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Text = "Elige una curva y edítala: arrastra un punto (0.25x–4x), clic en un hueco para añadir uno, clic derecho sobre un punto para quitarlo. Al aplicarla, el clip se corta en trozos de ~0,5 s con velocidad constante, así que la rampa sale suave." });
            var ap = new Button { Content = "Aplicar curva", Style = (Style)FindResource("Accent"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) }; ap.Click += (_, _) => ApplyCurve(curvePts); sec.Children.Add(ap);
        }
        void BuildSpeedFxUi()
        {
            var fx = new WrapPanel();
            foreach (var c in Opts.SpeedFx) { var pts = c.Pts; var b = new Button { Content = c.Name, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 4, 4, 0) }; b.Click += (_, _) => ApplyCurve(pts); fx.Children.Add(b); }
            ((Panel)secs["speedfx"]).Children.Add(fx);
            ((Panel)secs["speedfx"]).Children.Add(new TextBlock { Style = (Style)FindResource("Lbl"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Text = "Efectos listos: se aplican al clip seleccionado de una vez." });
        }

        string SpeedInfo() => sel.Curved ? $"Curva de velocidad    {sel.Used:0.0}s   →   {sel.Dur:0.0}s" : $"Duración    {sel.Dur * sel.Speed:0.0}s   →   {sel.Dur:0.0}s";

        // puts a continuous speed curve on the selected clip (no cutting): the clip keeps its length in source seconds, its timeline length follows the curve
        void ApplyCurve((double u, double v)[] pts)
        {
            var s0 = sel; if (s0 == null || s0.Kind is not ("video" or "audio")) { Busy.Text = "Selecciona un clip de vídeo o audio"; return; }
            if (proj.Locked.Contains(s0.Track)) { Busy.Text = "La pista está bloqueada"; return; }
            Push(); double used = s0.Used, oldEnd = s0.End;
            s0.Curve = pts.Select(p => new Kf { Prop = "speed", T = p.u * used, V = Math.Round(Math.Clamp(p.v, .25, 4), 2) }).ToList(); s0.Speed = 1; s0.Dur = s0.OutOf(used);
            foreach (var x in proj.Segs.Where(x => x != s0 && x.Track == s0.Track && x.Start >= oldEnd - 1e-6)) x.Start += s0.End - oldEnd; // ripple the clips after it
            RefreshDetails(); Dirty();
        }
    }
}

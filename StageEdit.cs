using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MiniCut
{
    // Edit directly on the preview like CapCut: click to select what is under the cursor, drag to move it, wheel to scale (text: size)
    public partial class MainWindow
    {
        Rectangle stageSel; Seg stageSeg; Point stageDown; double sx0, sy0; bool stageMoved;

        void InitStage()
        {
            stageSel = new Rectangle { Stroke = Br("#25f4ee"), StrokeThickness = 3, IsHitTestVisible = false, Visibility = Visibility.Collapsed }; Panel.SetZIndex(stageSel, int.MaxValue); Stage.Children.Add(stageSel);
            Stage.MouseLeftButtonDown += StageMouseDown; Stage.MouseMove += StageMouseMove; Stage.MouseLeftButtonUp += StageMouseUp; Stage.MouseWheel += StageWheel;
            Stage.AllowDrop = true; Stage.DragOver += (_, e) => { if (dragFx != null) { e.Effects = DragDropEffects.Copy; e.Handled = true; } };
            Stage.Drop += (_, e) => { if (dragFx == null) return; var k = dragFx; dragFx = null; var t = StageHit(e.GetPosition(Stage)) ?? sel; if (t != null) ApplyFx(t, k); e.Handled = true; };
        }

        Rect LayerBounds(Layer l, Seg s)
        {
            FrameworkElement f = s.Kind == "text" && l.El is Grid g && g.Children.Count > 0 ? (FrameworkElement)g.Children[0] : l.El;
            try { return f.TransformToVisual(Stage).TransformBounds(new Rect(0, 0, f.ActualWidth > 0 ? f.ActualWidth : f.Width, f.ActualHeight > 0 ? f.ActualHeight : f.Height)); } catch { return Rect.Empty; }
        }

        // topmost visible clip under the point (text wins, then upper tracks)
        Seg StageHit(Point p)
        {
            Seg best = null;
            foreach (var (s, l) in layers)
            {
                if (s.Kind == "audio" || P < s.Start || P >= s.End || proj.Hidden.Contains(s.Track) || l.El.Opacity <= 0) continue;
                if (!LayerBounds(l, s).Contains(p)) continue;
                if (best == null || s.Track < best.Track) best = s;
            }
            return best;
        }

        void StageMouseDown(object o, MouseButtonEventArgs e)
        {
            FocusStage(); var p = e.GetPosition(Stage); var s = StageHit(p);
            if (s == null) { if (sel != null) { sel = null; RefreshDetails(); Redraw(); } e.Handled = true; return; }
            if (sel != s) Select(s);
            if (proj.Locked.Contains(s.Track)) { Busy.Text = "La pista está bloqueada"; return; }
            stageSeg = s; stageDown = p; sx0 = s.Get("x", Rel); sy0 = s.Get("y", Rel); stageMoved = false; Stage.CaptureMouse(); e.Handled = true;
        }
        void StageMouseMove(object o, MouseEventArgs e)
        {
            var p = e.GetPosition(Stage);
            if (!Stage.IsMouseCaptured || stageSeg == null) { Stage.Cursor = StageHit(p) != null ? Cursors.SizeAll : Cursors.Arrow; return; }
            if (!stageMoved && Math.Abs(p.X - stageDown.X) + Math.Abs(p.Y - stageDown.Y) < 3) return;
            if (!stageMoved) { Push(); stageMoved = true; }
            stageSeg.Set("x", Math.Round(sx0 + p.X - stageDown.X), Rel); stageSeg.Set("y", Math.Round(sy0 + p.Y - stageDown.Y), Rel);
            Sync(false); RefreshDetails();
        }
        void StageMouseUp(object o, MouseButtonEventArgs e) { if (!Stage.IsMouseCaptured) return; Stage.ReleaseMouseCapture(); var moved = stageMoved; stageSeg = null; if (moved) Dirty(); }
        void StageWheel(object o, MouseWheelEventArgs e)
        {
            if (sel == null || sel.Kind == "audio" || proj.Locked.Contains(sel.Track)) return; Commit("stagewheel");
            if (sel.Kind == "text") sel.TextSize = Math.Clamp(sel.TextSize + (e.Delta > 0 ? 4 : -4), 12, 300); else sel.Set("scale", Math.Round(Math.Clamp(sel.Get("scale", Rel) * (e.Delta > 0 ? 1.05 : 1 / 1.05), .1, 4), 3), Rel);
            Sync(false); RefreshDetails(); Touch(); e.Handled = true;
        }

        // frame around the selected clip, like CapCut's
        void UpdateStageOutline()
        {
            if (stageSel == null) return;
            if (sel != null && layers.TryGetValue(sel, out var l) && P >= sel.Start && P < sel.End && sel.Kind != "audio" && !proj.Hidden.Contains(sel.Track))
            {
                var r = LayerBounds(l, sel); if (!r.IsEmpty && r.Width > 0) { Canvas.SetLeft(stageSel, r.X); Canvas.SetTop(stageSel, r.Y); stageSel.Width = r.Width; stageSel.Height = r.Height; stageSel.Visibility = Visibility.Visible; return; }
            }
            stageSel.Visibility = Visibility.Collapsed;
        }
    }
}

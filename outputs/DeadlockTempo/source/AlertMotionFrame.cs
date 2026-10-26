using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DeadlockTempo
{
    // The content never moves or scales. Three delayed contour layers expand around
    // it, using a critically damped response and smooth opacity envelopes.
    // Repeat while an alert is visible; digit updates never restart the cycle.
    internal sealed class AlertMotionFrame : Decorator
    {
        internal static readonly Point RestOffset = new Point(26, 36);
        internal const double Duration = 3.6;
        private string phase;
        private bool moving;
        private double elapsed, accent;
        internal double StartedAt { get; private set; }
        internal int TransitionCount { get; private set; }
        internal double Scale { get { return 1; } }
        internal double Lift { get { return 0; } }
        internal double Accent { get { return accent; } }
        internal bool IsMoving { get { return moving; } }
        internal string Phase { get { return phase; } }
        internal double BorderSpread { get { return Response(elapsed / 2.3); } }

        internal AlertMotionFrame(UIElement content)
        {
            Child = content;
            IsHitTestVisible = false;
        }
        internal void Trigger(string next, double now, bool motion)
        {
            if (!motion || next == null || next == "Ink") { Reset(); return; }
            phase = next; StartedAt = now; moving = true; TransitionCount++;
            Advance(now, true);
        }
        internal void Reset()
        {
            bool changed = moving || accent != 0;
            moving = false; accent = elapsed = 0;
            if (changed) InvalidateVisual();
        }
        internal void Synchronize(string next, double now, bool motion)
        {
            if (!motion || next == null || next == "Ink") { Reset(); return; }
            if (!moving) { Trigger(next, now, true); return; }
            // A disappearing higher-priority alert or a clock correction can change
            // the displayed phase without announcing a new event or resetting its rhythm.
            if (phase != next) { phase = next; InvalidateVisual(); }
        }
        private static double Clamp(double x) { return Math.Max(0, Math.Min(1, x)); }
        private static double Smoother(double x)
        {
            x = Clamp(x); return x * x * x * (x * (x * 6 - 15) + 10);
        }
        private static double Response(double x)
        {
            // Zero initial velocity; asymptotic deceleration, with no spring overshoot.
            x = Clamp(x);
            return (1 - (1 + 7 * x) * Math.Exp(-7 * x)) / (1 - 8 * Math.Exp(-7));
        }
        internal void Advance(double now, bool motion)
        {
            if (!motion || !moving) { Reset(); return; }
            double t = Math.Max(0, now - StartedAt) % Duration;
            elapsed = t;
            accent = Smoother(t / .28) * (1 - Smoother((t - 2.35) / 1.25));
            InvalidateVisual();
        }
        private static SolidColorBrush Ink(Color c, double opacity)
        {
            var brush = new SolidColorBrush(Color.FromArgb((byte)(255 * Clamp(opacity)), c.R, c.G, c.B));
            brush.Freeze(); return brush;
        }
        private static Pen Stroke(Color c, double opacity, double width)
        {
            var pen = new Pen(Ink(c, opacity), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            pen.Freeze(); return pen;
        }
        private static void Corners(DrawingContext dc, Rect r, Pen pen, double length)
        {
            foreach (int sx in new[] { -1, 1 }) foreach (int sy in new[] { -1, 1 })
            {
                double x = sx < 0 ? r.Left : r.Right, y = sy < 0 ? r.Top : r.Bottom;
                dc.DrawLine(pen, new Point(x - sx * length, y), new Point(x - sx * 3, y));
                dc.DrawLine(pen, new Point(x - sx * 3, y), new Point(x, y - sy * 3));
                dc.DrawLine(pen, new Point(x, y - sy * 3), new Point(x, y - sy * Math.Min(length, 10)));
            }
        }
        private static void Diamond(DrawingContext dc, Point p, double radius, Brush brush)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(p.X, p.Y - radius), true, true);
                g.LineTo(new Point(p.X + radius, p.Y), true, false);
                g.LineTo(new Point(p.X, p.Y + radius), true, false);
                g.LineTo(new Point(p.X - radius, p.Y), true, false);
            }
            geometry.Freeze(); dc.DrawGeometry(brush, null, geometry);
        }
        internal Rect PaintedBounds { get { return new Rect(RestOffset, Child.RenderSize); } }
        internal Rect EffectBounds
        {
            get { Rect r = PaintedBounds; if (accent > 0) r.Inflate(20, 24); return r; }
        }

        private void DrawWave(DrawingContext dc, Color color, double start, double duration, double strength)
        {
            for (int layer = 0; layer < 3; layer++)
            {
                double age = elapsed - start - layer * .16;
                if (age <= 0 || age >= duration) continue;
                double u = age / duration, expansion = Response(u);
                double alpha = Smoother(age / .30) * (1 - Smoother((u - .30) / .70));
                alpha *= strength * (1 - layer * .22);
                Rect ring = PaintedBounds;
                ring.Inflate(3 + 14 * expansion, 3 + 18 * expansion);
                Corners(dc, ring, Stroke(color, .72 * alpha, 1.2 - layer * .15), 24 + 16 * expansion);
            }
        }
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            if (accent <= 0 || Child.RenderSize.Width <= 0) return;
            Color color = AlertChip.PhaseColor(phase);
            Rect inner = PaintedBounds; inner.Inflate(2.5, 2.5);
            // Open ornaments frame the announcement without turning it into a box.
            var rail = new LinearGradientBrush(new GradientStopCollection {
                new GradientStop(Ink(color, 0).Color, 0),
                new GradientStop(Ink(color, .5 * accent).Color, .25),
                new GradientStop(Ink(color, .5 * accent).Color, .75),
                new GradientStop(Ink(color, 0).Color, 1)
            }, new Point(0,.5), new Point(1,.5));
            foreach (double railY in new[] { inner.Top, inner.Bottom })
                dc.DrawLine(new Pen(rail, .8), new Point(inner.Left + 40, railY), new Point(inner.Right - 40, railY));
            Corners(dc, inner, Stroke(color, .6 * accent, 1), 28);
            if (phase == "Danger")
            {
                DrawWave(dc, color, 0, 2.0, 1);
                DrawWave(dc, color, 1.10, 2.15, .86);
            }
            else DrawWave(dc, color, 0, phase == "Active" ? 2.85 : 2.55, 1);

            // Light glides along a fixed rail: the text and its background stay still.
            double cycle = phase == "Danger" ? (elapsed >= 1.10 ? (elapsed - 1.10) / 1.55 : elapsed / 1.10) : elapsed / 1.55;
            double progress = Smoother(cycle);
            double left = inner.Left + 5, right = inner.Right - 5, y = inner.Bottom + 1;
            double head = left + (right - left) * progress;
            double beam = accent * Smoother(cycle / .20) * (1 - Smoother((cycle - .70) / .30));
            if (beam > .001)
            {
                var gradient = new LinearGradientBrush(Ink(color, 0).Color, Ink(color, .85 * beam).Color, new Point(0, .5), new Point(1, .5));
                dc.DrawLine(new Pen(gradient, 1.3), new Point(Math.Max(left, head - 40), y), new Point(head, y));
                Diamond(dc, new Point(head, y), 2, Ink(Color.FromRgb(255, 245, 220), .9 * beam));
            }
            if (phase == "Active")
            {
                double opening = Response(elapsed / 1.8), center = inner.Left + inner.Width / 2;
                dc.DrawLine(Stroke(color, .8 * accent, 1.2), new Point(center - inner.Width * .4 * opening, inner.Top - 1),
                    new Point(center + inner.Width * .4 * opening, inner.Top - 1));
                Diamond(dc, new Point(center, inner.Top - 1), 2, Ink(color, accent));
            }
        }
        protected override Size MeasureOverride(Size constraint)
        {
            Child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return new Size(Math.Ceiling(Child.DesiredSize.Width + 52), Math.Ceiling(Child.DesiredSize.Height + 72));
        }
        protected override Size ArrangeOverride(Size size)
        {
            Child.Arrange(new Rect(RestOffset, Child.DesiredSize)); return size;
        }
        internal static Point WindowPosition(Size envelope, Rect bounds, double x, double y)
        { return CursorAlerts.Place(envelope, bounds, x, y); }
    }
}

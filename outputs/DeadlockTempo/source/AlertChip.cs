using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
namespace DeadlockTempo {
    // Only the surrounding contours animate; the readable announcement stays still.
    internal sealed class AlertChip : StackPanel {
        private readonly TextBlock label, note;
        private readonly Image picture;
        private double layoutWidth, measuredWidth = -1, measuredSize = -1;
        private string measuredName;
        private readonly SolidColorBrush ink = new SolidColorBrush();
        private readonly HashSet<string> announced = new HashSet<string>();
        private string phase;
        private Color fromColor, targetColor;
        private double transitionAt;
        private bool moving;
        internal int TransitionCount { get; private set; }
        internal string Phase { get { return phase; } }
        internal string Text { get { return label.Text; } }
        internal double TextSize { get { return label.FontSize; } }
        internal static Color PhaseColor(string state) {
            return state == "Active" ? Color.FromRgb(151,221,174) : state == "Danger" ? Color.FromRgb(255,128,116) : state == "Warning" ? Color.FromRgb(255,211,106) : Color.FromRgb(242,230,202);
        }
        internal AlertChip(string eventId, double size) {
            Orientation = Orientation.Vertical; Margin = new Thickness(0,4,0,4);
            HorizontalAlignment = HorizontalAlignment.Center; IsHitTestVisible = false;
            label = new TextBlock { FontFamily = (FontFamily)Application.Current.FindResource("DisplayFont"), FontWeight = FontWeights.Bold,
                Foreground = ink, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 8, ShadowDepth = 2, Opacity = .95 } };
            picture = new Image { Source = GameIcons.Get(eventId), Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 6, ShadowDepth = 1, Opacity = .85 } };
            picture.Visibility = picture.Source == null ? Visibility.Collapsed : Visibility.Visible;
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
            var headline = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            headline.Children.Add(picture); headline.Children.Add(label);
            note = new TextBlock { FontFamily = (FontFamily)Application.Current.FindResource("BodyFont"), Foreground = (Brush)Application.Current.FindResource("Ink"),
                TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,2,0,0) };
            Children.Add(headline); Children.Add(note);
        }
        internal void SetLayout(double width) { Width = layoutWidth = width; note.Width = width; }
        private void FitHeadline(Occurrence item, double size) {
            if (measuredWidth == layoutWidth && measuredSize == size && measuredName == item.Item.Name) return;
            measuredWidth = layoutWidth; measuredSize = size; measuredName = item.Item.Name;
            double artSize = size * 1.7, gap = size * .38;
            picture.Width = picture.Height = artSize;
            picture.Margin = new Thickness(0,0,gap,0);
            double available = Math.Max(1,layoutWidth - (picture.Source == null ? 0 : artSize + gap));
            double textWidth = 0;
            var face = new Typeface(label.FontFamily,label.FontStyle,label.FontWeight,label.FontStretch);
            // Reserve the longest phase so the picture and title do not jump when
            // the countdown loses a digit or changes to the started confirmation.
            foreach (double seconds in new[] { item.At - 30, item.At - 10, item.At - 9, item.At }) {
                var text = new FormattedText(CursorAlerts.Announcement(item,seconds), System.Globalization.CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,face,size,ink,VisualTreeHelper.GetDpi(this).PixelsPerDip);
                textWidth = Math.Max(textWidth,text.WidthIncludingTrailingWhitespace);
            }
            label.Width = Math.Min(available,Math.Ceiling(textWidth)+2);
        }
        internal void Update(Occurrence item, double seconds, double now, bool motion, double size) {
            string next = CursorAlerts.Phase(item,seconds);
            if (next != phase) {
                fromColor = phase == null ? PhaseColor(next) : ink.Color; targetColor = PhaseColor(next); phase = next;
                bool first = announced.Add(next); moving = motion && next != "Ink" && first;
                if (moving) { transitionAt = now; TransitionCount++; }
            }
            label.FontSize = size; label.Text = CursorAlerts.Announcement(item,seconds);
            FitHeadline(item,size);
            note.FontSize = Math.Max(12,size*.30);
            note.Text = item.Reminder ? "НАПОМИНАНИЕ · ТОЧНОЕ ВРЕМЯ ПОЯВЛЕНИЯ НЕ ОПРЕДЕЛЕНО" :
                item.Window ? "ПРИМЕРНОЕ ОКНО · " + Times.Format(item.At) + "–" + Times.Format(item.End) : "";
            note.Visibility = item.Window || item.Reminder ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetName(this,label.Text + (note.Text.Length > 0 ? ", " + note.Text : "")); Advance(now,motion);
        }
        internal void Advance(double now, bool motion) {
            double t = Math.Max(0,now-transitionAt); if (!motion || t >= .32) moving = false;
            double blend = moving ? Math.Min(1,t/.32) : 1; blend = blend*blend*(3-2*blend);
            ink.Color = Color.FromRgb((byte)(fromColor.R+(targetColor.R-fromColor.R)*blend),
                (byte)(fromColor.G+(targetColor.G-fromColor.G)*blend),(byte)(fromColor.B+(targetColor.B-fromColor.B)*blend));
        }
    }
}

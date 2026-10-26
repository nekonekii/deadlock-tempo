using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace DeadlockTempo
{
    internal static class CursorAlerts
    {
        internal const int Limit = 6;
        internal const int ActiveSeconds = 10;
        internal static bool IsActive(Occurrence item, double seconds)
        {
            return seconds >= item.At && seconds < item.At + ActiveSeconds;
        }
        internal static string Phase(Occurrence item, double seconds)
        {
            return IsActive(item, seconds) ? "Active" : ScheduleEngine.Urgency(item, seconds);
        }
        private static IEnumerable<Occurrence> Candidates(IEnumerable<Occurrence> items, double seconds)
        {
            foreach (var item in items)
            {
                // Retain the just-started occurrence as well as a possible imminent repeat.
                if (IsActive(item, seconds)) yield return item;
                var next = NextFuture(item, seconds);
                if (next.At > seconds) yield return next;
            }
        }
        internal static List<Occurrence> Select(IEnumerable<Occurrence> items, double seconds)
        {
            // A window's opening gets the same ten-second confirmation as a fixed event.
            return Candidates(items, seconds).Where(x => x.At - seconds <= AlertTiming.WarningSeconds)
                .OrderBy(x => x.At).ThenBy(x => x.Item.Id, StringComparer.Ordinal).ToList();
        }
        internal static List<Occurrence> Display(IEnumerable<Occurrence> items, double seconds)
        {
            var list = Candidates(items, seconds)
                .OrderBy(x => x.At).ThenBy(x => x.Item.Id, StringComparer.Ordinal).ToList();
            var warnings = list.Where(x => x.At - seconds <= AlertTiming.WarningSeconds).ToList();
            return warnings.Count > 0 ? warnings : list.Take(2).ToList();
        }
        private static Occurrence NextFuture(Occurrence item, double seconds)
        {
            double every;
            // The large panel retains a completed row briefly. It must not delay
            // the next cursor warning for user schedules with short periods.
            if (item.At <= seconds && Times.Parse(item.Item.Every, out every) && every > 0)
            {
                double at = item.At + (Math.Floor((seconds - item.At) / every) + 1) * every;
                return new Occurrence { Item = item.Item, At = at, End = at };
            }
            return item;
        }
        internal static bool CanShow(bool enabled, bool compact, bool running, bool ended, bool automatic, bool synchronized)
        {
            return enabled && compact && running && !ended && (!automatic || synchronized);
        }
        internal static string Countdown(Occurrence item, double seconds)
        {
            if (IsActive(item, seconds)) return item.Reminder ? "ПРОВЕРЬ" : item.Window ? "ОКНО" : "АКТИВНО";
            double remaining = Math.Max(0, Math.Ceiling(item.At - seconds));
            return remaining > AlertTiming.WarningSeconds ? Times.Format(remaining) : remaining.ToString("0") + "с";
        }
        internal static Point Place(Size size, Rect bounds, double unused, double topPercent)
        {
            double left = bounds.Left + (bounds.Width - size.Width) / 2;
            double top = bounds.Top + bounds.Height * Math.Max(15, Math.Min(25, topPercent)) / 100 - AlertMotionFrame.RestOffset.Y;
            return new Point(Math.Max(bounds.Left + 4, Math.Min(left, bounds.Right - size.Width - 4)),
                Math.Max(bounds.Top + 4, Math.Min(top, bounds.Bottom - size.Height - 4)));
        }
        internal static string Announcement(Occurrence item, double seconds)
        {
            if (item.Reminder) return IsActive(item, seconds) ? "РАЗЛОМ · ПРОВЕРЬ КАРТУ" :
                "ПРОВЕРИТЬ РАЗЛОМ · ЧЕРЕЗ " + Math.Max(0, Math.Ceiling(item.At - seconds)).ToString("0") + " С";
            string name;
            switch (item.Item.Id) {
                case "small": name = "МАЛЫЕ ЛАГЕРЯ"; break;
                case "medium": name = "СРЕДНИЕ ЛАГЕРЯ"; break;
                case "large": name = "КРУПНЫЕ ЛАГЕРЯ"; break;
                case "boxes": name = "КОРОБКИ И БАФФЫ"; break;
                case "runes": name = "РУНЫ"; break;
                case "machines": name = "АВТОМАТЫ"; break;
                case "rift": name = "НЕСТАБИЛЬНЫЙ РАЗЛОМ"; break;
                case "urn": name = "ЧАША ДУШ"; break;
                default: name = item.Item.Name.ToUpperInvariant(); break;
            }
            return IsActive(item, seconds) ? name + (item.Window ? " · ОКНО ОТКРЫТО" : " · ВРЕМЯ ПРИШЛО") :
                name + (item.Window ? " · ОКНО ЧЕРЕЗ " : " · ЧЕРЕЗ ") + Math.Max(0, Math.Ceiling(item.At - seconds)).ToString("0") + " С";
        }
    }

    // Pictures of in-game objects, with a generic vector fallback for custom events.
    // Asset loading is offline; the system and game cursor are never changed.
    internal sealed class AlertGlyph : FrameworkElement
    {
        private readonly string id;
        private string urgency;
        private Color color;
        private readonly System.Windows.Media.Imaging.BitmapSource gameIcon;
        internal AlertGlyph(string eventId, bool urgent, double size) : this(eventId, urgent ? "Danger" : "Warning", size) { }
        internal AlertGlyph(string eventId, string state, double size)
        {
            id = eventId; gameIcon = GameIcons.Get(eventId); SetState(state, AlertChip.PhaseColor(state)); Width = Height = size;
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            IsHitTestVisible = false;
        }
        internal void SetState(string state, Color ink)
        {
            if (urgency == state && color == ink) return;
            urgency = state; color = ink; InvalidateVisual();
        }
        private static string Shape(string name)
        {
            switch (name)
            {
                case "boxes": return "M4,4 L20,4 20,19 4,19 Z M4,4 L20,19 M20,4 L4,19";
                case "tunnels": return "M3,19 L3,10 C3,1 21,1 21,10 L21,19 M8,19 L8,10 16,10 16,19 Z M8,10 L16,19 M16,10 L8,19";
                case "midboxes": return "M4,7 L20,7 20,19 4,19 Z M4,7 L20,19 M20,7 L4,19 M7,4 L7,1 12,4 17,1 17,4";
                case "runes": return "M12,2 L21,11 12,20 3,11 Z M12,6 L12,16 M8,11 L16,11";
                case "machines": return "M5,2 L19,2 19,20 5,20 Z M8,5 L16,5 16,10 8,10 Z M8,14 L10,14 M14,13 L16,13 M8,18 L16,18";
                case "small": case "medium": case "large": return "M4,10 L2,3 8,7 L16,7 22,3 20,10 19,18 5,18 Z M7,11 L9,12 M15,12 L17,11";
                case "early": return "M4,10 L2,3 8,7 L16,7 22,3 20,10 19,18 5,18 Z M12,9 L12,14 15,14";
                case "rift": return "M16,2 C3,-1 1,19 11,20 C22,23 24,4 16,2 M14,5 L10,11 15,11 10,17";
                case "midboss": return "M5,7 L2,2 9,5 15,5 22,2 19,7 20,14 16,19 8,19 4,14 Z M7,10 L10,12 M14,12 L17,10 M10,16 L14,16";
                case "lane": return "M5,18 L5,10 C5,1 19,1 19,10 L19,18 M3,10 L21,10 M8,14 L10,14 M14,14 L16,14";
                case "snacks": return "M4,5 L20,5 20,19 4,19 Z M12,8 L12,16 M8,12 L16,12";
                default: return "M12,2 C24,2 24,20 12,20 C0,20 0,2 12,2 M12,5 L12,11 16,14";
            }
        }
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            dc.PushTransform(new ScaleTransform(ActualWidth / 24, ActualHeight / 24));
            bool danger = urgency == "Danger";
            var ink = new SolidColorBrush(color); ink.Freeze();
            var shadow = new Pen(new SolidColorBrush(Color.FromArgb(170, 15, 13, 10)), 3.5) { LineJoin = PenLineJoin.Round };
            var pen = new Pen(ink, 1.65) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (gameIcon != null)
            {
                double factor = Math.Min(22.0 / gameIcon.PixelWidth, 20.5 / gameIcon.PixelHeight);
                double width = gameIcon.PixelWidth * factor, height = gameIcon.PixelHeight * factor;
                var bounds = new Rect((24 - width) / 2, (20.5 - height) / 2, width, height);
                if (id == "rift") dc.PushClip(new RectangleGeometry(bounds, 2, 2));
                dc.DrawImage(gameIcon, bounds);
                if (id == "rift") dc.Pop();
                string badge = id == "tunnels" ? "Т" : id == "midboxes" ? "М" : id == "small" ? "1" :
                    id == "medium" ? "2" : id == "large" ? "3" : id == "early" ? "Р" : null;
                if (badge != null)
                {
                    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(225, 18, 17, 15)), null, new Rect(16, 13, 8, 8), 1, 1);
                    var mark = new FormattedText(badge, System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, new Typeface("Consolas"), 8, ink, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                    dc.DrawText(mark, new Point(17, 12));
                }
            }
            else
            {
                Geometry shape = Geometry.Parse(Shape(id)); shape.Freeze();
                dc.DrawGeometry(null, shadow, shape);
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(20, color.R, color.G, color.B)), pen, shape);
            }
            // A check marks the active phase, independent of the green color.
            if (urgency == "Active")
            {
                dc.DrawLine(pen, new Point(9, 21.5), new Point(11, 23));
                dc.DrawLine(pen, new Point(11, 23), new Point(15, 20.5));
            }
            else if (urgency != "Ink") dc.DrawEllipse(ink, null, new Point(danger ? 10 : 12, 22.3), 1, 1);
            if (danger) dc.DrawEllipse(ink, null, new Point(14, 22.3), 1, 1);
            dc.Pop();
        }
    }

    public sealed partial class Controller
    {
        private Window cursorOverlay;
        private StackPanel cursorIcons;
        private double bannerWidth = 1120, bannerFont = 48;
        private TextBlock cursorStatus;
        private AlertMotionFrame cursorMotion;
        private Border cursorBackdrop;
        private readonly DispatcherTimer cursorTick = new DispatcherTimer(DispatcherPriority.Background);
        private List<Occurrence> cursorItems = new List<Occurrence>();
        private readonly Dictionary<string, AlertChip> cursorViews = new Dictionary<string, AlertChip>();
        private double previewUntil, previewStarted;
        private double lastRaise;
        private double lastCursorPosition = double.NegativeInfinity;

        private void InitializeCursor()
        {
            cursorIcons = new StackPanel { Orientation = Orientation.Vertical, Background = Brushes.Transparent, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            cursorStatus = new TextBlock { Text = "Ожидание матча", FontFamily = new FontFamily("Segoe UI"), FontSize = 10, Foreground = (Brush)app.FindResource("Ink"), TextWrapping = TextWrapping.NoWrap };
            var content = new StackPanel { IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            content.Children.Add(cursorStatus); content.Children.Add(cursorIcons);
            // A feathered scrim keeps the headline readable over a bright scene.
            cursorBackdrop = new Border {
                Background = new LinearGradientBrush(new GradientStopCollection { new GradientStop(Color.FromArgb(0,18,16,13),0), new GradientStop(Color.FromArgb(180,18,16,13),.18), new GradientStop(Color.FromArgb(180,18,16,13),.82), new GradientStop(Color.FromArgb(0,18,16,13),1) }, new Point(0,.5), new Point(1,.5)),
                BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(3), Padding = new Thickness(20, 7, 20, 7),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false, Child = content
            };
            cursorMotion = new AlertMotionFrame(cursorBackdrop);
            cursorOverlay = new Window {
                Title = "Deadlock Tempo — предупреждения", WindowStyle = WindowStyle.None,
                AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight, ShowInTaskbar = false,
                ShowActivated = false, Topmost = true, Focusable = false, IsHitTestVisible = false,
                Content = cursorMotion, UseLayoutRounding = true, SnapsToDevicePixels = true
            };
            cursorOverlay.SourceInitialized += delegate { Native.OverlayStyle(cursorOverlay, false); };
            cursorTick.Interval = TimeSpan.FromMilliseconds(33);
            cursorTick.Tick += delegate {
                double now = VisionReader.Now;
                if (now - lastCursorPosition >= .032)
                {
                    PositionStatusOverlay(); PositionAtCrosshair(); lastCursorPosition = now;
                }
                if (cursorOverlay.IsVisible)
                {
                    foreach (var view in cursorViews.Values) view.Advance(now, SystemParameters.ClientAreaAnimation);
                    cursorMotion.Advance(now, SystemParameters.ClientAreaAnimation);
                }
                // Faster drawing only during transitions; native placement keeps its old cadence.
                var interval = TimeSpan.FromMilliseconds(cursorOverlay.IsVisible && cursorMotion.IsMoving ? 16 : 33);
                if (cursorTick.Interval != interval) cursorTick.Interval = interval;
            };
        }

        private void BindCursorControls()
        {
            Main<CheckBox>("CursorMode").IsChecked = settings.CursorMode;
            Main<CheckBox>("CursorMode").Checked += delegate { SetCursorMode(true); };
            Main<CheckBox>("CursorMode").Unchecked += delegate { SetCursorMode(false); };
            Main<CheckBox>("AutoStartGame").IsChecked = smokeReport == null && GameAutoStart.Enabled;
            Main<CheckBox>("AutoStartGame").Checked += delegate { ChangeAutoStart(true); };
            Main<CheckBox>("AutoStartGame").Unchecked += delegate { ChangeAutoStart(false); };
            Main<Slider>("BannerTextSize").Value = settings.BannerTextSize;
            Main<Slider>("BannerOpacity").Value = settings.BannerOpacity;
            Main<Slider>("BannerTop").Value = settings.BannerTop;
            Main<Slider>("BannerTextSize").ValueChanged += delegate { settings.BannerTextSize = Main<Slider>("BannerTextSize").Value; UpdateCursorAppearance(); Update(true); SaveSoon(); };
            Main<Slider>("BannerOpacity").ValueChanged += delegate { settings.BannerOpacity = Main<Slider>("BannerOpacity").Value; UpdateCursorAppearance(); SaveSoon(); };
            Main<Slider>("BannerTop").ValueChanged += delegate { settings.BannerTop = Main<Slider>("BannerTop").Value; UpdateCursorAppearance(); PositionAtCrosshair(); SaveSoon(); };
            Click("PreviewCursor", delegate {
                Main<CheckBox>("CursorMode").IsChecked = true;
                settings.ShowOverlay = true; previewStarted = VisionReader.Now; previewUntil = previewStarted + 40;
                ApplyOverlayMode(); PositionAtCrosshair(); Update(true);
                Status("Проба на 40 секунд: жёлтый → красный → ВРЕМЯ ПРИШЛО. Часы матча не меняются.");
            });
        }

        private void SetCursorMode(bool value)
        {
            settings.CursorMode = value; previewUntil = 0;
            SetEditing(false); ApplyOverlayMode(); UpdateCursorAppearance(); Update(true); SaveSoon();
        }
        private void ApplyOverlayMode()
        {
            if (settings.ShowOverlay && !settings.CursorMode) overlay.Show(); else overlay.Hide();
            if (!settings.ShowOverlay || !settings.CursorMode) cursorOverlay.Hide();
        }
        private void UpdateCursorAppearance()
        {
            cursorOverlay.Opacity = settings.BannerOpacity;
            Main<TextBlock>("BannerTextSizeValue").Text = settings.BannerTextSize.ToString("0") + " px";
            Main<TextBlock>("BannerOpacityValue").Text = (settings.BannerOpacity * 100).ToString("0") + "%";
            Main<TextBlock>("BannerTopValue").Text = settings.BannerTop.ToString("0") + "%";
            Main<TextBlock>("OverlayModeHint").Text = settings.CursorMode
                ? "Крупный текст сверху по центру: жёлтый за 30 секунд, красный за 10, подтверждение на 10 секунд."
                : "Сводная панель со всеми таймингами.";
        }
        private void ConfigureBanner(Rect region, int count)
        {
            bannerWidth = Math.Min(1180, Math.Min(region.Width * .82, region.Height * .85));
            bannerFont = settings.BannerTextSize * Math.Max(.6, Math.Min(1.25, region.Height / 1440));
            if (count > 2) bannerFont *= Math.Max(.45, 2.0 / count);
        }
        private void RefreshCursorAlerts(List<Occurrence> items, double seconds)
        {
            cursorItems = CursorAlerts.CanShow(settings.ShowOverlay, settings.CursorMode, clock.Running,
                matchEnded, settings.AutoVision, visionState == "СИНХРОНИЗИРОВАНО")
                ? CursorAlerts.Select(items, seconds) : new List<Occurrence>();
            UpdateCursorStatus();
            if (previewUntil <= VisionReader.Now) RenderCursor(cursorItems, seconds);
            PositionAtCrosshair();
        }
        private void ChangeAutoStart(bool enabled)
        {
            if (smokeReport != null) return;
            try { GameAutoStart.SetEnabled(enabled); Status(enabled ? "Автозапуск включён: обычный запуск Deadlock откроет Tempo без переключения из игры." : "Автозапуск отключён. Tempo можно открывать вручную."); }
            catch (Exception ex) { Status("Не удалось изменить автозапуск: " + ex.Message); }
        }
        private void UpdateCursorStatus() { cursorStatus.Visibility = Visibility.Collapsed; }
        private void RenderCursor(List<Occurrence> items, double seconds)
        {
            var visible = items.Take(CursorAlerts.Limit).ToList();
            var keys = new HashSet<string>(visible.Select(x => x.Key));
            foreach (var key in cursorViews.Keys.Where(x => !keys.Contains(x)).ToList())
            {
                cursorIcons.Children.Remove(cursorViews[key]); cursorViews.Remove(key);
            }
            // Keep each visual alive across numeric updates: a second ticking is not a transition.
            int index = 0;
            string changedPhase = null;
            string displayedPhase = "Ink";
            foreach (var item in visible)
            {
                AlertChip view;
                if (!cursorViews.TryGetValue(item.Key, out view))
                    cursorViews[item.Key] = view = new AlertChip(item.Item.Id, bannerFont);
                int transitions = view.TransitionCount;
                view.SetLayout(bannerWidth);
                view.Update(item, seconds, VisionReader.Now, SystemParameters.ClientAreaAnimation, bannerFont);
                if (view.Phase == "Danger" || (displayedPhase != "Danger" && view.Phase == "Active") ||
                    (displayedPhase == "Ink" && view.Phase == "Warning")) displayedPhase = view.Phase;
                if (view.TransitionCount != transitions && (changedPhase == null || view.Phase == "Danger" || (view.Phase == "Active" && changedPhase != "Danger")))
                    changedPhase = view.Phase;
                if (cursorIcons.Children.IndexOf(view) != index)
                {
                    cursorIcons.Children.Remove(view); cursorIcons.Children.Insert(index, view);
                }
                index++;
            }
            while (cursorIcons.Children.Count > index) cursorIcons.Children.RemoveAt(index);
            if (items.Count > CursorAlerts.Limit)
                cursorIcons.Children.Add(new TextBlock { Text = "+" + (items.Count - CursorAlerts.Limit), FontFamily = new FontFamily("Consolas"), FontSize = Math.Max(10, settings.IconSize * .65), Foreground = (Brush)app.FindResource("Warning"), VerticalAlignment = VerticalAlignment.Center });
            if (visible.Count == 0 || displayedPhase == "Ink") cursorMotion.Reset();
            else if (changedPhase != null) cursorMotion.Trigger(displayedPhase, VisionReader.Now, SystemParameters.ClientAreaAnimation);
            else cursorMotion.Synchronize(displayedPhase, VisionReader.Now, SystemParameters.ClientAreaAnimation);
            cursorOverlay.UpdateLayout();
        }
        private void PositionAtCrosshair()
        {
            if (shuttingDown || smokeReport != null) return;
            bool preview = previewUntil > VisionReader.Now;
            if (!preview && previewUntil != 0) { previewUntil = 0; RenderCursor(cursorItems, clock.Seconds); UpdateCursorStatus(); }
            if (!settings.ShowOverlay || !settings.CursorMode) { cursorOverlay.Hide(); return; }
            Rect screen;
            if (preview)
            {
                if (Native.GetForegroundWindow() != new WindowInteropHelper(main).Handle) { previewUntil = 0; RenderCursor(cursorItems, clock.Seconds); cursorOverlay.Hide(); return; }
                double elapsed = VisionReader.Now - previewStarted;
                UpdateCursorStatus();
                var samples = new[] { new Occurrence { Item = new Timing { Id = "runes", Name = "Руны" }, At = 30, End = 30 } };
                RenderCursor(CursorAlerts.Select(samples, elapsed), elapsed);
                var area = Forms.Screen.FromHandle(new WindowInteropHelper(main).Handle).Bounds;
                screen = new Rect(area.X, area.Y, area.Width, area.Height);
            }
            else
            {
                IntPtr game; System.Drawing.Rectangle bounds;
                if ((cursorItems.Count == 0) || !Native.GameBounds(out game, out bounds)) { cursorOverlay.Hide(); return; }
                screen = new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            }
            var source = PresentationSource.FromVisual(main);
            Matrix transform = source != null ? source.CompositionTarget.TransformFromDevice : Matrix.Identity;
            var region = new Rect(transform.Transform(screen.TopLeft), transform.Transform(screen.BottomRight));
            double previousWidth = bannerWidth, previousFont = bannerFont;
            ConfigureBanner(region, preview ? 1 : cursorItems.Count);
            if (previousWidth != bannerWidth || previousFont != bannerFont) {
                if (!preview) RenderCursor(cursorItems, clock.Seconds);
                else foreach (var view in cursorViews.Values) view.SetLayout(bannerWidth);
            }
            cursorOverlay.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Point position = CursorAlerts.Place(cursorOverlay.DesiredSize, region, 0, settings.BannerTop);
            cursorOverlay.Left = position.X; cursorOverlay.Top = position.Y;
            bool hasContent = cursorIcons.Children.Count != 0;
            if (hasContent && !cursorOverlay.IsVisible) { cursorOverlay.Show(); lastRaise = 0; }
            if (hasContent && VisionReader.Now - lastRaise > 1) { Native.KeepOnTop(cursorOverlay); lastRaise = VisionReader.Now; }
            if (!hasContent) cursorOverlay.Hide();
        }

        private void RunCursorSmoke() { RunAttentionSmoke(); }
    }
}

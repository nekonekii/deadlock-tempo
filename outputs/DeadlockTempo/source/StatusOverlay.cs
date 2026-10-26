using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace DeadlockTempo
{
    internal static class StatusBadge
    {
        internal static string Label(bool alertsEnabled, bool automatic, string vision, bool running, bool ended, double seconds)
        {
            string state;
            if (!alertsEnabled) state = "ТАЙМЕРЫ СКРЫТЫ";
            else if (ended) state = "МАТЧ ЗАВЕРШЁН";
            else if (vision == "OCR НЕДОСТУПЕН") state = "ОШИБКА ЧАСОВ";
            else if (automatic && vision == "ПАУЗА НА ЧАСАХ ИГРЫ") state = "ПАУЗА";
            else if (automatic && vision != "СИНХРОНИЗИРОВАНО") state = "ОЖИДАЮ ЧАСЫ";
            else if (!automatic) state = running ? "РУЧНОЙ " + Times.Format(seconds) : "РУЧНОЙ · ПАУЗА";
            else state = "РАБОТАЕТ " + Times.Format(seconds);
            return "TEMPO · " + state;
        }

        internal static Point Place(Size size, Rect game)
        {
            // Under the game's performance statistics, away from the top-center team HUD.
            return new Point(Math.Max(game.Left + 8, game.Right - size.Width - 12),
                Math.Min(game.Bottom - size.Height - 8, game.Top + Math.Max(24, game.Height * .115)));
        }
    }

    public sealed partial class Controller
    {
        private Window statusOverlay;
        private TextBlock statusLabel;
        private Ellipse statusDot;
        private double statusLastRaise;

        private void InitializeStatusOverlay()
        {
            statusDot = new Ellipse { Width = 4, Height = 4, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            statusLabel = new TextBlock { FontFamily = new FontFamily("Segoe UI"), FontSize = 10, FontWeight = FontWeights.Normal,
                Foreground = (Brush)app.FindResource("Ink"), TextWrapping = TextWrapping.NoWrap };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Opacity = .8 };
            row.Children.Add(statusDot); row.Children.Add(statusLabel);
            var surface = new Border { Background = (Brush)app.FindResource("CompactBackdrop"), CornerRadius = new CornerRadius(2),
                Padding = new Thickness(5, 2, 5, 2), Child = row,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            statusOverlay = new Window {
                Title = "Deadlock Tempo — статус", WindowStyle = WindowStyle.None, AllowsTransparency = true,
                Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight,
                ShowInTaskbar = false, ShowActivated = false, Topmost = true, Focusable = false, IsHitTestVisible = false,
                UseLayoutRounding = true, SnapsToDevicePixels = true, Content = surface
            };
            statusOverlay.SourceInitialized += delegate { Native.OverlayStyle(statusOverlay, false); };
            UpdateStatusOverlayText();
        }

        private void UpdateStatusOverlayText()
        {
            if (statusOverlay == null) return;
            statusLabel.Text = StatusBadge.Label(settings.ShowOverlay, settings.AutoVision, visionState, clock.Running, matchEnded, clock.Seconds);
            bool synchronized = !settings.AutoVision || visionState == "СИНХРОНИЗИРОВАНО";
            string color = visionState == "OCR НЕДОСТУПЕН" ? "Danger" :
                !settings.ShowOverlay || matchEnded ? "Muted" : synchronized ? "StatusReady" : "Warning";
            statusDot.Fill = (Brush)app.FindResource(color);
            System.Windows.Automation.AutomationProperties.SetName(statusOverlay, statusLabel.Text);
        }

        private void PositionStatusOverlay()
        {
            // This window is independent of warning selection, OCR validity and the show-timers toggle.
            if (shuttingDown || smokeReport != null) return;
            IntPtr game; System.Drawing.Rectangle bounds;
            if (!Native.GameBounds(out game, out bounds)) { statusOverlay.Hide(); return; }
            var source = PresentationSource.FromVisual(main);
            Matrix transform = source != null ? source.CompositionTarget.TransformFromDevice : Matrix.Identity;
            var region = new Rect(transform.Transform(new Point(bounds.Left, bounds.Top)), transform.Transform(new Point(bounds.Right, bounds.Bottom)));
            statusOverlay.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Point position = StatusBadge.Place(statusOverlay.DesiredSize, region);
            statusOverlay.Left = position.X; statusOverlay.Top = position.Y;
            if (!statusOverlay.IsVisible) { statusOverlay.Show(); statusLastRaise = 0; }
            if (VisionReader.Now - statusLastRaise >= 1) { Native.KeepOnTop(statusOverlay); statusLastRaise = VisionReader.Now; }
        }

        // Focus-preserving, off-screen integration check; it never opens the main control panel.
        private void RunStatusSmoke()
        {
            try
            {
                Tests.Check(!main.IsVisible, "status test keeps the control panel hidden");
                main.Close();
                Tests.Check(!shuttingDown && tick.IsEnabled && new WindowInteropHelper(main).Handle != IntPtr.Zero,
                    "closing the control panel preserves the overlay, clock and hotkey window");
                settings.AlwaysVisible = false; settings.ShowOverlay = true; settings.AutoVision = true;
                clock.Reset(); matchEnded = false; visionState = "ОЖИДАНИЕ МАТЧА";
                UpdateStatusOverlayText();
                statusOverlay.Left = -16000; statusOverlay.Top = -16000;
                IntPtr foreground = Native.GetForegroundWindow();
                statusOverlay.Show(); statusOverlay.UpdateLayout();
                Tests.Check(Native.GetForegroundWindow() == foreground, "status never takes input focus");
                long mask = Native.Layered | Native.Transparent | Native.NoActivate | Native.ToolWindow;
                Tests.Check((Native.GetStyle(new WindowInteropHelper(statusOverlay).Handle) & mask) == mask, "status is transparent to clicks and cannot activate");
                Tests.Check(statusOverlay.IsVisible && statusLabel.Text.Contains("ОЖИДАЮ ЧАСЫ") && cursorItems.Count == 0, "status remains visible with no warnings and no synchronized clock");
                Tests.Render(statusOverlay, Path.ChangeExtension(smokeReport, ".waiting.png"));
                visionState = "СИНХРОНИЗИРОВАНО"; clock.Set(421); clock.Start(); UpdateStatusOverlayText();
                Tests.Check(statusLabel.Text == "TEMPO · РАБОТАЕТ 07:01", "synchronized status includes the live clock");
                Tests.Check(((FrameworkElement)statusOverlay.Content).ActualHeight <= 19 && statusLabel.FontSize == 10, "status remains a tiny single line");
                Tests.Render(statusOverlay, Path.ChangeExtension(smokeReport, ".ready.png"));
                visionState = "НЕТ СИНХРОНИЗАЦИИ"; clock.Pause(); UpdateStatusOverlayText();
                Tests.Check(statusOverlay.IsVisible && statusLabel.Text.Contains("ОЖИДАЮ ЧАСЫ"), "OCR loss changes text instead of hiding status");
                visionState = "OCR НЕДОСТУПЕН"; UpdateStatusOverlayText();
                Tests.Check(statusOverlay.IsVisible && statusLabel.Text.Contains("ОШИБКА ЧАСОВ"), "OCR failure remains visible");
                Tests.Render(statusOverlay, Path.ChangeExtension(smokeReport, ".error.png"));
                settings.ShowOverlay = false; UpdateStatusOverlayText();
                Tests.Check(statusOverlay.IsVisible && statusLabel.Text.Contains("ТАЙМЕРЫ СКРЫТЫ"), "global timer hide preserves the independent status");
                settings.ShowOverlay = true; matchEnded = true; UpdateStatusOverlayText();
                Tests.Check(statusOverlay.IsVisible && statusLabel.Text.Contains("МАТЧ ЗАВЕРШЁН"), "end of match retains status");
                foreach (var area in new[] { new Rect(0, 0, 3440, 1440), new Rect(-1920, 0, 1920, 1080), new Rect(80, 60, 1280, 720) })
                {
                    var size = new Size(180, 18);
                    Tests.Check(area.Contains(new Rect(StatusBadge.Place(size, area), size)), "status follows game bounds including a second monitor");
                }
                RunAttentionSmoke();
                main.ShowActivated = false; main.WindowStartupLocation = WindowStartupLocation.Manual;
                main.Left = -16000; main.Top = -16000;
                Main<TabControl>("Tabs").SelectedIndex = 2;
                main.Show(); main.UpdateLayout();
                Tests.Check(Native.GetForegroundWindow() == foreground, "settings preview does not interrupt the game");
                Tests.Render(main, Path.ChangeExtension(smokeReport, ".settings.png"));
                main.Hide();
                File.WriteAllText(smokeReport, "PASS: persistent status; closing panel keeps overlay alive; click-through and focus preservation; large centered text at 3440x1440, 1920x1080, 1280x720 and negative monitor origins; yellow/red/active transitions, simultaneous events, stable text during animation, reduced motion, expiry and OCR gating. Respawn tracking has been removed; first-spawn schedule is retained.");
                ExitCode = 0;
            }
            catch (Exception ex) { ExitCode = 1; File.WriteAllText(smokeReport, ex.ToString()); }
            finally { Shutdown(); }
        }
    }
}

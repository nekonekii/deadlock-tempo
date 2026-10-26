using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

[assembly: AssemblyTitle("Deadlock Tempo")]
[assembly: AssemblyDescription("External screen-synchronized match timer")]
[assembly: AssemblyVersion("1.13.3.0")]

namespace DeadlockTempo
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e) {
                GameAutoStart.Log("Unhandled failure: " + e.ExceptionObject);
            };
            if (args.Length == 1 && args[0] == "--watch") return GameAutoStart.Run();
            if (args.Length == 1 && args[0] == "--enable-autostart") { GameAutoStart.SetEnabled(true); return 0; }
            if (args.Length == 1 && args[0] == "--disable-autostart") { GameAutoStart.SetEnabled(false); return 0; }
            if (args.Length == 1 && args[0] == "--stop-watcher") { GameAutoStart.StopWatcher(); return 0; }
            if (args.Length == 2 && args[0] == "--test") return Tests.Run(args[1]);
            if (args.Length == 3 && args[0] == "--vision-test") return VisionTests.Run(args[1], args[2]);
            bool statusOnly = args.Length == 2 && args[0] == "--status-smoke-test";
            bool smoke = statusOnly || (args.Length == 2 && (args[0] == "--smoke-test" || args[0] == "--auto-smoke-test"));
            bool automatic = statusOnly || (args.Length == 1 && args[0] == "--game-auto") || (smoke && args[0] == "--auto-smoke-test");
            bool created;
            using (var instance = new Mutex(true, "Local\\DeadlockTempo.v1" + (smoke ? ".test" : ""), out created))
            {
                if (!created) { if (!automatic) GameAutoStart.RequestPanel(); return 0; }
                try
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.Resources = (ResourceDictionary)XamlReader.Parse(Storage.Resource("Theme.xaml"));
                    GameFonts.Load(app);
                    var controller = new Controller(app, smoke ? args[1] : null, automatic, statusOnly);
                    app.Run();
                    return controller.ExitCode;
                }
                catch (Exception ex)
                {
                    if (smoke) File.WriteAllText(args[1], ex.ToString());
                    else {
                        GameAutoStart.Log("Overlay failed: " + ex);
                        if (!automatic) MessageBox.Show("Не удалось запустить Tempo:\n" + ex.Message, "Deadlock Tempo", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    return 1;
                }
            }
        }
    }

    public sealed partial class Controller
    {
        private readonly Application app;
        private readonly Window main, overlay;
        private readonly MatchClock clock = new MatchClock();
        private readonly DispatcherTimer tick = new DispatcherTimer();
        private readonly DispatcherTimer saveDelay = new DispatcherTimer();
        private readonly DispatcherTimer visionTick = new DispatcherTimer();
        private readonly DispatcherTimer lifetimeTick = new DispatcherTimer();
        private EventWaitHandle panelRequest;
        private readonly bool automaticLaunch;
        private readonly IntPtr initialForeground;
        private int gameMissingTicks;
        private readonly VisionReader vision = new VisionReader();
        private readonly MatchDetector detector = new MatchDetector();
        private bool visionBusy, matchEnded;
        private int visionGeneration, visionFailures;
        private double? appliedOcr;
        private string visionState = "ОЖИДАНИЕ МАТЧА";
        private readonly string smokeReport;
        private Profile profile;
        private Settings settings;
        private ObservableCollection<Timing> draft;
        private Forms.NotifyIcon tray;
        private HwndSource hwndSource;
        private readonly List<int> registered = new List<int>();
        private bool editing, shuttingDown, loading = true, activeMatch;
        private double previousSeconds;
        private int lastSecond = -1, monitorIndex;
        public int ExitCode { get; private set; }

        public Controller(Application application, string smoke, bool automatic, bool statusOnly = false)
        {
            app = application; smokeReport = smoke; automaticLaunch = automatic; initialForeground = Native.GetForegroundWindow();
            string warning = LoadData();
            if (automatic) { settings.ShowOverlay = true; settings.AutoVision = true; }
            main = (Window)XamlReader.Parse(Storage.Resource("MainWindow.xaml"));
            overlay = (Window)XamlReader.Parse(Storage.Resource("Overlay.xaml"));
            InitializeCursor();
            InitializeStatusOverlay();
            app.MainWindow = main;
            // Fit the control window on small displays; the content panels scroll.
            main.Width = Math.Min(main.Width, SystemParameters.WorkArea.Width - 24);
            main.Height = Math.Min(main.Height, SystemParameters.WorkArea.Height - 24);
            main.MinWidth = Math.Min(main.MinWidth, main.Width);
            main.MinHeight = Math.Min(main.MinHeight, main.Height);
            BindControls();
            main.SourceInitialized += delegate { SetupHotkeys(); };
            overlay.SourceInitialized += delegate { Native.OverlayStyle(overlay, false); };
            overlay.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { if (editing) { overlay.DragMove(); SaveSoon(); } };
            overlay.LocationChanged += delegate { if (editing) SaveSoon(); };
            main.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) {
                if (!shuttingDown) { e.Cancel = true; SetEditing(false); main.Hide(); }
            };
            main.StateChanged += delegate { if (main.WindowState == WindowState.Minimized && editing) SetEditing(false); };
            tick.Interval = TimeSpan.FromMilliseconds(200);
            tick.Tick += delegate { Update(false); };
            saveDelay.Interval = TimeSpan.FromMilliseconds(500);
            saveDelay.Tick += delegate { saveDelay.Stop(); SaveSettings(); };
            SystemEvents.PowerModeChanged += OnPower;
            SystemEvents.DisplaySettingsChanged += OnDisplay;
            SetupTray();
            if (automatic) new WindowInteropHelper(main).EnsureHandle(); else main.Show();
            // Hidden WPF windows need native handles before measurement/positioning.
            new WindowInteropHelper(overlay).EnsureHandle();
            new WindowInteropHelper(cursorOverlay).EnsureHandle();
            new WindowInteropHelper(statusOverlay).EnsureHandle();
            UpdateAppearance();
            RestorePosition();
            ApplyOverlayMode();
            loading = false;
            ReloadDraft();
            Update(true);
            SaveSoon();
            tick.Start();
            if (smokeReport == null) cursorTick.Start();
            visionTick.Interval = TimeSpan.FromMilliseconds(350);
            visionTick.Tick += async delegate { await ReadVision(); };
            if (smokeReport == null) visionTick.Start();
            if (smokeReport == null)
            {
                GameAutoStart.Log("Overlay started v1.13.3; automatic=" + automaticLaunch);
                panelRequest = new EventWaitHandle(false, EventResetMode.AutoReset, GameAutoStart.PanelEvent);
                lifetimeTick.Interval = TimeSpan.FromSeconds(1);
                lifetimeTick.Tick += delegate {
                    if (panelRequest.WaitOne(0)) ShowMain();
                    if (automaticLaunch && GameAutoStart.GameKey().Length == 0) { if (++gameMissingTicks >= 5) Shutdown(); }
                    else gameMissingTicks = 0;
                };
                lifetimeTick.Start();
            }
            if (!string.IsNullOrEmpty(warning)) Status(warning);
            if (smokeReport != null) app.Dispatcher.BeginInvoke(new Action(statusOnly ? (Action)RunStatusSmoke : RunSmoke), DispatcherPriority.ApplicationIdle);
        }

        private T Main<T>(string name) where T : FrameworkElement { return (T)main.FindName(name); }
        private T Hud<T>(string name) where T : FrameworkElement { return (T)overlay.FindName(name); }
        private void Click(string name, Action action) { Main<Button>(name).Click += delegate { action(); }; }
        private void Status(string text) { Main<TextBlock>("Status").Text = text; }

        private string LoadData()
        {
            profile = Storage.DefaultProfile(); settings = new Settings();
            if (smokeReport != null) return null;
            var errors = new List<string>();
            string path = Path.Combine(Storage.Folder, "profile.json");
            if (File.Exists(path))
            {
                try { var candidate = Storage.Json().Deserialize<Profile>(File.ReadAllText(path)); string error = ScheduleEngine.Validate(candidate); if (error != null) throw new InvalidDataException(error); profile = candidate; }
                catch (Exception ex) { errors.Add("Профиль не прочитан; используется встроенный. Исходный файл сохранён. " + ex.Message); }
            }
            if (Storage.UpgradeObjectives(profile))
            {
                try { Storage.Write(path, profile); }
                catch (Exception ex) { errors.Add("Обновление событий действует до перезапуска, но не сохранено: " + ex.Message); }
            }
            path = Path.Combine(Storage.Folder, "settings.json");
            if (File.Exists(path))
            {
                try
                {
                    settings = Storage.Json().Deserialize<Settings>(File.ReadAllText(path));
                    if (settings == null || !Finite(settings.Scale) || !Finite(settings.Opacity)) throw new InvalidDataException("Недопустимые параметры.");
                    settings.Scale = Math.Max(.8, Math.Min(1.5, settings.Scale)); settings.Opacity = Math.Max(.6, Math.Min(1, settings.Opacity));
                    settings.IconSize = Finite(settings.IconSize) ? Math.Max(12, Math.Min(36, settings.IconSize)) : 20;
                    settings.IconOpacity = Finite(settings.IconOpacity) ? Math.Max(.15, Math.Min(1, settings.IconOpacity)) : .70;
                    settings.CrosshairOffsetX = Finite(settings.CrosshairOffsetX) ? Math.Max(5, Math.Min(20, settings.CrosshairOffsetX)) : 8;
                    settings.CrosshairOffsetY = Finite(settings.CrosshairOffsetY) ? Math.Max(-20, Math.Min(20, settings.CrosshairOffsetY)) : -7;
                    if (!Finite(settings.Left) || !Finite(settings.Top)) { settings.Left = double.NaN; settings.Top = double.NaN; }
                }
                catch { settings = new Settings(); errors.Add("Настройки восстановлены по умолчанию; исходный файл оставлен на диске."); }
            }
            if (settings.BannerLayoutVersion < 1) {
                settings.CursorMode = true; settings.AlwaysVisible = false;
                settings.BannerLayoutVersion = 1;
            }
            settings.BannerTextSize = Finite(settings.BannerTextSize) ? Math.Max(32, Math.Min(64, settings.BannerTextSize)) : 48;
            settings.BannerOpacity = Finite(settings.BannerOpacity) ? Math.Max(.7, Math.Min(1, settings.BannerOpacity)) : 1;
            settings.BannerTop = Finite(settings.BannerTop) ? Math.Max(15, Math.Min(25, settings.BannerTop)) : 17;
            return string.Join(" ", errors);
        }
        private static bool Finite(double v) { return !double.IsNaN(v) && !double.IsInfinity(v); }

        private void BindControls()
        {
            Click("Start", ToggleClock);
            Click("Reset", delegate { ManualMode(); clock.Reset(); activeMatch = false; matchEnded = false; previousSeconds = 0; Main<TextBox>("SyncTime").Text = "00:00"; Update(true); Status("Матч сброшен. Включите автоопределение для захвата новых часов игры."); });
            Click("Sync", SyncClock);
            Main<TextBox>("SyncTime").KeyDown += delegate(object s, KeyEventArgs e) { if (e.Key == Key.Enter) { SyncClock(); e.Handled = true; } };
            Click("Minus", delegate { ManualMode(); clock.Set(clock.Seconds - 1); previousSeconds = clock.Seconds; Update(true); });
            Click("Plus", delegate { ManualMode(); clock.Set(clock.Seconds + 1); previousSeconds = clock.Seconds; Update(true); });
            Click("ToggleOverlay", ToggleOverlay);
            Click("MoveOverlay", delegate { if (settings.CursorMode) { Main<TabControl>("Tabs").SelectedIndex = 2; return; } if (!overlay.IsVisible) { overlay.Show(); settings.ShowOverlay = true; } SetEditing(!editing); });
            Click("HidePanel", delegate { SetEditing(false); main.Hide(); Status("Панель скрыта. Ctrl + Alt + F10 вернёт её."); });
            Click("ApplyProfile", ApplyProfile);
            Click("Defaults", delegate { draft = new ObservableCollection<Timing>(Storage.DefaultProfile().Events); Main<DataGrid>("ScheduleGrid").ItemsSource = draft; Main<TextBlock>("ProfileError").Text = "Исходный профиль загружен в редактор. Нажмите «Сохранить расписание», чтобы применить."; });
            Click("AddEvent", delegate { if (draft.Count >= 80) { Main<TextBlock>("ProfileError").Text = "Лимит: 80 событий."; return; } var item = new Timing { Id = Guid.NewGuid().ToString("N"), Name = "Новое событие", First = "00:00", Enabled = false, Note = "Пользовательское событие" }; draft.Add(item); Main<DataGrid>("ScheduleGrid").SelectedItem = item; Main<DataGrid>("ScheduleGrid").ScrollIntoView(item); });
            Main<DataGrid>("ScheduleGrid").SelectionChanged += delegate { var item = Main<DataGrid>("ScheduleGrid").SelectedItem as Timing; Main<TextBlock>("EventNote").Text = item == null ? "Выберите строку, чтобы увидеть пояснение." : item.Note + (string.IsNullOrWhiteSpace(item.Source) ? "" : "\nИсточник: " + item.Source); };
            Main<Slider>("Scale").Value = settings.Scale;
            Main<Slider>("Opacity").Value = settings.Opacity;
            Main<CheckBox>("Sound").IsChecked = settings.Sound;
            BindCursorControls();
            Main<CheckBox>("AutoVision").IsChecked = settings.AutoVision;
            Main<CheckBox>("AutoVision").Checked += delegate { SetAutoVision(true); };
            Main<CheckBox>("AutoVision").Unchecked += delegate { SetAutoVision(false); };
            Main<Slider>("Scale").ValueChanged += delegate { settings.Scale = Main<Slider>("Scale").Value; UpdateAppearance(); ClampPosition(); SaveSoon(); };
            Main<Slider>("Opacity").ValueChanged += delegate { settings.Opacity = Main<Slider>("Opacity").Value; UpdateAppearance(); SaveSoon(); };
            Main<CheckBox>("Sound").Checked += delegate { settings.Sound = true; SaveSoon(); };
            Main<CheckBox>("Sound").Unchecked += delegate { settings.Sound = false; SaveSoon(); };
            Click("PosLeft", delegate { Place("left"); }); Click("PosRight", delegate { Place("right"); }); Click("PosCenter", delegate { Place("center"); });
            Click("NextScreen", delegate { monitorIndex = (monitorIndex + 1) % Forms.Screen.AllScreens.Length; Place("right"); Status("Оверлей перемещён: " + Forms.Screen.AllScreens[monitorIndex].DeviceName); });
            Click("SourceResearch", delegate { OpenUrl("https://www.dltimes.com/articles/city-never-sleeps-spawn-timers"); });
            Click("SourceCrates", delegate { OpenUrl("https://deadlock.wiki/Crates"); });
            Main<TextBlock>("DataLocation").Text = "Локальные настройки: " + Storage.Folder + "\nПри работе нет сетевых запросов. Ссылки открываются только по нажатию.";
        }

        private void OpenUrl(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception ex) { Status("Не удалось открыть ссылку: " + ex.Message); } }
        private void ToggleClock()
        {
            ManualMode(); matchEnded = false;
            if (clock.Running) { clock.Pause(); Status("Пауза. Возобновите часы одновременно с игрой."); }
            else { clock.Start(); activeMatch = true; Status("Таймер идёт. Ctrl + Alt + F8 — пауза."); }
            previousSeconds = clock.Seconds; Update(true);
        }
        private void SyncClock()
        {
            double seconds;
            if (!Times.Parse(Main<TextBox>("SyncTime").Text, out seconds)) { Status("Введите время в формате ММ:СС, например 07:35 (секунды 00–59)."); Main<TextBox>("SyncTime").Focus(); return; }
            ManualMode(); matchEnded = false;
            clock.Set(seconds); previousSeconds = seconds; activeMatch = seconds > 0 || clock.Running; Update(true);
            Status("Время установлено: " + Times.Format(seconds) + (clock.Running ? ". Таймер продолжает идти." : ". Нажмите «Продолжить», когда часы игры идут."));
        }
        private void ToggleOverlay()
        {
            SetEditing(false);
            settings.ShowOverlay = !settings.ShowOverlay; previewUntil = 0;
            ApplyOverlayMode(); Update(true); SaveSoon();
        }
        private void ShowMain()
        {
            main.Show(); main.WindowState = WindowState.Normal; main.Activate();
        }
        private void SetEditing(bool value)
        {
            editing = value;
            if (new WindowInteropHelper(overlay).Handle != IntPtr.Zero) Native.OverlayStyle(overlay, value);
            Main<Button>("MoveOverlay").Content = settings.CursorMode ? "Настроить текст" : editing ? "Зафиксировать положение" : "Настроить положение";
            Hud<TextBlock>("OverlayFooter").Text = editing ? "ПЕРЕТАЩИТЕ ПАНЕЛЬ · ЗАТЕМ ЗАФИКСИРУЙТЕ" : "30 СЕК · ЖЁЛТЫЙ / 10 СЕК · КРАСНЫЙ";
            if (editing) Status("Режим перемещения: перетащите оверлей, затем нажмите «Зафиксировать положение».");
            else ClampPosition();
            SaveSoon(); Update(true);
        }
        private void ReloadDraft()
        {
            draft = new ObservableCollection<Timing>(Storage.Copy(profile).Events);
            Main<DataGrid>("ScheduleGrid").ItemsSource = draft;
            Main<TextBlock>("ProfileCaption").Text = profile.Name + " · " + profile.EvidenceDate;
            Main<TextBlock>("EventNote").Text = "Выберите строку, чтобы увидеть пояснение и источник.";
        }
        private void ApplyProfile()
        {
            var grid = Main<DataGrid>("ScheduleGrid");
            if (!grid.CommitEdit(DataGridEditingUnit.Cell, true) || !grid.CommitEdit(DataGridEditingUnit.Row, true)) { Main<TextBlock>("ProfileError").Text = "Завершите редактирование строки."; return; }
            var candidate = new Profile { Name = profile.Name, EvidenceDate = profile.EvidenceDate, ObjectiveEventsVersion = profile.ObjectiveEventsVersion, Events = draft.ToList() };
            string error = ScheduleEngine.Validate(candidate);
            if (error != null) { Main<TextBlock>("ProfileError").Text = error; return; }
            try
            {
                if (smokeReport == null) Storage.Write(Path.Combine(Storage.Folder, "profile.json"), candidate);
                profile = Storage.Copy(candidate); previousSeconds = clock.Seconds;
                Main<TextBlock>("ProfileError").Text = "Расписание сохранено и применено."; Update(true);
            }
            catch (Exception ex) { Main<TextBlock>("ProfileError").Text = "Не удалось сохранить: " + ex.Message; }
        }

        private void UpdateAppearance()
        {
            var card = Hud<Border>("OverlayCard");
            card.Background = new SolidColorBrush(Color.FromArgb((byte)(255 * settings.Opacity), 27, 25, 21));
            card.LayoutTransform = new ScaleTransform(settings.Scale, settings.Scale);
            Main<TextBlock>("ScaleValue").Text = (settings.Scale * 100).ToString("0") + "%";
            Main<TextBlock>("OpacityValue").Text = (settings.Opacity * 100).ToString("0") + "%";
            UpdateCursorAppearance();
            overlay.UpdateLayout();
        }
        private Rect ScreenRect(Forms.Screen screen)
        {
            var area = screen.WorkingArea;
            var source = PresentationSource.FromVisual(main);
            Matrix transform = source != null ? source.CompositionTarget.TransformFromDevice : Matrix.Identity;
            return new Rect(transform.Transform(new Point(area.Left, area.Top)), transform.Transform(new Point(area.Right, area.Bottom)));
        }
        private Size OverlaySize()
        {
            overlay.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return new Size(Math.Max(356 * settings.Scale, overlay.DesiredSize.Width), Math.Max(120, overlay.DesiredSize.Height));
        }
        private void RestorePosition()
        {
            var screens = Forms.Screen.AllScreens;
            monitorIndex = Array.FindIndex(screens, x => x.Primary); if (monitorIndex < 0) monitorIndex = 0;
            if (Finite(settings.Left) && Finite(settings.Top)) { overlay.Left = settings.Left; overlay.Top = settings.Top; ClampPosition(); }
            else Place("right");
        }
        private void Place(string side)
        {
            var screens = Forms.Screen.AllScreens; monitorIndex = Math.Max(0, Math.Min(monitorIndex, screens.Length - 1));
            Rect r = ScreenRect(screens[monitorIndex]); Size size = OverlaySize();
            overlay.Left = side == "left" ? r.Left + 22 : side == "center" ? r.Left + (r.Width - size.Width) / 2 : r.Right - size.Width - 22;
            overlay.Top = r.Top + 100; ClampPosition(); SaveSoon();
        }
        private void ClampPosition()
        {
            if (!Finite(overlay.Left) || !Finite(overlay.Top)) return;
            var screens = Forms.Screen.AllScreens; int found = Array.FindIndex(screens, s => ScreenRect(s).Contains(new Point(overlay.Left + 20, overlay.Top + 20)));
            if (found >= 0) monitorIndex = found;
            monitorIndex = Math.Max(0, Math.Min(monitorIndex, screens.Length - 1));
            Rect r = ScreenRect(screens[monitorIndex]); Size size = OverlaySize();
            overlay.Left = Math.Max(r.Left, Math.Min(overlay.Left, r.Right - size.Width));
            overlay.Top = Math.Max(r.Top, Math.Min(overlay.Top, r.Bottom - size.Height));
        }
        private void SaveSoon() { if (!loading && !shuttingDown && smokeReport == null) { saveDelay.Stop(); saveDelay.Start(); } }
        private void SaveSettings()
        {
            if (smokeReport != null) return;
            settings.Left = overlay.Left; settings.Top = overlay.Top;
            try { Storage.Write(Path.Combine(Storage.Folder, "settings.json"), settings); }
            catch (Exception ex) { Status("Настройки действуют, но не сохранены: " + ex.Message); }
        }

        private void ManualMode() { if (settings.AutoVision) Main<CheckBox>("AutoVision").IsChecked = false; }
        private void SetAutoVision(bool enabled)
        {
            settings.AutoVision = enabled; visionGeneration++; detector.Reset(); appliedOcr = null; visionFailures = 0;
            visionState = enabled ? "ОЖИДАНИЕ МАТЧА" : "РУЧНЫЕ ЧАСЫ";
            if (enabled) { clock.Pause(); matchEnded = false; }
            Main<TextBlock>("VisionDetail").Text = enabled ? "Вернитесь в Deadlock. Захват работает только при активном окне игры." : "Чтение экрана отключено. Управляйте часами вручную.";
            SaveSoon(); Update(true);
        }
        private async Task ReadVision()
        {
            if (visionBusy || !settings.AutoVision || shuttingDown) return;
            visionBusy = true; int generation = visionGeneration;
            try
            {
                VisionObservation observation = await Task.Run(() => vision.ReadScreen());
                if (shuttingDown || !settings.AutoVision || generation != visionGeneration) return;
                visionFailures = 0;
                var decision = detector.Observe(observation);
                visionState = decision.State; matchEnded = decision.Ended;
                Main<TextBlock>("VisionDetail").Text = observation.Detail;
                if (decision.Time.HasValue)
                {
                    double time = decision.Time.Value;
                    if (appliedOcr != time || !decision.Run || !clock.Running)
                        clock.Set(time + (decision.Run ? Math.Min(1, VisionReader.Now - observation.Stamp) : 0));
                    appliedOcr = time; activeMatch = !decision.Ended;
                }
                if (decision.Run) clock.Start(); else clock.Pause();
                Update(true);
            }
            catch (Exception ex)
            {
                if (shuttingDown || generation != visionGeneration) return;
                clock.Pause(); visionState = "OCR НЕДОСТУПЕН";
                Main<TextBlock>("VisionDetail").Text = "Чтение экрана не удалось: " + ex.Message;
                if (++visionFailures >= 3) { Main<CheckBox>("AutoVision").IsChecked = false; Status("Авточтение остановлено после трёх ошибок. " + ex.Message); }
                Update(true);
            }
            finally { visionBusy = false; }
        }

        private void Update(bool force)
        {
            double seconds = clock.Seconds; int second = (int)Math.Floor(seconds);
            if (!force && second == lastSecond) return;
            lastSecond = second;
            Main<TextBlock>("ClockText").Text = Times.Format(seconds); Hud<TextBlock>("OverlayClock").Text = Times.Format(seconds);
            Main<TextBlock>("ClockState").Text = settings.AutoVision ? visionState : clock.Running ? "ВРЕМЯ МАТЧА · ИДЁТ" : activeMatch ? "ВРЕМЯ МАТЧА · ПАУЗА" : "МАТЧ НЕ НАЧАТ";
            Main<Button>("Start").Content = clock.Running ? "Пауза" : activeMatch ? "Продолжить" : "Начать матч";
            Main<Button>("ToggleOverlay").Content = settings.ShowOverlay ? "Скрыть оверлей" : "Показать оверлей";
            Hud<TextBlock>("OverlayState").Text = editing ? "ПЕРЕМЕЩЕНИЕ · КЛИКИ ЗАНЯТЫ ПАНЕЛЬЮ" : settings.AutoVision ? "◈ " + visionState : clock.Running ? "РУЧНЫЕ ЧАСЫ · МАТЧ ИДЁТ" : "РУЧНЫЕ ЧАСЫ · ПАУЗА";
            bool uncertain = settings.AutoVision && visionState != "СИНХРОНИЗИРОВАНО" && !matchEnded;
            Hud<TextBlock>("OverlayState").Foreground = (Brush)app.FindResource(uncertain ? "Warning" : "Muted");
            Hud<StackPanel>("OverlayEvents").Opacity = uncertain ? .60 : 1;
            List<Occurrence> upcoming = Upcoming(seconds);
            UpdateStatusOverlayText();
            RenderEvents(Main<StackPanel>("Upcoming"), upcoming.Take(8), seconds, false);
            RenderOverlay(upcoming, seconds);
            RefreshCursorAlerts(upcoming, seconds);
            if (!editing) { overlay.UpdateLayout(); ClampPosition(); }
            if (settings.Sound && clock.Running && seconds >= previousSeconds && seconds - previousSeconds <= 2 && upcoming.Any(x => previousSeconds < x.At - AlertTiming.WarningSeconds && seconds >= x.At - AlertTiming.WarningSeconds)) System.Media.SystemSounds.Asterisk.Play();
            previousSeconds = seconds;
        }

        private List<Occurrence> Upcoming(double seconds)
        {
            return ScheduleEngine.Upcoming(profile, seconds);
        }

        private void RenderEvents(StackPanel target, IEnumerable<Occurrence> items, double seconds, bool compact)
        {
            target.Children.Clear();
            var list = items.ToList();
            if (list.Count == 0) { target.Children.Add(new TextBlock { Text = "Впереди нет событий.\nИзмените расписание или начните новый матч.", FontSize = 13, Foreground = (Brush)app.FindResource("Muted"), Margin = new Thickness(0, 10, 0, 12) }); return; }
            foreach (var occurrence in list)
            {
                string urgency = ScheduleEngine.Urgency(occurrence, seconds);
                var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(compact ? 68 : 92) });
                var texts = new StackPanel { Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                texts.Children.Add(new TextBlock { Text = occurrence.Item.Name, FontSize = compact ? 12 : 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                string hint = occurrence.Reminder ? "НАПОМИНАНИЕ · ПРОВЕРИТЬ КАРТУ" : occurrence.At <= seconds ? (occurrence.Window ? "ОКНО ОТКРЫТО" : "ПОЯВЛЕНИЕ ПО РАСПИСАНИЮ") : urgency == "Danger" ? "ДО 10 СЕКУНД" : urgency == "Warning" ? "СКОРО · ДО 30 СЕКУНД" : occurrence.Detail(seconds);
                texts.Children.Add(new TextBlock { Text = hint, FontSize = compact ? 9 : 11, Foreground = (Brush)app.FindResource(urgency == "Ink" ? "Muted" : urgency), Margin = new Thickness(0, 3, 0, 0) });
                grid.Children.Add(texts);
                var countdown = new TextBlock { Text = occurrence.Countdown(seconds), FontFamily = new FontFamily("Consolas"), FontSize = compact ? (seconds >= occurrence.At ? 13 : 21) : 22, FontWeight = FontWeights.Bold, Foreground = (Brush)app.FindResource(urgency), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, TextWrapping = TextWrapping.NoWrap };
                Grid.SetColumn(countdown, 1); grid.Children.Add(countdown);
                var border = new Border { Child = grid, BorderThickness = new Thickness(urgency == "Ink" ? 1 : 4, 0, 0, 0), BorderBrush = (Brush)app.FindResource(urgency == "Ink" ? "Line" : urgency), Padding = compact ? new Thickness(9, 6, 4, 7) : new Thickness(15, 12, 15, 12), Margin = new Thickness(0, 0, 0, compact ? 3 : 7), CornerRadius = new CornerRadius(1), Background = urgency == "Danger" ? new SolidColorBrush(Color.FromArgb(70, 150, 40, 30)) : urgency == "Warning" ? new SolidColorBrush(Color.FromArgb(45, 220, 170, 65)) : compact ? Brushes.Transparent : (Brush)app.FindResource("Panel"), ToolTip = occurrence.Item.Note };
                AutomationProperties.SetName(border, occurrence.Item.Name + ", " + occurrence.Countdown(seconds));
                target.Children.Add(border);
            }
        }

        private void RenderOverlay(List<Occurrence> items, double seconds)
        {
            var root = Hud<StackPanel>("OverlayEvents"); root.Children.Clear();
            if (matchEnded)
            {
                root.Children.Add(new TextBlock { Text = "МАТЧ ЗАВЕРШЁН", FontFamily = (FontFamily)app.FindResource("DisplayFont"), FontSize = 25, Foreground = (Brush)app.FindResource("Accent"), Margin = new Thickness(0, 12, 0, 12) });
                Hud<TextBlock>("OverlayMore").Text = "Ожидаю часы следующего матча"; return;
            }
            var periodic = items.Where(x => !string.IsNullOrWhiteSpace(x.Item.Every) && !x.Item.RepeatReminder).ToList();
            var first = items.Where(x => string.IsNullOrWhiteSpace(x.Item.Every) && !x.Window).ToList();
            var windows = items.Where(x => x.Window).ToList();
            var reminders = items.Where(x => x.Reminder).ToList();
            AddGroup(root, "ПО РАСПИСАНИЮ", periodic.Take(3), seconds);
            AddGroup(root, "ПЕРВОЕ ПОЯВЛЕНИЕ", first.Take(8), seconds);
            AddGroup(root, "ОКНО ПОЯВЛЕНИЯ", windows.Take(2), seconds);
            AddGroup(root, "НАПОМИНАНИЯ · ПРОВЕРИТЬ КАРТУ", reminders.Take(2), seconds);
            int shown = Math.Min(3, periodic.Count) + Math.Min(8, first.Count) + Math.Min(2, windows.Count) + Math.Min(2, reminders.Count);
            int completed = profile.Events.Count(x => x.Enabled && string.IsNullOrWhiteSpace(x.Every) && !items.Any(o => o.Item.Id == x.Id));
            Hud<TextBlock>("OverlayMore").Text = (items.Count > shown ? "+ ещё " + (items.Count - shown) + " в панели · " : "") + (completed > 0 ? "✓ Прошло первых появлений: " + completed : "Все включённые события в поле зрения");
        }
        private void AddGroup(StackPanel root, string title, IEnumerable<Occurrence> values, double seconds)
        {
            var list = values.ToList(); if (list.Count == 0) return;
            root.Children.Add(new TextBlock { Text = title, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = (Brush)app.FindResource("Accent"), Margin = new Thickness(0, 12, 0, 7) });
            var section = new StackPanel(); RenderEvents(section, list, seconds, true); root.Children.Add(section);
        }

        private void SetupHotkeys()
        {
            IntPtr hwnd = new WindowInteropHelper(main).Handle; hwndSource = HwndSource.FromHwnd(hwnd); hwndSource.AddHook(WindowMessage);
            var failed = new List<string>();
            for (int i = 0; i < 3; i++) { if (Native.RegisterHotKey(hwnd, 100 + i, 0x1 | 0x2 | 0x4000, (uint)(0x77 + i))) registered.Add(100 + i); else failed.Add("Ctrl + Alt + F" + (8 + i)); }
            Main<TextBlock>("HotkeyStatus").Text = failed.Count == 0 ? "Сочетания зарегистрированы в Windows." : "Заняты: " + string.Join(", ", failed) + ". Используйте кнопки или значок в трее.";
        }
        private IntPtr WindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x312) { int id = wParam.ToInt32(); if (id == 100) ToggleClock(); if (id == 101) ToggleOverlay(); if (id == 102) { if (main.IsVisible && main.IsActive) { SetEditing(false); main.Hide(); } else ShowMain(); } handled = true; }
            return IntPtr.Zero;
        }
        private void SetupTray()
        {
            tray = new Forms.NotifyIcon { Text = "Deadlock Tempo · таймер матча", Icon = System.Drawing.SystemIcons.Application, Visible = smokeReport == null };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Панель управления", null, delegate { app.Dispatcher.Invoke(new Action(ShowMain)); });
            menu.Items.Add("Старт / пауза", null, delegate { app.Dispatcher.Invoke(new Action(ToggleClock)); });
            menu.Items.Add("Показать / скрыть оверлей", null, delegate { app.Dispatcher.Invoke(new Action(ToggleOverlay)); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Выход и отключить автозапуск", null, delegate { app.Dispatcher.Invoke(new Action(delegate {
                GameAutoStart.SetEnabled(false); Shutdown();
            })); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { app.Dispatcher.Invoke(new Action(ShowMain)); };
        }
        private void OnPower(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend) app.Dispatcher.BeginInvoke(new Action(delegate { clock.Pause(); Update(true); Status("Компьютер переходил в сон. Проверьте время и возобновите таймер вручную."); }));
        }
        private void OnDisplay(object sender, EventArgs e) { app.Dispatcher.BeginInvoke(new Action(delegate { ClampPosition(); SaveSoon(); })); }
        private void Shutdown()
        {
            if (shuttingDown) return; shuttingDown = true;
            if (smokeReport == null) GameAutoStart.Log("Overlay shutting down; game=" + GameAutoStart.GameKey());
            tick.Stop(); cursorTick.Stop(); visionTick.Stop(); lifetimeTick.Stop(); visionGeneration++; saveDelay.Stop(); SaveSettings();
            if (panelRequest != null) panelRequest.Dispose();
            SystemEvents.PowerModeChanged -= OnPower; SystemEvents.DisplaySettingsChanged -= OnDisplay;
            IntPtr hwnd = new WindowInteropHelper(main).Handle;
            foreach (int id in registered) Native.UnregisterHotKey(hwnd, id);
            if (hwndSource != null) hwndSource.RemoveHook(WindowMessage);
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            statusOverlay.Close(); cursorOverlay.Close(); overlay.Close(); app.Shutdown(ExitCode);
        }

        private void RunSmoke()
        {
            try
            {
                if (automaticLaunch)
                {
                    Tests.Check(!main.IsVisible, "automatic launch keeps the control panel hidden");
                    Tests.Check(Native.GetForegroundWindow() == initialForeground, "automatic launch preserves game focus");
                    main.Show();
                }
                overlay.Show();
                long mask = Native.Layered | Native.Transparent | Native.NoActivate | Native.ToolWindow;
                IntPtr hwnd = new WindowInteropHelper(overlay).Handle;
                Tests.Check((Native.GetStyle(hwnd) & mask) == mask, "overlay has layered, transparent, no-activate and tool-window styles");
                SetEditing(true); Tests.Check((Native.GetStyle(hwnd) & Native.Transparent) == 0, "editing enables mouse interaction");
                SetEditing(false); Tests.Check((Native.GetStyle(hwnd) & mask) == mask, "locking restores click-through");
                IntPtr before = Native.GetForegroundWindow(); overlay.Hide(); overlay.Show(); Tests.Check(Native.GetForegroundWindow() == before, "showing overlay preserves foreground window");
                clock.Set(270); activeMatch = true; Update(true);
                Tests.Check(Hud<TextBlock>("OverlayClock").Text == "04:30", "HUD follows match clock");
                Tests.Check(Hud<StackPanel>("OverlayEvents").Children.Count == 6, "HUD shows periodic, first-spawn and window groups together");
                Main<TextBox>("SyncTime").Text = "08:75"; SyncClock(); Tests.Check((int)clock.Seconds == 270, "invalid sync does not mutate clock");
                Main<TextBox>("SyncTime").Text = "07:42"; SyncClock(); Tests.Check((int)clock.Seconds == 462, "valid sync updates clock");
                var first = draft[0]; first.First = "broken"; ApplyProfile(); Tests.Check(profile.Events[0].First == "02:00", "invalid draft does not alter live profile");
                ReloadDraft(); ApplyProfile();
                Tests.Check(profile.ObjectiveEventsVersion == 1 && !Storage.UpgradeObjectives(profile), "editing the schedule retains the completed objective migration");
                ReloadDraft();
                clock.Reset(); activeMatch = false; Main<TextBox>("SyncTime").Text = "00:00"; Update(true); Status("Готово. Запустите таймер в момент 00:00 на часах матча.");
                main.UpdateLayout(); overlay.UpdateLayout();
                Tests.Render(main, Path.ChangeExtension(smokeReport, ".control.png"));
                Tests.Render(overlay, Path.ChangeExtension(smokeReport, ".overlay.png"));
                clock.Set(150); Update(true); overlay.UpdateLayout();
                Tests.Render(overlay, Path.ChangeExtension(smokeReport, ".yellow.png"));
                clock.Set(170); Update(true); overlay.UpdateLayout();
                Tests.Render(overlay, Path.ChangeExtension(smokeReport, ".red.png"));
                Tests.Check(overlay.Left + overlay.ActualWidth <= ScreenRect(Forms.Screen.AllScreens[monitorIndex]).Right + 1, "overlay stays inside screen after width change");
                matchEnded = true; Update(true); overlay.UpdateLayout();
                Tests.Render(overlay, Path.ChangeExtension(smokeReport, ".ended.png"));
                RunCursorSmoke();
                File.WriteAllText(smokeReport, "PASS: UI loaded; native overlay styles; move/lock; HUD; sync validation; profile isolation; large centered announcements, click-through, focus preservation, simultaneous categories, stable text during repeated contours, red/active transitions, reduced motion, expiry and renders.\n" + Main<TextBlock>("HotkeyStatus").Text);
                ExitCode = 0;
            }
            catch (Exception ex) { ExitCode = 1; File.WriteAllText(smokeReport, ex.ToString()); }
            finally { Shutdown(); }
        }
    }
}

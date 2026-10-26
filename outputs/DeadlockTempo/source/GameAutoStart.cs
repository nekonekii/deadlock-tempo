using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace DeadlockTempo
{
    internal sealed class GameLaunchPolicy
    {
        private readonly Func<double> now;
        private string lastGame;
        private double retryAfter;
        internal GameLaunchPolicy() : this(() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency) { }
        internal GameLaunchPolicy(Func<double> time) { now = time; }
        internal bool NeedsLaunch(string game, bool overlayRunning)
        {
            if (string.IsNullOrEmpty(game)) { lastGame = null; retryAfter = 0; return false; }
            if (lastGame != game) { lastGame = game; retryAfter = 0; }
            if (overlayRunning) { retryAfter = 0; return false; }
            return now() >= retryAfter;
        }
        internal void Launched(string game) { lastGame = game; retryAfter = now() + 5; }
    }

    // A separate ordinary user process watches process names only. It does not
    // open the game for memory access, inject anything, or change Steam's launch options.
    internal static class GameAutoStart
    {
        internal const string PanelEvent = "Local\\DeadlockTempo.ShowPanel.v1";
        private const string StopEvent = "Local\\DeadlockTempo.WatcherStop.v1";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunName = "DeadlockTempo";
        private static string EnabledFile { get { return Path.Combine(Storage.Folder, "autostart.enabled"); } }
        internal static string Executable
        {
            get {
                string installed = Path.Combine(Storage.Folder, "App", "DeadlockTempo.exe");
                return File.Exists(installed) ? installed : Assembly.GetExecutingAssembly().Location;
            }
        }
        internal static bool Enabled
        {
            get {
                if (File.Exists(EnabledFile)) return File.ReadAllText(EnabledFile).Trim() == "true";
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    string value = key == null ? null : key.GetValue(RunName) as string;
                    return !string.IsNullOrEmpty(value) && value.EndsWith(" --watch", StringComparison.Ordinal);
                }
            }
        }
        internal static void SetEnabled(bool enabled)
        {
            Directory.CreateDirectory(Storage.Folder);
            File.WriteAllText(EnabledFile, enabled ? "true" : "false");
            string executable = Executable;
            bool scheduled = false;
            try { executable = PersistentStartup.Configure(enabled, executable, EnabledFile); scheduled = enabled; }
            catch (Exception ex) { Log("Windows recovery task could not be configured: " + ex); }
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) key.SetValue(RunName, "\"" + executable + "\" --watch", RegistryValueKind.String);
                else key.DeleteValue(RunName, false);
            }
            if (enabled && !scheduled) Start("--watch");
            if (!enabled) StopWatcher();
        }
        internal static void StopWatcher()
        {
            try { using (var signal = EventWaitHandle.OpenExisting(StopEvent)) signal.Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
        }
        internal static void RequestPanel()
        {
            try { using (var signal = EventWaitHandle.OpenExisting(PanelEvent)) signal.Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
        }
        internal static bool OverlayRunning()
        {
            Mutex instance;
            if (!Mutex.TryOpenExisting("Local\\DeadlockTempo.v1", out instance)) return false;
            instance.Dispose(); return true;
        }
        internal static string GameKey()
        {
            // Enumerating names/IDs uses the OS process list, not game data.
            var games = Process.GetProcessesByName("deadlock");
            try { return string.Join(",", games.Select(x => x.Id).OrderBy(x => x)); }
            finally { foreach (var game in games) game.Dispose(); }
        }
        private static void Start(string argument)
        {
            using (var child = Process.Start(new ProcessStartInfo(Executable, argument) {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(Executable)
            })) { }
        }
        internal static void Log(string message)
        {
            try {
                Directory.CreateDirectory(Storage.Folder);
                string path = Path.Combine(Storage.Folder, "autostart.log");
                if (File.Exists(path) && new FileInfo(path).Length > 32000) File.WriteAllText(path, "");
                File.AppendAllText(path, DateTimeOffset.Now.ToString("o") + " [" + Process.GetCurrentProcess().Id + "] " + message + Environment.NewLine);
            } catch { }
        }
        [STAThread]
        internal static int Run()
        {
            bool created;
            using (var single = new Mutex(true, "Local\\DeadlockTempo.Watcher.v1", out created))
            {
                if (!created) return 0;
                using (var stop = new EventWaitHandle(false, EventResetMode.AutoReset, StopEvent))
                using (var timer = new Forms.Timer { Interval = 2000 })
                using (var tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "Tempo · автозапуск с Deadlock", Visible = true })
                using (var menu = new Forms.ContextMenuStrip())
                using (var context = new Forms.ApplicationContext())
                {
                    var policy = new GameLaunchPolicy();
                    Action show = delegate { if (OverlayRunning()) RequestPanel(); else Start("--show"); };
                    menu.Items.Add("Открыть Tempo", null, delegate { show(); });
                    menu.Items.Add("Отключить автозапуск", null, delegate { SetEnabled(false); context.ExitThread(); });
                    tray.ContextMenuStrip = menu; tray.DoubleClick += delegate { show(); };
                    Action poll = delegate {
                        try {
                            if (stop.WaitOne(0) || !Enabled) { context.ExitThread(); return; }
                            string game = GameKey();
                            if (policy.NeedsLaunch(game, OverlayRunning()))
                            {
                                policy.Launched(game);
                                Start("--game-auto");
                                Log("Overlay launched for Deadlock PID " + game);
                            }
                        } catch (Exception ex) { Log("Launch check failed: " + ex.Message); }
                    };
                    timer.Tick += delegate { poll(); };
                    Log("Watcher started"); timer.Start();
                    Forms.Application.Run(context);
                    timer.Stop(); tray.Visible = false; Log("Watcher stopped");
                }
            }
            return 0;
        }
    }
}

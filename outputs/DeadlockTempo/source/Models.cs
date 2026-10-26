using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace DeadlockTempo
{
    public sealed class MatchClock
    {
        private readonly Func<double> now;
        private double stored, anchor;
        public bool Running { get; private set; }
        public MatchClock() : this(() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency) { }
        public MatchClock(Func<double> time) { now = time; }
        public double Seconds { get { return Math.Max(0, stored + (Running ? now() - anchor : 0)); } }
        public void Start() { if (!Running) { anchor = now(); Running = true; } }
        public void Pause() { if (Running) { stored = Seconds; Running = false; } }
        public void Set(double seconds) { stored = Math.Max(0, seconds); anchor = now(); }
        public void Reset() { Running = false; Set(0); }
    }

    public sealed class Timing
    {
        public string Id { get; set; }
        public string IconId { get; set; }
        public bool Enabled { get; set; }
        public string Name { get; set; }
        public string First { get; set; }
        public string Until { get; set; }
        public string Every { get; set; }
        public string Note { get; set; }
        public string Source { get; set; }
        public bool Approximate { get; set; }
        // Repeat reminders are prompts to inspect the game, not predicted spawns.
        public bool RepeatReminder { get; set; }
        public string Schedule { get { return (string.IsNullOrWhiteSpace(First) ? "Не подтверждено" : First) + (!string.IsNullOrWhiteSpace(Until) ? "–" + Until : "") + (!string.IsNullOrWhiteSpace(Every) ? (RepeatReminder ? " / напоминать каждые " : " / каждые ") + Every : ""); } }
        public string Mode { get { return RepeatReminder ? "ОКНО + НАПОМИНАНИЯ" : !string.IsNullOrWhiteSpace(Every) ? "ПО РАСПИСАНИЮ" : !string.IsNullOrWhiteSpace(Until) ? "ОКНО ПОЯВЛЕНИЯ" : "ПЕРВОЕ ПОЯВЛЕНИЕ"; } }
    }

    public sealed class Profile
    {
        public string Name { get; set; }
        public string EvidenceDate { get; set; }
        public int ObjectiveEventsVersion { get; set; }
        public List<Timing> Events { get; set; }
    }

    public sealed class Settings
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Scale { get; set; }
        public double Opacity { get; set; }
        public bool Sound { get; set; }
        public bool ShowOverlay { get; set; }
        public bool AutoVision { get; set; }
        public bool CursorMode { get; set; }
        public bool AlwaysVisible { get; set; }
        public double IconSize { get; set; }
        public double IconOpacity { get; set; }
        public int BannerLayoutVersion { get; set; }
        public double BannerTextSize { get; set; }
        public double BannerOpacity { get; set; }
        public double BannerTop { get; set; }
        public double CrosshairOffsetX { get; set; }
        public double CrosshairOffsetY { get; set; }
        public Settings() { BannerTextSize = 48; BannerOpacity = 1; BannerTop = 17; Left = double.NaN; Top = double.NaN; Scale = 1; Opacity = .94; ShowOverlay = true; AutoVision = true; CursorMode = true; AlwaysVisible = false; IconSize = 20; IconOpacity = .70; CrosshairOffsetX = 8; CrosshairOffsetY = -7; }
    }

    public sealed class Occurrence
    {
        public Timing Item { get; set; }
        public double At { get; set; }
        public double End { get; set; }
        public bool Window { get { return End > At; } }
        public bool Reminder { get { double first; return Item.RepeatReminder && Times.Parse(Item.First, out first) && At > first; } }
        public string Key { get { return Item.Id + ":" + At.ToString(CultureInfo.InvariantCulture); } }
        public string Countdown(double seconds)
        {
            if (Window && seconds >= At) return "ОКНО";
            if (seconds >= At) return Reminder ? "ПРОВЕРЬ" : "СЕЙЧАС";
            return Times.Format(Math.Ceiling(At - seconds));
        }
        public string Detail(double seconds)
        {
            if (Reminder) return Times.Format(At) + " · напоминание проверить карту, не время появления";
            if (Window) return Times.Format(At) + "–" + Times.Format(End) + " · ориентир";
            return Times.Format(At) + " · " + (!string.IsNullOrWhiteSpace(Item.Every) ? "по расписанию" : Item.Approximate ? "ориентир" : "первое появление");
        }
    }

    public static class Times
    {
        public static bool Parse(string text, out double seconds)
        {
            seconds = 0;
            if (text == null) return false;
            string[] p = text.Trim().Split(':');
            int m, s;
            if (p.Length != 2 || !int.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out m) || !int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out s) || m < 0 || m > 999 || s < 0 || s > 59) return false;
            seconds = m * 60 + s;
            return true;
        }
        public static string Format(double seconds)
        {
            long s = (long)Math.Max(0, Math.Floor(seconds));
            return (s / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (s % 60).ToString("00", CultureInfo.InvariantCulture);
        }
    }

    internal static class AlertTiming
    {
        internal const int WarningSeconds = 30, DangerSeconds = 10;
        internal static string Urgency(double remaining)
        {
            return remaining <= 0 ? "Ink" : remaining <= DangerSeconds ? "Danger" : remaining <= WarningSeconds ? "Warning" : "Ink";
        }
    }

    public static class ScheduleEngine
    {
        public static string Urgency(Occurrence occurrence, double seconds)
        {
            double remaining = occurrence.At - seconds;
            return AlertTiming.Urgency(remaining);
        }
        public static List<Occurrence> Upcoming(Profile profile, double seconds)
        {
            var result = new List<Occurrence>();
            foreach (Timing t in profile.Events.Where(x => x.Enabled))
            {
                double first, until, every;
                if (!Times.Parse(t.First, out first)) continue;
                Times.Parse(t.Until, out until); Times.Parse(t.Every, out every);
                double at = first;
                double firstEnd = until > first ? until : first;
                if (every > 0 && seconds > firstEnd + 10)
                    at = first + Math.Ceiling((seconds - first - 10) / every) * every;
                double end = at == first ? firstEnd : at;
                if (seconds > end + 10) continue;
                result.Add(new Occurrence { Item = t, At = at, End = end });
            }
            return result.OrderBy(x => x.At).ThenBy(x => x.Item.Id, StringComparer.Ordinal).ToList();
        }

        public static string Validate(Profile p)
        {
            if (p == null || p.Events == null || p.Events.Count == 0 || p.Events.Count > 80) return "В профиле должно быть от 1 до 80 событий.";
            var ids = new HashSet<string>();
            foreach (var t in p.Events)
            {
                if (t == null || string.IsNullOrWhiteSpace(t.Id) || !ids.Add(t.Id)) return "У событий должны быть уникальные идентификаторы.";
                if (string.IsNullOrWhiteSpace(t.Name) || t.Name.Length > 70) return "Укажите название события (до 70 символов).";
                double first = 0, until = 0, every = 0;
                if ((!string.IsNullOrWhiteSpace(t.First) || t.Enabled) && !Times.Parse(t.First, out first)) return t.Name + ": первое появление — в формате ММ:СС.";
                if (!string.IsNullOrWhiteSpace(t.Until) && (!Times.Parse(t.Until, out until) || until <= first)) return t.Name + ": конец окна должен быть позже начала.";
                if (!string.IsNullOrWhiteSpace(t.Every) && (!Times.Parse(t.Every, out every) || every < 30)) return t.Name + ": период должен быть не меньше 00:30.";
                if (until > 0 && every > 0 && !t.RepeatReminder) return t.Name + ": выберите окно или период, не оба сразу.";
                if (t.RepeatReminder && (every <= 0 || until <= first || every <= until - first + 10))
                    return t.Name + ": напоминания должны идти после первого окна; период должен быть длиннее окна и 10 секунд.";
            }
            return null;
        }
    }

    public static class Storage
    {
        public static readonly string Folder = ResolveFolder();
        private static string ResolveFolder()
        {
            string marker = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data-directory.txt");
            if (File.Exists(marker))
            {
                string folder = File.ReadAllText(marker).Trim();
                if (Path.IsPathRooted(folder)) return Path.GetFullPath(folder);
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeadlockTempo");
        }
        public static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 }; }
        public static T Copy<T>(T value) { return Json().Deserialize<T>(Json().Serialize(value)); }
        public static void Write<T>(string path, T value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, Json().Serialize(value), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, path + ".bak"); else File.Move(tmp, path);
        }
        public static string Resource(string name)
        {
            using (var stream = typeof(Storage).Assembly.GetManifestResourceStream(name))
            using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
        }
        public static Profile DefaultProfile() { return Json().Deserialize<Profile>(Resource("profile.json")); }
        internal static bool UpgradeObjectives(Profile profile)
        {
            if (profile.ObjectiveEventsVersion >= 1) return false;
            var defaults = DefaultProfile();
            foreach (string id in new[] { "urn", "rift" })
            {
                var existing = profile.Events.FirstOrDefault(x => x.Id == id);
                var replacement = Copy(defaults.Events.Single(x => x.Id == id));
                if (existing == null) profile.Events.Add(replacement);
                else if (id == "rift" && existing.First == "11:00" && existing.Until == "13:00" && string.IsNullOrWhiteSpace(existing.Every))
                {
                    existing.Every = replacement.Every;
                    existing.RepeatReminder = replacement.RepeatReminder;
                    existing.Approximate = replacement.Approximate;
                    existing.Note = replacement.Note;
                    existing.Source = replacement.Source;
                }
            }
            profile.ObjectiveEventsVersion = 1;
            profile.EvidenceDate = defaults.EvidenceDate;
            return true;
        }
    }
}

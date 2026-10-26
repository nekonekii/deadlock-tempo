using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace DeadlockTempo
{
    // Screen pixels only. No process handles, game APIs, hooks or input synthesis.
    internal sealed class VisionObservation
    {
        internal bool Foreground, Hud, Result;
        internal int? Seconds;
        internal string Detail = "";
        internal string Evidence = "";
        internal double Stamp;
    }

    internal sealed class VisionReader
    {
        private OcrEngine engine;
        internal static double Now { get { return (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency; } }
        internal static Task<T> Await<T>(Windows.Foundation.IAsyncOperation<T> operation)
        {
            var completion = new TaskCompletionSource<T>();
            operation.Completed = (op, status) => {
                try { completion.TrySetResult(op.GetResults()); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { op.Close(); }
            };
            return completion.Task;
        }

        internal async Task<VisionObservation> ReadScreen(string fixture = null)
        {
            Rectangle bounds; IntPtr handle;
            if (!Native.GameBounds(out handle, out bounds))
                return new VisionObservation { Stamp = Now, Detail = "Ожидаю активное окно Deadlock" };
            using (var pixels = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb))
            {
                using (var graphics = Graphics.FromImage(pixels)) graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
                if (Native.GetForegroundWindow() != handle) return new VisionObservation { Stamp = Now, Detail = "Окно игры потеряло фокус" };
                double captured = Now;
                if (fixture != null) pixels.Save(fixture, ImageFormat.Png);
                var observation = await Analyze(pixels);
                // Discard a frame if the user switched away while OCR was running.
                if (Native.GetForegroundWindow() != handle) return new VisionObservation { Stamp = captured, Detail = "Окно игры потеряло фокус" };
                observation.Stamp = captured;
                return observation;
            }
        }

        // Coordinates use a 1440-high reference, centered on the client area.
        // This preserves HUD geometry on 16:9 and 21:9 rather than stretching it.
        internal static Rectangle Region(Bitmap image, double x, double y, double w, double h)
        {
            double scale = image.Height / 1440.0;
            var rect = new Rectangle((int)Math.Round(image.Width / 2.0 + x * scale), (int)Math.Round(y * scale), Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)));
            return Rectangle.Intersect(rect, new Rectangle(0, 0, image.Width, image.Height));
        }

        private static double Fraction(Bitmap image, Rectangle area, Func<Color, bool> condition)
        {
            int n = 0, good = 0;
            int step = Math.Max(1, image.Height / 360);
            for (int y = area.Top; y < area.Bottom; y += step)
                for (int x = area.Left; x < area.Right; x += step) { n++; if (condition(image.GetPixel(x, y))) good++; }
            return n == 0 ? 0 : (double)good / n;
        }
        internal static bool TeamBars(Bitmap image)
        {
            var a = Region(image, -180, 63, 165, 38); var b = Region(image, 15, 63, 165, 38);
            Func<Color, bool> amber = c => c.R > 130 && c.G > 65 && c.G < c.R * .88 && c.B < c.G * .65;
            Func<Color, bool> blue = c => c.B > 105 && c.B > c.R * 1.12 && c.B > c.G * 1.03;
            return (Fraction(image, a, amber) > .22 && Fraction(image, b, blue) > .22) || (Fraction(image, b, amber) > .22 && Fraction(image, a, blue) > .22);
        }
        internal static bool Minimap(Bitmap image)
        {
            var r = Region(image, 720, 860, 520, 520);
            // The red enemy structures and bright allied lane markers corroborate the top HUD.
            return Fraction(image, r, c => c.R > 155 && c.G > 35 && c.G < c.R * .6 && c.B < 95) > .006
                && Fraction(image, r, c => c.G > 135 && c.G > c.R * 1.15 && c.B > 55) > .003;
        }

        internal static int? ParseClock(string text)
        {
            // Never manufacture a colon or infer minutes from an arbitrary digit string.
            text = (text ?? "").Replace(" ", "").Replace("O", "0").Replace("o", "0");
            var match = Regex.Match(text, @"^([0-9]{1,3}):([0-5][0-9])$");
            if (!match.Success) return null;
            int value = int.Parse(match.Groups[1].Value) * 60 + int.Parse(match.Groups[2].Value);
            return value <= 180 * 60 ? (int?)value : null;
        }
        private static string Letters(string s) { return Regex.Replace((s ?? "").ToLowerInvariant(), @"[^a-zа-я0-9]", ""); }
        internal static bool ResultWords(string heading, string button)
        {
            string a = Letters(heading), b = Letters(button);
            bool title = a.Contains("victory") || a.Contains("defeat") || a.Contains("победа") || a.Contains("поражение")
                || a.Contains("n06eaa") || a.Contains("n06ega") || a.Contains("n06eда") || a.Contains("nobeaa");
            bool again = b.Contains("playagain") || b.Contains("игратьснова") || b.Contains("patbchoba") || b.Contains("urpatbchoba");
            return title && again;
        }

        internal async Task<VisionObservation> Analyze(Bitmap image)
        {
            var result = new VisionObservation { Foreground = true, Stamp = Now };
            bool teams = TeamBars(image), map = Minimap(image);
            result.Hud = teams && map;
            if (teams)
            {
                var roi = Region(image, -60, 12, 120, 39);
                string text = await ReadCrop(image, roi, 6, 125);
                result.Seconds = ParseClock(text);
                if (!result.Seconds.HasValue) { text = await ReadCrop(image, roi, 6, 170); result.Seconds = ParseClock(text); }
                result.Detail = image.Width + "×" + image.Height + " · часы: " + (result.Seconds.HasValue ? Times.Format(result.Seconds.Value) : "не прочитаны") + " · команды ✓ · карта " + (map ? "✓" : "—");

            }
            else
            {
                // Two distant text landmarks and absent gameplay HUD are required.
                string title = await ReadCrop(image, Region(image, -350, 80, 700, 200), 2, 0);
                string button = await ReadCrop(image, Region(image, -210, 1225, 420, 95), 2, 0);
                result.Evidence = title + " / " + button;
                result.Result = ResultWords(title, button);
                result.Detail = result.Result ? "Подтверждаю экран результата" : "Игровой HUD не найден · меню / загрузка";
            }
            return result;
        }

        internal async Task<string> ReadCrop(Bitmap image, Rectangle roi, int magnify, int threshold)
        {
            if (roi.Width < 1 || roi.Height < 1) return "";
            using (var crop = image.Clone(roi, PixelFormat.Format32bppArgb))
            {
                if (threshold > 0)
                {
                    for (int y = 0; y < crop.Height; y++) for (int x = 0; x < crop.Width; x++)
                    {
                        Color c = crop.GetPixel(x, y);
                        crop.SetPixel(x, y, Math.Min(c.R, Math.Min(c.G, c.B)) > threshold ? Color.Black : Color.White);
                    }
                }
                double ratio = Math.Min((double)magnify, 2400.0 / Math.Max(crop.Width, crop.Height));
                using (var enlarged = new Bitmap(Math.Max(1, (int)(crop.Width * ratio)), Math.Max(1, (int)(crop.Height * ratio)), PixelFormat.Format32bppArgb))
                using (var bytes = new MemoryStream())
                {
                    using (var graphics = Graphics.FromImage(enlarged)) { graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; graphics.DrawImage(crop, 0, 0, enlarged.Width, enlarged.Height); }
                    enlarged.Save(bytes, ImageFormat.Png);
                    return (await Recognize(bytes.ToArray())).Text;
                }
            }
        }
        internal async Task<OcrResult> Recognize(byte[] png)
        {
            if (engine == null) engine = OcrEngine.TryCreateFromLanguage(new Language("en-US")) ?? OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine == null) throw new InvalidOperationException("Windows OCR недоступен. Включите английское распознавание текста в языковых компонентах Windows или используйте ручные часы.");
            using (var stream = new InMemoryRandomAccessStream())
            {
                using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(png); await Await<uint>(writer.StoreAsync()); await Await<bool>(writer.FlushAsync()); writer.DetachStream();
                }
                stream.Seek(0);
                var decoder = await Await<BitmapDecoder>(BitmapDecoder.CreateAsync(stream));
                using (var bitmap = await Await<SoftwareBitmap>(decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)))
                { return await Await<OcrResult>(engine.RecognizeAsync(bitmap)); }
            }
        }
    }

    internal sealed class VisionDecision
    {
        internal string State;
        internal double? Time;
        internal bool Run, Ended;
    }
    internal sealed class MatchDetector
    {
        private int? candidate, last;
        private int confirmations, resultFrames;
        private double candidateStamp, candidateStart, lastSeen, lastAdvance, resultStart, noHudSince = -1;
        private bool active, ended, advanced, sceneChanged, paused;
        internal void Reset() { candidate = last = null; confirmations = resultFrames = 0; active = ended = advanced = sceneChanged = paused = false; lastSeen = lastAdvance = 0; noHudSince = -1; }
        private VisionDecision HoldLastReading(double stamp, string unavailableState)
        {
            // Clock and presentation share the same short grace interval. Previously
            // Run stayed true while State changed, so one missed frame hid the banner,
            // destroyed its visuals and replayed the entrance on the next good frame.
            bool fresh = active && stamp - lastSeen < 3;
            return new VisionDecision {
                State = fresh ? (paused ? "ПАУЗА НА ЧАСАХ ИГРЫ" : "СИНХРОНИЗИРОВАНО") : unavailableState,
                Run = fresh && !paused, Time = active && (!fresh || paused) ? last : null, Ended = ended
            };
        }
        internal VisionDecision Observe(VisionObservation o)
        {
            if (o.Foreground && o.Result && !o.Hud)
            {
                if (resultFrames++ == 0) resultStart = o.Stamp;
                if (resultFrames >= 3 && o.Stamp - resultStart >= 1.2)
                { active = false; ended = true; candidate = null; confirmations = 0; return new VisionDecision { State = "МАТЧ ЗАВЕРШЁН", Ended = true, Time = last }; }
            }
            else resultFrames = 0;
            if (!o.Foreground || !o.Hud || !o.Seconds.HasValue)
            {
                if (o.Foreground && !o.Hud)
                {
                    if (noHudSince < 0) noHudSince = o.Stamp;
                    if (o.Stamp - noHudSince >= 3) sceneChanged = true;
                }
                else noHudSince = -1;
                candidate = null; confirmations = 0; advanced = false;
                return HoldLastReading(o.Stamp, ended ? "МАТЧ ЗАВЕРШЁН" : active ? "НЕТ СИНХРОНИЗАЦИИ" : "ОЖИДАНИЕ МАТЧА");
            }
            int value = o.Seconds.Value;
            noHudSince = -1;
            bool validStep = candidate.HasValue && value >= candidate.Value && value - candidate.Value <= o.Stamp - candidateStamp + 2 && o.Stamp - candidateStamp < 4;
            if (!validStep) { confirmations = 1; candidateStart = o.Stamp; advanced = false; }
            else { confirmations++; if (value > candidate.Value) advanced = true; }
            candidate = value; candidateStamp = o.Stamp;
            // A new session needs three coherent readings with actual advancement.
            if (!active)
            {
                if (confirmations < 3 || !advanced || o.Stamp - candidateStart < 1.1)
                    return new VisionDecision { State = ended ? "ПРОВЕРЯЮ НОВЫЙ МАТЧ" : "ПОДТВЕРЖДАЮ ЧАСЫ", Ended = ended };
                active = true; ended = false; sceneChanged = paused = false; last = value; lastSeen = lastAdvance = o.Stamp;
                return new VisionDecision { State = "СИНХРОНИЗИРОВАНО", Run = true, Time = value };
            }
            // Reject OCR outliers. Reacquire after obscured HUD; allow a confirmed
            // low clock only after a gap, so a dropped digit cannot reset a match.
            bool newMatch = last.HasValue && value < last.Value - 5 && (value <= 30 || sceneChanged) && o.Stamp - lastSeen >= 3 && confirmations >= 3 && advanced;
            bool plausible = last.HasValue && value >= last.Value && value - last.Value <= o.Stamp - lastSeen + 2;
            if (!plausible && !newMatch)
                return HoldLastReading(o.Stamp, "ПРОВЕРЯЮ ПОКАЗАНИЯ");
            if (newMatch || value != last) { lastAdvance = o.Stamp; last = value; }
            sceneChanged = false;
            lastSeen = o.Stamp;
            paused = o.Stamp - lastAdvance >= 2.5;
            return new VisionDecision { State = paused ? "ПАУЗА НА ЧАСАХ ИГРЫ" : "СИНХРОНИЗИРОВАНО", Run = !paused, Time = value };
        }
    }
}

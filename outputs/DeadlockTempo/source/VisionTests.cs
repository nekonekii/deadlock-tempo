using System;
using System.Drawing;
using System.IO;
using System.Text;

namespace DeadlockTempo
{
    internal static class VisionTests
    {
        private static VisionObservation Frame(double stamp, int? seconds, bool hud, bool result)
        { return new VisionObservation { Foreground = true, Stamp = stamp, Seconds = seconds, Hud = hud, Result = result }; }
        internal static int Unit()
        {
            int count = 0;
            Action<bool, string> check = (ok, name) => { Tests.Check(ok, name); count++; };
            check(VisionReader.ParseClock("2:39") == 159, "OCR minute and seconds");
            check(VisionReader.ParseClock(" 02 : 39 ") == 159, "OCR spaces");
            check(!VisionReader.ParseClock("239").HasValue && !VisionReader.ParseClock("2:99").HasValue, "reject ambiguous clock");
            check(VisionReader.ResultWords("n06eAa", "nrPATb CHOBA"), "Russian result with English OCR");
            check(!VisionReader.ResultWords("Victory", "Settings"), "result needs two landmarks");
            var d = new MatchDetector();
            check(!d.Observe(Frame(0, 0, false, false)).Run, "clock alone is not enough");
            check(!d.Observe(Frame(.7, 0, true, false)).Run, "first frame cannot start");
            check(!d.Observe(Frame(1.4, 0, true, false)).Run, "static zero cannot start");
            var x = d.Observe(Frame(2.1, 1, true, false));
            check(x.Run && x.Time == 1, "advancing clock starts with observed time");
            x = d.Observe(Frame(2.8, 2, true, false)); check(x.Run && x.Time == 2, "normal synchronization");
            x = d.Observe(Frame(3.5, 82, true, false)); check(!x.Time.HasValue, "reject large OCR jump");
            x = d.Observe(Frame(4.2, 3, true, false)); check(x.Time == 3, "recover after OCR outlier");
            d.Observe(Frame(4.9, 3, true, false)); d.Observe(Frame(5.6, 3, true, false));
            x = d.Observe(Frame(7, 3, true, false)); check(!x.Run && x.Time == 3, "game pause detected");
            x = d.Observe(Frame(7.7, 4, true, false)); check(x.Run, "resume after pause");
            x = d.Observe(new VisionObservation { Stamp = 8.4 }); check(!x.Ended, "alt-tab is not match end");
            x = d.Observe(new VisionObservation { Stamp = 12 }); check(!x.Run && x.Time == 4, "lost HUD freezes at last read clock");
            x = d.Observe(Frame(12.7, 9, true, false)); check(x.Run && x.Time == 9, "reacquire after alt-tab");
            x = d.Observe(Frame(13.4, null, false, true)); check(!x.Ended, "single result frame cannot end");
            d.Observe(Frame(14.1, null, false, true)); x = d.Observe(Frame(14.8, null, false, true));
            check(x.Ended && !x.Run, "confirmed result ends match");
            d.Observe(Frame(20, 0, true, false)); d.Observe(Frame(20.7, 1, true, false)); x = d.Observe(Frame(21.4, 1, true, false));
            check(x.Run && !x.Ended && x.Time == 1, "new match rearms after result");
            d.Reset(); d.Observe(Frame(0, 600, true, false)); d.Observe(Frame(.7, 601, true, false)); d.Observe(Frame(1.4, 601, true, false));
            d.Observe(Frame(2, null, false, false)); d.Observe(Frame(6, null, false, false));
            d.Observe(Frame(8, 90, true, false)); d.Observe(Frame(8.7, 91, true, false)); x = d.Observe(Frame(9.4, 91, true, false));
            check(x.Run && x.Time == 91, "rearm after menu even if early 30 seconds were missed");
            var occurrence = new Occurrence { At = 180, Item = new Timing() };
            check(ScheduleEngine.Urgency(occurrence, 149.9) == "Ink", "neutral before 30 seconds");
            check(ScheduleEngine.Urgency(occurrence, 150) == "Warning", "yellow at 30 seconds");
            check(ScheduleEngine.Urgency(occurrence, 170) == "Danger", "red at 10 seconds");
            d.Reset(); d.Observe(Frame(100,149,true,false)); d.Observe(Frame(100.6,150,true,false));
            x = d.Observe(Frame(101.2,150,true,false));
            check(x.Run && x.State == "СИНХРОНИЗИРОВАНО", "flicker regression starts with confirmed match");
            foreach (var missed in new[] { Frame(101.5,null,true,false), Frame(101.8,950,true,false), Frame(102.1,null,false,false) }) {
                x = d.Observe(missed);
                check(CursorAlerts.CanShow(true,true,x.Run,x.Ended,true,x.State == "СИНХРОНИЗИРОВАНО"), "brief unreadable HUD or OCR outlier keeps current warning visible");
                check(StatusBadge.Label(true,true,x.State,x.Run,x.Ended,151).Contains("РАБОТАЕТ"), "brief OCR loss does not flash status to waiting");
                check(!x.Time.HasValue, "missed frame does not rewind the live clock");
            }
            x = d.Observe(Frame(104.2,null,false,false));
            check(!x.Run && x.Time == 150 && x.State != "СИНХРОНИЗИРОВАНО", "three-second loss still stops stale countdowns");
            x = d.Observe(Frame(104.9,154,true,false));
            check(x.Run && x.Time == 154 && x.State == "СИНХРОНИЗИРОВАНО", "valid frame recovers after timeout");
            d.Observe(Frame(105.6,154,true,false)); x = d.Observe(Frame(107.5,154,true,false));
            check(!x.Run && x.State == "ПАУЗА НА ЧАСАХ ИГРЫ", "pause confirmed before bad frame");
            x = d.Observe(Frame(108,null,true,false));
            check(!x.Run && x.State == "ПАУЗА НА ЧАСАХ ИГРЫ" && x.Time == 154, "bad frame cannot resume a confirmed pause or flash its status");
            return count;
        }
        internal static int Run(string folder, string report)
        {
            var log = new StringBuilder();
            try
            {
                log.AppendLine("PASS: " + Unit() + " detector checks.");
                var reader = new VisionReader();
                foreach (string name in new[] { "match", "menu", "victory", "selection", "early-match" })
                using (var image = new Bitmap(Path.Combine(folder, name + ".png")))
                {
                    var o = reader.Analyze(image).GetAwaiter().GetResult();
                    log.AppendLine(name + ": seconds=" + o.Seconds + ", HUD=" + o.Hud + ", result=" + o.Result + ", " + o.Detail + " | " + o.Evidence);
                    Tests.Check(name == "match" ? o.Seconds == 159 && o.Hud && !o.Result : name == "early-match" ? o.Seconds == 26 && o.Hud && !o.Result : name == "selection" ? o.Seconds == 5 && !o.Hud && !o.Result : name == "victory" ? o.Result && !o.Hud : !o.Hud && !o.Result && !o.Seconds.HasValue, name + " classification");
                    // Derived geometry test: center-crop ultrawide to 16:9, then scale.
                    using (var crop = image.Clone(new Rectangle((image.Width - 2560) / 2, 0, 2560, 1440), System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                    using (var scaled = new Bitmap(crop, 1920, 1080))
                    {
                        var small = reader.Analyze(scaled).GetAwaiter().GetResult();
                        log.AppendLine(name + " 1920x1080: seconds=" + small.Seconds + ", HUD=" + small.Hud + ", result=" + small.Result + " | " + small.Evidence);
                        Tests.Check(small.Seconds == o.Seconds && small.Hud == o.Hud && small.Result == o.Result, name + " scaled classification");
                    }
                }
                log.AppendLine("PASS: original screen and derived 16:9 fixture checks.");
                File.WriteAllText(report, log.ToString()); return 0;
            }
            catch (Exception ex) { File.WriteAllText(report, log + ex.ToString()); return 1; }
        }
    }
}

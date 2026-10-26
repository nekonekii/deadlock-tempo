using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeadlockTempo
{
    internal static class Tests
    {
        internal static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); }
        internal static int Run(string report)
        {
            int count = 0;
            Action<bool, string> check = (ok, name) => { Check(ok, name); count++; };
            try
            {
                count += VisionTests.Unit();
                count += CursorTests.Unit();
                double time = 100; var clock = new MatchClock(() => time);
                check(clock.Seconds == 0 && !clock.Running, "new clock is paused");
                clock.Start(); time += 2.75; check(clock.Seconds == 2.75, "monotonic elapsed time, not timer ticks");
                clock.Start(); time += 1; check(clock.Seconds == 3.75, "start is idempotent");
                clock.Pause(); time += 200; check(clock.Seconds == 3.75, "paused time does not advance");
                clock.Set(100); clock.Start(); time += 10; check(clock.Seconds == 110, "sync while paused then resume");
                clock.Set(20); time += 1; check(clock.Seconds == 21, "sync while running resets anchor");
                clock.Set(-10); check(clock.Seconds == 0, "cannot go negative");
                clock.Reset(); check(clock.Seconds == 0 && !clock.Running, "reset pauses");
                double seconds;
                check(Times.Parse("100:59", out seconds) && seconds == 6059, "long matches");
                check(!Times.Parse("12:60", out seconds), "seconds overflow rejected");
                check(!Times.Parse("-1:00", out seconds), "negative time rejected");
                check(!Times.Parse("1:2:3", out seconds), "ambiguous clock rejected");
                check(Times.Format(3600) == "60:00", "no hour wrap");
                Profile p = Storage.DefaultProfile(); check(ScheduleEngine.Validate(p) == null, "default profile valid");
                check(p.Events.Select(x => x.Id).SequenceEqual(new[] { "small", "boxes", "medium", "runes", "large", "machines", "urn", "rift" }) && p.Events.All(x => x.Enabled), "urn joins the seven retained events, removed entries stay absent");
                var before = ScheduleEngine.Upcoming(p, 119.2).First(x => x.Item.Id == "small");
                check(before.Countdown(119.2) == "00:01", "countdown rounds up");
                check(ScheduleEngine.Upcoming(p, 120).First(x => x.Item.Id == "small").Countdown(120) == "СЕЙЧАС", "at exact first spawn");
                check(ScheduleEngine.Upcoming(p, 130).Any(x => x.Item.Id == "small"), "10 second display grace");
                check(!ScheduleEngine.Upcoming(p, 131).Any(x => x.Item.Id == "small"), "first-spawn event expires");
                check(ScheduleEngine.Upcoming(p, 300).First(x => x.Item.Id == "runes").At == 300, "periodic exact boundary");
                check(ScheduleEngine.Upcoming(p, 310.1).First(x => x.Item.Id == "runes").At == 600, "periodic advances after grace");
                check(ScheduleEngine.Upcoming(p, 600).First(x => x.Item.Id == "runes").At == 600, "second period retained");
                check(ScheduleEngine.Upcoming(p, 3600).First(x => x.Item.Id == "runes").At == 3600, "long match period");
                check(!ScheduleEngine.Upcoming(p, 700).Any(x => x.Item.Id == "boxes"), "boxes are not falsely repeated");
                check(ScheduleEngine.Upcoming(p, 660).First(x => x.Item.Id == "rift").Countdown(660) == "ОКНО", "window opens");
                check(ScheduleEngine.Upcoming(p, 779).Any(x => x.Item.Id == "rift"), "window lasts until range end");
                var rift = ScheduleEngine.Upcoming(p, 791).Single(x => x.Item.Id == "rift");
                check(rift.At == 1080 && rift.End == rift.At && rift.Reminder && !rift.Window, "after first window rift has a reminder, not a fabricated spawn window");
                check(!ScheduleEngine.Upcoming(p, 660).Single(x => x.Item.Id == "rift").Reminder, "first rift window remains separate from repeat reminders");
                check(ScheduleEngine.Upcoming(p, 1090.1).Single(x => x.Item.Id == "rift").At == 1500, "next rift reminder advances after its ten-second confirmation");
                check(rift.Countdown(1080) == "ПРОВЕРЬ" && rift.Detail(1080).Contains("не время появления"), "reminder does not claim an objective spawned");
                check(ScheduleEngine.Upcoming(p, 0).Single(x => x.Item.Id == "urn").At == 600, "urn first appears at ten minutes");
                check(ScheduleEngine.Upcoming(p, 610).Single(x => x.Item.Id == "urn").At == 600 && ScheduleEngine.Upcoming(p, 610.1).Single(x => x.Item.Id == "urn").At == 900, "urn repeat preserves current ten-second confirmation");
                check(ScheduleEngine.Upcoming(p, 1200).Single(x => x.Item.Id == "urn").At == 1200 && ScheduleEngine.Upcoming(p, 3600).Single(x => x.Item.Id == "urn").At == 3600, "urn repeats every five minutes into long matches");
                check(GameIcons.Get("urn") != null && GameIcons.Get("rift") != null, "both objective pictures are embedded and load offline");
                var clone = Storage.Copy(p); clone.Events[0].First = "bad"; check(ScheduleEngine.Validate(clone) != null && p.Events[0].First == "02:00", "validation and draft isolation");
                clone = Storage.Copy(p); clone.Events[0].Every = "00:00"; check(ScheduleEngine.Validate(clone) != null, "zero period rejected");
                clone = Storage.Copy(p); clone.Events[0].Until = "01:00"; check(ScheduleEngine.Validate(clone) != null, "inverted window rejected");
                clone = Storage.Copy(p); clone.Events[0].Until = "03:00"; clone.Events[0].Every = "05:00"; check(ScheduleEngine.Validate(clone) != null, "range plus repeat rejected");
                clone = Storage.Copy(p); clone.Events.Single(x => x.Id == "rift").Every = "02:10"; check(ScheduleEngine.Validate(clone) != null, "reminder cannot overlap its initial window or confirmation");
                clone = Storage.Copy(p); clone.Events.Single(x => x.Id == "rift").Until = null; check(ScheduleEngine.Validate(clone) != null, "reminder requires an initial window");
                clone = Storage.Copy(p); clone.Events[1].Id = clone.Events[0].Id; check(ScheduleEngine.Validate(clone) != null, "duplicate id rejected");
                clone = Storage.Copy(p); foreach (var e in clone.Events) e.Enabled = false; check(ScheduleEngine.Upcoming(clone, 0).Count == 0, "empty enabled profile");
                var legacy = Storage.Copy(p); legacy.ObjectiveEventsVersion = 0; legacy.Events.RemoveAll(x => x.Id == "urn");
                var legacyRift = legacy.Events.Single(x => x.Id == "rift"); legacyRift.Every = null; legacyRift.RepeatReminder = false;
                legacyRift.Enabled = false; legacyRift.Name = "Мой разлом"; legacyRift.IconId = "rift";
                legacy.Events[0].First = "02:15";
                string retained = Storage.Json().Serialize(legacy.Events.Where(x => x.Id != "rift").ToList());
                check(Storage.UpgradeObjectives(legacy) && legacy.Events.Count == 8 && legacy.Events.Single(x => x.Id == "urn").Enabled, "existing installations gain the missing urn automatically");
                check(legacyRift.RepeatReminder && legacyRift.Every == "07:00" && !legacyRift.Enabled && legacyRift.Name == "Мой разлом" && legacyRift.IconId == "rift", "migration adds rift reminders while retaining user presentation and disabled state");
                check(retained == Storage.Json().Serialize(legacy.Events.Where(x => x.Id != "rift" && x.Id != "urn").ToList()), "migration leaves every other timing untouched");
                legacy.Events.RemoveAll(x => x.Id == "urn");
                check(!Storage.UpgradeObjectives(legacy) && !legacy.Events.Any(x => x.Id == "urn"), "completed migration does not restore a subsequently deleted event");
                var custom = Storage.Copy(p); custom.ObjectiveEventsVersion = 0; custom.Events.Single(x => x.Id == "rift").First = "12:00";
                string customRift = Storage.Json().Serialize(custom.Events.Single(x => x.Id == "rift"));
                Storage.UpgradeObjectives(custom);
                check(customRift == Storage.Json().Serialize(custom.Events.Single(x => x.Id == "rift")), "custom rift schedule is preserved");
                var missing = Storage.Copy(p); missing.ObjectiveEventsVersion = 0; missing.Events.RemoveAll(x => x.Id == "urn" || x.Id == "rift");
                Storage.UpgradeObjectives(missing);
                check(missing.Events.Count == 8 && ScheduleEngine.Validate(missing) == null, "both missing objectives are added once with a valid schedule");
                string temp = report + ".json";
                Storage.Write(temp, p); Storage.Write(temp, p); check(Storage.Json().Deserialize<Profile>(File.ReadAllText(temp)).Events.Count == p.Events.Count && File.Exists(temp + ".bak"), "atomic profile round trip and backup");
                File.WriteAllText(report, "PASS: " + count + " checks. Clock, boundaries, periods, ranges, invalid inputs, profile isolation, atomic persistence."); return 0;
            }
            catch (Exception ex) { File.WriteAllText(report, ex.ToString()); return 1; }
        }
        internal static void Render(Window window, string path)
        {
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(path)) png.Save(file);
        }
    }
}

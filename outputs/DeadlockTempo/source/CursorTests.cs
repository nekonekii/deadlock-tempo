using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace DeadlockTempo
{
    internal static class CursorTests
    {
        internal static int Unit()
        {
            int count = 0;
            Action<bool, string> check = (ok, name) => { Tests.Check(ok, name); count++; };
            var p = Storage.DefaultProfile();
            Func<double, List<Occurrence>> alerts = seconds => CursorAlerts.Select(ScheduleEngine.Upcoming(p, seconds), seconds);
            check(alerts(149.99).Count == 0, "cursor quiet before 30-second threshold");
            check(alerts(150).Single().Item.Id == "boxes", "cursor crate appears at exactly 30 seconds");
            check(ScheduleEngine.Urgency(alerts(169.99).Single(), 169.99) == "Warning", "cursor remains yellow until 10 seconds");
            check(ScheduleEngine.Urgency(alerts(170).Single(), 170) == "Danger", "cursor red at exactly 10 seconds");
            check(alerts(179.99).Count == 1 && CursorAlerts.Phase(alerts(180).Single(), 180) == "Active", "warning becomes active exactly at spawn");
            check(alerts(189.99).Count == 1 && alerts(190).Count == 0, "active confirmation lasts exactly ten seconds");
            check(CursorAlerts.Countdown(alerts(150.1).Single(), 150.1) == "30с", "per-event seconds round up");
            check(CursorAlerts.Countdown(alerts(170).Single(), 170) == "10с", "per-event ten-second countdown");
            var crate = ScheduleEngine.Upcoming(p, 180).Single(x => x.Item.Id == "boxes");
            check(CursorAlerts.Countdown(crate, 181) == "АКТИВНО" && CursorAlerts.Phase(crate, 180) == "Active", "started event has active label and loses red warning");
            var phases = Enumerable.Range(150, 30).Select(t => ScheduleEngine.Urgency(alerts(t).Single(), t)).ToList();
            check(phases.Take(20).All(x => x == "Warning") && phases.Skip(20).Count() == 10 && phases.Skip(20).All(x => x == "Danger"), "yellow lasts 20 seconds followed by red for 10 seconds");
            check(alerts(270).Select(x => x.Item.Id).OrderBy(x => x).SequenceEqual(new[] { "medium", "runes" }), "cursor retains medium camps and runes without removed tunnel event");
            check(alerts(570).Any(x => x.Item.Id == "runes") && !alerts(570).Any(x => x.Item.Id == "boxes"), "runes repeat and crates do not repeat");
            check(alerts(630).Any(x => x.Item.Id == "rift") && CursorAlerts.Countdown(alerts(660).Single(x => x.Item.Id == "rift"), 660) == "ОКНО" && !alerts(670).Any(x => x.Item.Id == "rift"), "window opening is confirmed for ten seconds only");
            check(CursorAlerts.Phase(alerts(600).Single(x => x.Item.Id == "runes"), 600) == "Active" && alerts(609.99).Single(x => x.Item.Id == "runes").At == 600 && alerts(610).Count == 0 && alerts(870).Count == 2 && alerts(870).All(x => x.At == 900), "periodic runes and urn confirm current spawn then resume the next warning together");
            check(!alerts(569.99).Any(x => x.Item.Id == "urn") && alerts(570).Count == 2 && alerts(570).Any(x => x.Item.Id == "urn"), "urn warns at thirty seconds without suppressing runes");
            check(CursorAlerts.Phase(alerts(589.99).Single(x => x.Item.Id == "urn"),589.99) == "Warning" && CursorAlerts.Phase(alerts(590).Single(x => x.Item.Id == "urn"),590) == "Danger", "urn changes from yellow to red at ten seconds");
            check(CursorAlerts.Announcement(alerts(600).Single(x => x.Item.Id == "urn"),600) == "ЧАША ДУШ · ВРЕМЯ ПРИШЛО" && alerts(609.99).Any(x => x.Item.Id == "urn") && !alerts(610).Any(x => x.Item.Id == "urn"), "urn confirmation names the objective and expires after ten seconds");
            check(!alerts(1049.99).Any(x => x.Item.Id == "rift") && alerts(1050).Single().Reminder, "rift reminder starts thirty seconds before its scheduled prompt");
            check(CursorAlerts.Announcement(alerts(1070).Single(),1070).Contains("ПРОВЕРИТЬ РАЗЛОМ") && CursorAlerts.Phase(alerts(1070).Single(),1070) == "Danger", "red rift reminder asks to inspect the map");
            check(CursorAlerts.Announcement(alerts(1080).Single(),1080) == "РАЗЛОМ · ПРОВЕРЬ КАРТУ" && CursorAlerts.Countdown(alerts(1080).Single(),1080) == "ПРОВЕРЬ" && alerts(1090).Count == 0, "rift reminder never claims an active spawn and lasts ten seconds");
            var fast = new Profile { Events = new List<Timing> { new Timing { Id = "fast", Name = "Fast", Enabled = true, First = "01:00", Every = "00:30" } } };
            var fastAlerts = CursorAlerts.Select(ScheduleEngine.Upcoming(fast, 60), 60);
            check(fastAlerts.Count == 2 && fastAlerts[0].At == 60 && fastAlerts[1].At == 90, "active confirmation does not suppress the next short periodic warning");
            check(CursorAlerts.Select(ScheduleEngine.Upcoming(fast, 70), 70).Single().At == 90, "short periodic confirmation expires independently of next warning");
            check(CursorAlerts.Display(ScheduleEngine.Upcoming(p, 180), 180).Single().At == 180, "always-visible mode also preserves active confirmation");
            check(alerts(301).Count == 2 && alerts(310).Count == 0, "simultaneous events retain their own active confirmations");
            check(CursorAlerts.CanShow(true, true, true, false, true, true), "synced live clock permits cursor alerts");
            check(!CursorAlerts.CanShow(true, true, false, false, true, true), "paused clock hides cursor alerts");
            check(!CursorAlerts.CanShow(true, true, true, true, true, true), "end screen hides cursor alerts");
            check(!CursorAlerts.CanShow(true, true, true, false, true, false), "uncertain OCR hides cursor alerts");
            check(!CursorAlerts.CanShow(false, true, true, false, false, false), "global hide suppresses cursor alerts");
            check(!CursorAlerts.CanShow(true, false, true, false, false, false), "banner mode suppresses cursor alerts");
            check(CursorAlerts.CanShow(true, true, true, false, false, false), "manual running clock permits cursor alerts");
            var size = new Size(1180, 180);
            var screen = new Rect(0, 0, 3440, 1440);
            var center = CursorAlerts.Place(size, screen, 0, 17);
            check(Math.Abs(center.X + size.Width / 2 - screen.Width / 2) < .01, "announcement is horizontally centered");
            check(center.Y > 180 && center.Y + size.Height < screen.Height / 2, "announcement clears top scoreboard and central reticle");
            foreach (var area in new[] { screen, new Rect(0,0,1920,1080), new Rect(50,70,1280,720), new Rect(-1920,-200,1920,1080) }) {
                var scaledSize = new Size(Math.Min(1180, area.Width * .8), area.Height * .18);
                var at = CursorAlerts.Place(scaledSize, area, 0, 17);
                check(area.Contains(new Rect(at,scaledSize)), "announcement stays inside window and negative-origin monitor");
                check(Math.Abs(at.X + scaledSize.Width / 2 - (area.Left + area.Width / 2)) < .01, "window movement preserves horizontal center");
            }
            var old = Storage.Json().Deserialize<Settings>("{\"Scale\":1,\"Opacity\":0.94,\"ShowOverlay\":true}");
            check(old.BannerLayoutVersion == 0 && old.BannerTextSize == 48 && old.BannerOpacity == 1 && old.BannerTop == 17, "old settings acquire readable independent banner defaults");
            old.BannerTextSize = 60; old.BannerTop = 20;
            var saved = Storage.Json().Deserialize<Settings>(Storage.Json().Serialize(old));
            check(saved.BannerTextSize == 60 && saved.BannerTop == 20, "announcement preferences survive persistence");
            check(CursorAlerts.Announcement(crate,170).Contains("КОРОБКИ") && CursorAlerts.Announcement(crate,170).Contains("10 С"), "readable announcement identifies event and countdown");
            check(CursorAlerts.Announcement(crate,180).Contains("ВРЕМЯ ПРИШЛО"), "active announcement is distinct from countdown");
            var quiet = CursorAlerts.Display(ScheduleEngine.Upcoming(p, 31), 31);
            check(quiet.Count == 2 && quiet[0].Item.Id == "small" && quiet[0].At == 120 && quiet[1].Item.Id == "boxes" && quiet[1].At == 180, "nearest remaining timings persist without removed early-camp entry");
            check(CursorAlerts.Countdown(quiet[0], 31) == "01:29" && ScheduleEngine.Urgency(quiet[0], 31) == "Ink", "neutral countdown uses minutes and does not warn early");
            check(CursorAlerts.Display(ScheduleEngine.Upcoming(p, 270), 270).Count == 2, "persistent mode retains all simultaneous warnings");
            double launchTime = 0;
            var launch = new GameLaunchPolicy(() => launchTime);
            check(!launch.NeedsLaunch("", false), "autostart waits when game is absent");
            check(launch.NeedsLaunch("123", false), "new game triggers overlay startup");
            launch.Launched("123");
            check(!launch.NeedsLaunch("123", false), "startup grace prevents duplicate processes before the overlay mutex exists");
            launchTime = 4.9;
            check(!launch.NeedsLaunch("123", false), "failed startup is not retried in a tight loop");
            launchTime = 5;
            check(launch.NeedsLaunch("123", false), "missing overlay recovers during the SAME game session");
            launch.Launched("123");
            check(!launch.NeedsLaunch("123", true), "successful launch does not create a duplicate");
            check(launch.NeedsLaunch("123", false), "observed running overlay is recovered after unexpected exit");
            launch.Launched("123");
            launchTime = 5.1;
            check(launch.NeedsLaunch("456", false), "next game launch starts overlay again");
            check(!launch.NeedsLaunch("456", true), "running overlay is never duplicated");
            launch.NeedsLaunch("", false);
            check(launch.NeedsLaunch("456", false), "launch after observed game exit re-arms watcher");
            return count;
        }
    }
}

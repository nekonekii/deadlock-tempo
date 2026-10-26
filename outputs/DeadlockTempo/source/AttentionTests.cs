using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
namespace DeadlockTempo {
    public sealed partial class Controller {
        private void RunAttentionSmoke() {
            cursorOverlay.Left = -16000; cursorOverlay.Top = -16000;
            cursorStatus.Visibility = Visibility.Collapsed;
            settings.BannerTextSize = 48; settings.BannerOpacity = 1;
            ConfigureBanner(new Rect(0,0,3440,1440),1);
            var item = new Occurrence { Item = new Timing { Id = "boxes", Name = "Коробки" }, At = 180, End = 180 };
            var items = new List<Occurrence> { item };
            RenderCursor(new List<Occurrence>(),150); RenderCursor(items,150);
            IntPtr foreground = Native.GetForegroundWindow();
            cursorOverlay.Show(); cursorOverlay.UpdateLayout();
            Tests.Check(Native.GetForegroundWindow() == foreground,"large announcement does not activate");
            long mask = Native.Layered | Native.Transparent | Native.NoActivate | Native.ToolWindow;
            Tests.Check((Native.GetStyle(new WindowInteropHelper(cursorOverlay).Handle) & mask) == mask,"large text is click-through");
            var view = cursorViews[item.Key];
            Tests.Check(view.Text.Contains("КОРОБКИ И БАФФЫ") && view.Text.Contains("30 С") && view.TextSize == 48,"full-size readable countdown");
            int started = cursorMotion.TransitionCount;
            RenderCursor(items,151);
            Tests.Check(object.ReferenceEquals(view,cursorViews[item.Key]) && cursorMotion.TransitionCount == started,"ticks preserve the visual and animation rhythm");
            cursorMotion.Trigger("Warning",100,true); cursorMotion.Advance(100.5,true);
            Tests.Render(cursorOverlay,Path.ChangeExtension(smokeReport,".banner-yellow.png"));
            RenderCursor(items,170); cursorMotion.Trigger("Danger",100,true); cursorMotion.Advance(100.5,true);
            Tests.Check(view.Phase == "Danger" && view.Text.Contains("10 С"),"red state has explicit remaining seconds");
            Tests.Render(cursorOverlay,Path.ChangeExtension(smokeReport,".banner-red.png"));
            int red = cursorMotion.TransitionCount;
            RenderCursor(items,169.9); RenderCursor(items,170.1);
            Tests.Check(cursorMotion.TransitionCount == red,"OCR jitter does not replay a phase");
            Rect before = view.TransformToAncestor(cursorMotion).TransformBounds(new Rect(view.RenderSize));
            cursorMotion.Trigger("Danger",100,true);
            for (int frame=0;frame<600;frame++) {
                double now = 100 + frame / 60.0;
                view.Advance(now,true); cursorMotion.Advance(now,true); cursorOverlay.UpdateLayout();
                Rect after = view.TransformToAncestor(cursorMotion).TransformBounds(new Rect(view.RenderSize));
                Tests.Check(before == after && cursorMotion.Scale == 1 && cursorMotion.Lift == 0,"text does not zoom or jump during repeated contours");
                Tests.Check(new Rect(cursorMotion.RenderSize).Contains(cursorMotion.EffectBounds),"contours are not clipped");
            }
            cursorMotion.Advance(110,false);
            Tests.Check(!cursorMotion.IsMoving && cursorMotion.Accent == 0,"reduced-motion preference is respected");
            RenderCursor(items,180); cursorMotion.Trigger("Active",100,true); cursorMotion.Advance(100.5,true);
            Tests.Check(view.Phase == "Active" && view.Text.Contains("ВРЕМЯ ПРИШЛО"),"event start uses an explicit confirmation");
            Tests.Render(cursorOverlay,Path.ChangeExtension(smokeReport,".banner-active.png"));
            var simultaneous = CursorAlerts.Select(ScheduleEngine.Upcoming(profile,270),270);
            foreach (var region in new[] { new Rect(0,0,3440,1440),new Rect(0,0,1920,1080),new Rect(50,70,1280,720),new Rect(-1920,0,1920,1080) }) {
                ConfigureBanner(region,simultaneous.Count); RenderCursor(simultaneous,270); cursorOverlay.UpdateLayout();
                Tests.Check(cursorViews.Count == 2 && cursorViews.Values.Any(v=>v.Text.Contains("СРЕДНИЕ ЛАГЕРЯ")) && cursorViews.Values.Any(v=>v.Text.Contains("РУНЫ")),"both simultaneous events remain visible");
                var envelope = cursorOverlay.RenderSize;
                Point at = CursorAlerts.Place(envelope,region,0,17);
                Tests.Check(region.Contains(new Rect(at,envelope)),"announcement fits each game resolution");
                Tests.Check(Math.Abs(at.X+envelope.Width/2-(region.Left+region.Width/2))<1,"announcement stays centered on each monitor");
                Tests.Check(at.Y+envelope.Height<region.Top+region.Height*.46,"two announcements stay clear of the reticle");
                Tests.Check(at.Y+AlertMotionFrame.RestOffset.Y>=region.Top+region.Height*.15,"announcement clears scoreboard");
            }
            ConfigureBanner(new Rect(0,0,3440,1440),2); RenderCursor(simultaneous,270);
            cursorMotion.Trigger("Warning",100,true); cursorMotion.Advance(100.5,true);
            Tests.Render(cursorOverlay,Path.ChangeExtension(smokeReport,".banner-simultaneous.png"));
            foreach (double seconds in new[] { 570.0,590,600,630,1050,1070,1080,1470,1490,1500 }) {
                var objectives = CursorAlerts.Select(ScheduleEngine.Upcoming(profile,seconds),seconds);
                foreach (var region in new[] { new Rect(0,0,3440,1440),new Rect(0,0,1280,720) }) {
                    ConfigureBanner(region,objectives.Count); RenderCursor(objectives,seconds); cursorOverlay.UpdateLayout();
                    Tests.Check(cursorViews.Count == objectives.Count,"urn, runes and rift each keep a visible announcement");
                    var envelope = cursorOverlay.RenderSize;
                    Point at = CursorAlerts.Place(envelope,region,0,17);
                    Tests.Check(region.Contains(new Rect(at,envelope)) && at.Y+envelope.Height<region.Top+region.Height*.46,"objective announcements fit above the reticle at " + seconds + " in " + region + "; size " + envelope);
                }
                ConfigureBanner(new Rect(0,0,3440,1440),objectives.Count); RenderCursor(objectives,seconds);
                foreach (var objectiveView in cursorViews.Values) objectiveView.Advance(VisionReader.Now+1,true);
                cursorMotion.Trigger(CursorAlerts.Phase(objectives[0],seconds),100,true); cursorMotion.Advance(100.5,true);
                Tests.Render(cursorOverlay,Path.ChangeExtension(smokeReport,".objectives-"+seconds.ToString("0")+".png"));
            }
            clock.Reset(); ReloadDraft(); ApplyProfile();
            Tests.Check(profile.ObjectiveEventsVersion == 1 && !Storage.UpgradeObjectives(profile),"saving the schedule preserves the completed objective migration");
            // Exercise the actual alert/status rendering path through missed OCR frames.
            var flickerDetector = new MatchDetector();
            Func<double,int?,bool,VisionObservation> observation = (stamp,value,hud) => new VisionObservation { Stamp=stamp, Seconds=value, Hud=hud, Foreground=true };
            flickerDetector.Observe(observation(100,149,true)); flickerDetector.Observe(observation(100.6,150,true));
            var decision = flickerDetector.Observe(observation(101.2,150,true));
            settings.CursorMode = settings.ShowOverlay = settings.AutoVision = true;
            matchEnded = false; visionState = decision.State; clock.Set(150); clock.Start();
            RefreshCursorAlerts(Upcoming(clock.Seconds),clock.Seconds); UpdateStatusOverlayText();
            var retained = cursorViews[item.Key]; int entrances = cursorMotion.TransitionCount;
            foreach (var missed in new[] { observation(101.5,null,true),observation(101.8,950,true),observation(102.1,null,false),observation(102.4,151,true) }) {
                decision = flickerDetector.Observe(missed); visionState = decision.State;
                if (decision.Run) clock.Start(); else clock.Pause();
                RefreshCursorAlerts(Upcoming(clock.Seconds),clock.Seconds); UpdateStatusOverlayText();
                Tests.Check(cursorViews.ContainsKey(item.Key) && object.ReferenceEquals(retained,cursorViews[item.Key]),"OCR gaps preserve the existing text and picture instead of recreating them");
                Tests.Check(cursorMotion.TransitionCount == entrances && statusLabel.Text.Contains("РАБОТАЕТ"),"OCR recovery does not replay entrance or flash status");
            }
            RenderCursor(CursorAlerts.Select(ScheduleEngine.Upcoming(profile,310),310),310);
            Tests.Check(cursorViews.Count == 0 && !cursorMotion.IsMoving,"announcement and animation expire together");
            Tests.Check(!typeof(Controller).Assembly.GetManifestResourceNames().Contains("camps.json") && typeof(Controller).Assembly.GetType("DeadlockTempo.CampTracker") == null,"respawn code and catalog are removed from binary");
        }
    }
}

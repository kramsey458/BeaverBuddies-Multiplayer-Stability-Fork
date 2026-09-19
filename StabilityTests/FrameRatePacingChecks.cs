#nullable enable
using BeaverBuddies;
using BeaverBuddies.Panel;
using Newtonsoft.Json.Linq;
using TimberNet;

// A guest reporting its frame rate, and the host easing off when it falls below a floor the host chose.
static class FrameRatePacingChecks
{
    static void Check(bool value, string message = "assertion failed") { if (!value) throw new Exception(message); }
    static void Equal<T>(T expected, T actual) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"expected {expected}, got {actual}");

    public static IEnumerable<(string Name, Action Run)> Tests()
    {
        yield return ("Frame rate meter: counts frames per second in one-second windows", () =>
        {
            var meter = new FrameRateMeter();
            Equal(0, meter.Fps);
            for (int i = 0; i <= 60; i++) meter.Frame(10 + i / 60.0);        // one second at 60 fps
            Equal(60, meter.Fps);
            for (int i = 1; i <= 14; i++) meter.Frame(11 + i / 14.0);        // then one second at 14 fps
            Equal(14, meter.Fps);
        });
        yield return ("Frame rate meter: a long gap (loading, a save) starts again instead of reporting a tiny figure", () =>
        {
            var meter = new FrameRateMeter();
            for (int i = 0; i <= 60; i++) meter.Frame(i / 60.0);
            Equal(60, meter.Fps);
            meter.Frame(9);                                                  // eight seconds with no frames
            Equal(0, meter.Fps);
            for (int i = 1; i <= 30; i++) meter.Frame(9 + i / 30.0);
            Equal(30, meter.Fps);
            meter.Frame(5);                                                  // a clock that went backwards also restarts
            Equal(0, meter.Fps);
        });
        yield return ("Status reply carries the guest's frame rate, and every field is still validated", () =>
        {
            Check(StatusFrames.TryParseReply(StatusFrames.Reply(7, 1234, 14), out int seq, out int? tick, out int? fps));
            Equal(7, seq); Equal((int?)1234, tick); Equal((int?)14, fps);
            // Nothing to report (window in the background, first second of play): the field is left out.
            JObject none = StatusFrames.Reply(7, 1234, 0);
            Check(none["fps"] == null && StatusFrames.TryParseReply(none, out _, out tick, out fps) && tick == 1234 && fps == null);
            // Replies from builds that send less still count.
            Check(StatusFrames.TryParseReply(StatusFrames.Reply(7, 1234), out _, out tick, out fps) && tick == 1234 && fps == null);
            Check(StatusFrames.TryParseReply(StatusFrames.Reply(7), out _, out tick, out fps) && tick == null && fps == null);
            Check((int)StatusFrames.Reply(1, 1, 99999)["fps"]! == StatusFrames.MaxReportedFps, "an absurd frame rate is capped when sent");
            JObject With(Action<JObject> mutate) { var j = StatusFrames.Reply(1, 10, 30); mutate(j); return j; }
            Check(!StatusFrames.TryParseReply(With(j => j["fps"] = 0), out _, out _, out _), "zero fps");
            Check(!StatusFrames.TryParseReply(With(j => j["fps"] = -5), out _, out _, out _), "negative fps");
            Check(!StatusFrames.TryParseReply(With(j => j["fps"] = StatusFrames.MaxReportedFps + 1), out _, out _, out _), "fps above the cap");
            Check(!StatusFrames.TryParseReply(With(j => j["fps"] = "30"), out _, out _, out _), "string fps");
            Check(!StatusFrames.TryParseReply(With(j => j["fps"] = 29.5), out _, out _, out _), "fractional fps");
            Check(!StatusFrames.TryParseReply(With(j => j["fps"] = long.MaxValue), out _, out _, out _), "overflowing fps");
            Check(!StatusFrames.TryParseReply(With(j => j["extra"] = 1), out _, out _, out _), "fifth field");
            Check(!StatusFrames.TryParseReply(new JObject { ["type"] = StatusFrames.ReplyType, ["seq"] = 1, ["fps"] = 30, ["other"] = 2 }, out _, out tick, out fps)
                  && tick == null && fps == null, "an unexpected field must not leave a half-parsed result");
            // The two-field form of the older parser still behaves as before.
            Check(StatusFrames.TryParseReply(StatusFrames.Reply(7, 1234, 14), out seq, out tick) && tick == 1234);
        });
        yield return ("Frame rate pacing: off, or with no report, the host is never slowed", () =>
        {
            var pacing = new FrameRatePacing();
            for (int i = 0; i < 20; i++) { pacing.Sample(0, 5, true); Equal(100, pacing.Percent); }
            for (int i = 0; i < 20; i++) { pacing.Sample(30, null, true); Equal(100, pacing.Percent); }
        });
        yield return ("Frame rate pacing: three reports below the floor ease 10%, down to the floor of 30%", () =>
        {
            var pacing = new FrameRatePacing();
            pacing.Sample(30, 14, true); pacing.Sample(30, 14, true); Equal(100, pacing.Percent);
            pacing.Sample(30, 14, true); Equal(90, pacing.Percent);
            pacing.Sample(30, 29, true); pacing.Sample(30, 29, true); Equal(90, pacing.Percent);
            pacing.Sample(30, 29, true); Equal(80, pacing.Percent);
            for (int i = 0; i < 100; i++) pacing.Sample(30, 1, true);
            Equal(FrameRatePacing.MinPercent, pacing.Percent); Check(pacing.IsEasing);
        });
        yield return ("Frame rate pacing: one dip does not ease, and a report at the floor resets the count", () =>
        {
            var pacing = new FrameRatePacing();
            foreach (int fps in new[] { 60, 12, 60, 12, 12, 30, 12, 12, 35 }) { pacing.Sample(30, fps, true); Equal(100, pacing.Percent); }
        });
        yield return ("Frame rate pacing: climbs back only with room to spare, and holds in between", () =>
        {
            var pacing = new FrameRatePacing();
            for (int i = 0; i < 9; i++) pacing.Sample(30, 10, true);
            Equal(70, pacing.Percent);
            Equal(37, FrameRatePacing.RecoverAbove(30));
            for (int i = 0; i < 20; i++) pacing.Sample(30, 33, true);        // above the floor, short of the headroom
            Equal(70, pacing.Percent);
            pacing.Sample(30, 40, true); pacing.Sample(30, 40, true); Equal(70, pacing.Percent);
            pacing.Sample(30, 40, true); Equal(75, pacing.Percent);
            for (int i = 0; i < 30; i++) pacing.Sample(30, 90, true);
            Equal(100, pacing.Percent);
            Equal(25, FrameRatePacing.RecoverAbove(20)); Equal(75, FrameRatePacing.RecoverAbove(60));
        });
        yield return ("Frame rate pacing: a paused game or speed 1 is never held against a guest; switching it off restores full speed", () =>
        {
            var pacing = new FrameRatePacing();
            for (int i = 0; i < 6; i++) pacing.Sample(30, 10, true);
            Equal(80, pacing.Percent);
            for (int i = 0; i < 20; i++) pacing.Sample(30, 5, false);        // paused: nothing moves
            Equal(80, pacing.Percent);
            pacing.Sample(30, 5, true); pacing.Sample(30, 5, true); Equal(80, pacing.Percent);   // the count restarted
            pacing.Sample(0, 5, true); Equal(100, pacing.Percent);           // the host switched it off
            for (int i = 0; i < 3; i++) pacing.Sample(30, 5, true);
            pacing.Sample(30, null, true); Equal(100, pacing.Percent);       // the guest left, or tabbed out
        });
        yield return ("Frame rate pacing: the floors cycle Off, 20, 30, 45, 60 and back, and an unknown value starts again", () =>
        {
            int floor = 0; var seen = new List<int>();
            for (int i = 0; i < 6; i++) { floor = FrameRatePacing.NextFloor(floor); seen.Add(floor); }
            Check(seen.SequenceEqual(new[] { 20, 30, 45, 60, 0, 20 }), string.Join(",", seen));
            Equal(0, FrameRatePacing.NextFloor(999));
        });
        yield return ("Host pacing: the lower of lag easing and frame rate easing applies, never both multiplied", () =>
        {
            var lag = new HostPacing();
            Equal(7f, lag.Apply(7, 100));
            Check(Math.Abs(lag.Apply(7, 70) - 4.9f) < .001f);
            foreach (int behind in new[] { 30, 31, 32, 33, 34 }) lag.Sample(behind, true);
            Equal(85, lag.Percent);
            Check(Math.Abs(lag.Apply(7, 90) - 5.95f) < .001f, "lag easing is lower");
            Check(Math.Abs(lag.Apply(7, 50) - 3.5f) < .001f, "frame rate easing is lower");
            Equal(1f, lag.Apply(1, 30)); Equal(1f, lag.Apply(3, 30)); Equal(0f, lag.Apply(0, 30));
            for (int i = 0; i < 3; i++) lag.Sample(500, true);
            Check(lag.IsHolding); Equal(0f, lag.Apply(7, 50));               // a hold still wins
        });
        yield return ("Frame rate model: a slow guest at speed 7 is brought back above the floor and the speed settles", () =>
        {
            var run = Simulate(floor: 30, simulationMsPerTick: _ => 60, otherMsPerFrame: 20);
            Check(run.FpsAtFullSpeed < 20, $"the model guest should struggle at full speed, drew {run.FpsAtFullSpeed}");
            Check(run.LowestFpsLateOn >= 30, $"late on the guest still dropped to {run.LowestFpsLateOn} fps");
            Check(run.FinalPercent >= 40 && run.FinalPercent <= 60, $"settled at {run.FinalPercent}%");
            Equal(0, run.ChangesLateOn);                                     // no see-sawing once it has settled
        });
        yield return ("Frame rate model: a fast guest is never slowed, whatever the floor", () =>
        {
            foreach (int floor in new[] { 20, 30, 45, 60 })
                Equal(100, Simulate(floor, _ => 25, otherMsPerFrame: 8).LowestPercent);
        });
        yield return ("Frame rate model: full speed returns when the guest's load drops", () =>
        {
            var run = Simulate(floor: 30, simulationMsPerTick: t => t < 120 ? 60 : 20, otherMsPerFrame: 20, totalSeconds: 360);
            Check(run.LowestPercent < 100, "the heavy spell should have eased the host");
            Equal(100, run.FinalPercent);
        });
        yield return ("The host learns each guest's frame rate from its replies, and forgets it when the guest stops reporting", () =>
        {
            int previousInterval = TimberServer.StatusIntervalMs;
            TimberServer.StatusIntervalMs = 50;
            var listener = new QueueListener();
            var host = new TimberServer(listener, () => Task.FromResult(new byte[] { 1 }),
                () => new JObject { [TimberNetBase.TYPE_KEY] = "Init", [TimberNetBase.TICKS_KEY] = 0 });
            var (hostSide, guestSide) = PipeStream.Pair();
            listener.Pending.Add(hostSide);
            var guest = new TimberClient(guestSide);
            try
            {
                host.Start();
                bool mapped = false; guest.OnMapReceived += _ => mapped = true;
                guest.Start();
                Check(SpinWait.SpinUntil(() => { host.Update(); guest.Update(); return mapped; }, 3000), "guest never joined");
                Check(host.WorstGuestFps == null, "nothing reported yet");
                guest.ReportedFps = 14;
                Check(SpinWait.SpinUntil(() => { host.Update(); guest.Update(); Thread.Sleep(2); return host.WorstGuestFps == 14; }, 3000),
                    $"host saw {host.WorstGuestFps}");
                Equal((int?)14, host.GetNetworkStatus().Peers.Single().Fps);
                guest.ReportedFps = 0;                                       // the guest tabbed out
                Check(SpinWait.SpinUntil(() => { host.Update(); guest.Update(); Thread.Sleep(2); return host.WorstGuestFps == null; }, 3000),
                    "an old frame rate was kept after the guest stopped reporting");
                guest.ReportedFps = 48;
                Check(SpinWait.SpinUntil(() => { host.Update(); guest.Update(); Thread.Sleep(2); return host.WorstGuestFps == 48; }, 3000));
                guest.Close();
                Check(SpinWait.SpinUntil(() => { host.Update(); Thread.Sleep(2); return host.WorstGuestFps == null; }, 3000),
                    "a guest that left is still counted");
            }
            finally { TimberServer.StatusIntervalMs = previousInterval; guest.Close(); host.Close(); }
        });
        yield return ("Panel: the host sees the slowest guest's frame rate, the floor it chose, and why it is easing off", () =>
        {
            string T(string key, object[] args) => args.Length == 0 ? key : key + ":" + string.Join(",", args);
            var input = new PanelInputs { IsHost = true, Speed = 7, TickRate = 11.7 };
            input.Players.Add(new PanelPlayer { Id = 0, Name = "Host", IsYou = true, IsHost = true });
            input.Players.Add(new PanelPlayer { Id = 1, Name = "A", RttMs = 30, SilenceSeconds = .1, Fps = 58 });
            input.Players.Add(new PanelPlayer { Id = 2, Name = "B", RttMs = 30, SilenceSeconds = .1, Fps = 14 });
            var model = PanelModelBuilder.Build(input, T);
            Equal("BeaverBuddies.Panel.FpsValue:14", model.GuestFpsText);
            Equal("BeaverBuddies.Panel.FpsFloorOff", model.FpsFloorText); Check(model.PacingText == null);
            input.GuestFpsFloor = 30; input.FrameRatePacingPercent = 60;
            model = PanelModelBuilder.Build(input, T);
            Equal("BeaverBuddies.Panel.FpsValue:30", model.FpsFloorText);
            Equal("BeaverBuddies.Panel.PacingFpsValue:60", model.PacingText);
            input.HostPacingPercent = 40;                                    // lag easing is the stronger of the two
            Equal("BeaverBuddies.Panel.PacingValue:40", PanelModelBuilder.Build(input, T).PacingText);
            input.HostPacingHolding = true;
            Equal("BeaverBuddies.Panel.PacingHolding", PanelModelBuilder.Build(input, T).PacingText);
            // A guest sees none of it, and guests that report nothing show no figure.
            var guestView = new PanelInputs { IsHost = false, GuestFpsFloor = 30, FrameRatePacingPercent = 60 };
            guestView.Players.Add(new PanelPlayer { Id = 1, Name = "A", IsYou = true, Fps = 14 });
            var guestModel = PanelModelBuilder.Build(guestView, T);
            Check(guestModel.GuestFpsText == null && guestModel.FpsFloorText == null && guestModel.PacingText == null);
            input.Players.ForEach(p => p.Fps = null);
            Check(PanelModelBuilder.Build(input, T).GuestFpsText == null);
        });
    }

    // One guest at speed 7. Each second the host runs at 11.67 ticks/s times its easing percentage, the guest keeps
    // up with that, and what is left of the guest's second goes to frames: fps = (1000 - ticks x ms per tick) / ms
    // per frame, capped at 60 (VSync). The host samples that once a second, as the status feed does.
    static (int FpsAtFullSpeed, int LowestPercent, int FinalPercent, int LowestFpsLateOn, int ChangesLateOn) Simulate(
        int floor, Func<double, double> simulationMsPerTick, double otherMsPerFrame, double totalSeconds = 240)
    {
        const double fullTicksPerSecond = 7 / .6;
        var pacing = new FrameRatePacing();
        int Fps(double time, int percent) =>
            (int)Math.Max(1, Math.Min(60, (1000 - fullTicksPerSecond * percent / 100.0 * simulationMsPerTick(time)) / otherMsPerFrame));
        int lowest = 100, lowestFpsLate = int.MaxValue, changesLate = 0;
        for (int second = 0; second < totalSeconds; second++)
        {
            int fps = Fps(second, pacing.Percent);
            int before = pacing.Percent;
            pacing.Sample(floor, fps, true);
            lowest = Math.Min(lowest, pacing.Percent);
            if (second > totalSeconds * 2 / 3)
            {
                lowestFpsLate = Math.Min(lowestFpsLate, fps);
                if (pacing.Percent != before) changesLate++;
            }
        }
        return (Fps(0, 100), lowest, pacing.Percent, lowestFpsLate, changesLate);
    }

    sealed class QueueListener : ISocketListener
    {
        public readonly System.Collections.Concurrent.BlockingCollection<ISocketStream> Pending = new();
        public void Start() { }
        public ISocketStream AcceptClient() => Pending.Take();
        public void Stop() => Pending.CompleteAdding();
    }
}

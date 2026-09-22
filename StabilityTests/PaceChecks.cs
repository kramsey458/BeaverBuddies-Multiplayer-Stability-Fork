using BeaverBuddies;

// 1.1.14: a guest follows the host's pace while the host eases off for a slow guest (CatchUpSpeed.PaceFor), and the
// host's easing thresholds count time rather than ticks above speed 7 (HostPacing.Scaled).
static class PaceChecks
{
    static void Check(bool value, string message = "assertion failed") { if (!value) throw new Exception(message); }
    static void Equal<T>(T expected, T actual) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"expected {expected}, got {actual}");

    // A guest behind a host that runs at hostSpeed while the players chose `chosen`: frame by frame at 60 fps, the host's
    // ticks arriving 50 ms after the host starts them, the guest never starting a tick it has not received. Returns the
    // share of frames the guest could not move at all, over the last minute of two.
    static double FrozenShare(bool guestTold, float chosen = 7, float hostSpeed = 4.9f)
    {
        const double frame = 1 / 60.0, tickSeconds = 0.6, latency = 0.05;
        var arrivals = new Queue<(double At, int Tick)>();
        double hostTicks = 0, guest = 0, time = 0;
        int sent = 0, available = 0, frozen = 0, counted = 0;
        float current = chosen;
        for (int f = 0; f < 60 * 120; f++, time += frame)
        {
            hostTicks += frame * hostSpeed / tickSeconds;
            while (sent < (int)hostTicks) { sent++; arrivals.Enqueue((time + latency, sent)); }
            while (arrivals.Count > 0 && arrivals.Peek().At <= time) available = arrivals.Dequeue().Tick;
            float pace = guestTold ? CatchUpSpeed.PaceFor(chosen, hostSpeed) : chosen;
            current = CatchUpSpeed.For(pace, available - (int)guest, current);
            double want = guest + frame * current / tickSeconds;
            bool stuck = guest >= available;
            guest = Math.Min(want, available);
            if (f >= 60 * 60) { counted++; if (stuck) frozen++; }
        }
        return (double)frozen / counted;
    }

    public static IEnumerable<(string Name, Action Run)> Tests()
    {
        yield return ("Pace: a guest follows the host's eased pace, never above the chosen speed, and never a hold", () =>
        {
            Equal(7f, CatchUpSpeed.PaceFor(7, null));
            Equal(4.9f, CatchUpSpeed.PaceFor(7, 4.9f));
            Equal(7f, CatchUpSpeed.PaceFor(7, 0));
            Equal(7f, CatchUpSpeed.PaceFor(7, 9));
            Equal(0f, CatchUpSpeed.PaceFor(0, 4));
            Equal(3.5f, CatchUpSpeed.PaceFor(3.5f, null));
        });

        yield return ("Pace: at the host's pace a guest in step runs at it, and one far behind still catches up", () =>
        {
            float pace = CatchUpSpeed.PaceFor(7, 4.9f);
            Equal(4.9f, CatchUpSpeed.For(pace, 0, 4.9f));
            Check(CatchUpSpeed.For(pace, 12, 4.9f) > 4.9f, "a guest well behind the eased host no longer catches up");
        });

        yield return ("Pace: behind a host eased to 70%, a guest told the pace stands still at almost no frames", () =>
        {
            double untold = FrozenShare(guestTold: false), told = FrozenShare(guestTold: true);
            // Untold, the guest reaches the start of every tick early and waits there (about a third of all frames).
            Check(untold > 0.15, $"the model no longer shows the stop-go it was built for ({untold:P1} frozen)");
            Check(told < 0.02, $"a guest told the host's pace still stood still at {told:P1} of frames");
        });

        yield return ("Pacing: above speed 7 the thresholds count the same time, so an eased host climbs back", () =>
        {
            Equal(15, HostPacing.Scaled(HostPacing.HighTicks, 7));
            Equal(15, HostPacing.Scaled(HostPacing.HighTicks, 3));
            Equal(64, HostPacing.Scaled(HostPacing.HighTicks, 30));
            Equal(17, HostPacing.Scaled(HostPacing.LowTicks, 30));
            // At speed 30, a guest that cannot keep up (far behind, not gaining) eases the host...
            var boosted = new HostPacing();
            for (int i = 0; i < 8; i++) boosted.Sample(100 + i, true, 30);
            Check(boosted.Percent < 100 && !boosted.IsHolding, "a guest 100 ticks behind at speed 30 did not ease the host");
            int eased = boosted.Percent;
            // ...and once it keeps up (12 to 16 ticks behind at speed 30 with 150 ms of ping) the host climbs back.
            for (int i = 0; i < 10; i++) boosted.Sample(14, true, 30);
            Check(boosted.Percent > eased, "an eased host never climbed back at a boosted speed");
            // At speed 7 nothing changes: 14 ticks behind neither eases nor lets the host climb.
            var seven = new HostPacing();
            for (int i = 0; i < 8; i++) seven.Sample(40 + i, true);
            int sevenEased = seven.Percent;
            Check(sevenEased < 100);
            for (int i = 0; i < 10; i++) seven.Sample(14, true, 7);
            Equal(sevenEased, seven.Percent);
        });
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BeaverBuddies.Panel
{
    public enum Quality { Unknown, Good, Fair, Poor, Silent }

    public enum StatusKind { InSync, CatchingUp, WaitingForHost, Unstable, Desynced, Disconnected }

    public static class PingQuality
    {
        public const double GoodMaxMs = 80;
        public const double FairMaxMs = 160;
        /// <summary>No reply for this long means the player has stopped responding, whatever their last ping was.</summary>
        public const double SilentSeconds = 5;

        public static Quality Classify(double? rttMs, double? silenceSeconds)
        {
            if (silenceSeconds != null && silenceSeconds.Value >= SilentSeconds) return Quality.Silent;
            if (rttMs == null) return Quality.Unknown;
            if (rttMs.Value <= GoodMaxMs) return Quality.Good;
            return rttMs.Value <= FairMaxMs ? Quality.Fair : Quality.Poor;
        }
    }

    /// <summary>Simulation ticks per second, measured over a short sliding window so it neither flickers nor lags.</summary>
    public sealed class TickRateMeter
    {
        const double WindowSeconds = 3;
        const double MinSpanSeconds = .5;
        readonly Queue<(double Time, long Ticks)> samples = new Queue<(double, long)>();

        /// <summary>Records the current tick and returns the rate, or null until there is enough history.</summary>
        public double? Sample(long ticks, double nowSeconds)
        {
            // A lower tick count means a new map or session: start again rather than report a negative rate.
            if (samples.Count > 0 && ticks < samples.Last().Ticks) samples.Clear();
            samples.Enqueue((nowSeconds, ticks));
            while (samples.Count > 2 && nowSeconds - samples.Peek().Time > WindowSeconds) samples.Dequeue();
            var oldest = samples.Peek();
            double span = nowSeconds - oldest.Time;
            if (span < MinSpanSeconds) return null;
            return (ticks - oldest.Ticks) / span;
        }

        public void Reset() => samples.Clear();
    }

    public sealed class PanelPlayer
    {
        public int Id;
        public string Name = "";
        public bool IsYou, IsHost;
        public double? RttMs;
        public double? SilenceSeconds;
        public string Transport = "";
        /// <summary>Host only: how many ticks behind the host this guest last reported being. Null if unknown.</summary>
        public int? TicksBehind;
        /// <summary>Host only: the frames per second this guest last reported. Null if it has not reported one.</summary>
        public int? Fps;
    }

    /// <summary>Everything the panel needs, gathered by the game and free of any game types.</summary>
    public sealed class PanelInputs
    {
        public bool IsHost, Stopped, Desynced, WaitingForHost;
        public int TicksBehind;
        public double? HostSilenceSeconds;
        public double? TickRate;
        public float Speed;
        /// <summary>Host only: percent of the chosen speed the host is running at. Below 100 while easing off for a guest.</summary>
        public int HostPacingPercent = 100;
        public bool HostPacingHolding;
        /// <summary>Host only: the guest frame rate below which the host eases off. 0 is off.</summary>
        public int GuestFpsFloor;
        /// <summary>Host only: percent of the chosen speed the host runs at because of a guest's frame rate.</summary>
        public int FrameRatePacingPercent = 100;
        public List<PanelPlayer> Players = new List<PanelPlayer>();
    }

    public sealed class PanelRow
    {
        public string Name = "", PingText = "";
        public Quality Quality;
        /// <summary>Your own row. It is drawn in bold, and its ping is a dash: you have no ping to yourself.</summary>
        public bool IsYou;
    }

    /// <summary>The finished text and states the view shows. No layout, no colors: just what to say.</summary>
    public sealed class PanelModel
    {
        public StatusKind Status;
        public string StatusText = "", Role = "", Summary = "";
        public Quality SummaryQuality;
        public List<PanelRow> Rows = new List<PanelRow>();
        public string TickRateText = "", SpeedText = "";
        /// <summary>Only for guests: how far this game is behind the host. Null for the host.</summary>
        public string BehindText;
        /// <summary>How the players are connected ("Direct", "Steam"), or null when unknown.</summary>
        public string LinkText;
        /// <summary>Only for the host: how far the slowest guest is behind. Null for a guest or when unknown.</summary>
        public string GuestsBehindText;
        /// <summary>Only for the host while it is easing off so a guest can keep up. Null otherwise.</summary>
        public string PacingText;
        /// <summary>Only for the host: the lowest frame rate any guest reported. Null for a guest or when unknown.</summary>
        public string GuestFpsText;
        /// <summary>Only for the host: the chosen guest frame rate floor ("Off", "30 fps"). Clicking it picks the next one.</summary>
        public string FpsFloorText;
    }

    public static class PanelModelBuilder
    {
        /// <summary>A guest this many ticks behind the host is shown as catching up.</summary>
        public const int CatchingUpTicks = 3;

        /// <summary>Shown as the ping on your own row.</summary>
        public const string YourPingText = "-";

        /// <param name="t">Translates a key with format arguments (the game's localization).</param>
        public static PanelModel Build(PanelInputs input, Func<string, object[], string> t)
        {
            var model = new PanelModel();
            model.Role = t(input.IsHost ? "BeaverBuddies.Panel.Host" : "BeaverBuddies.Panel.Guest", Array.Empty<object>());

            var others = input.Players.Where(p => !p.IsYou && !p.IsHost).ToList();
            var silent = input.Players.Any(p => !p.IsYou && !p.IsHost && Classify(p) == Quality.Silent);
            bool hostSilent = !input.IsHost && input.HostSilenceSeconds != null && input.HostSilenceSeconds.Value >= PingQuality.SilentSeconds;

            // The most urgent thing wins.
            if (input.Stopped) model.Status = StatusKind.Disconnected;
            else if (input.Desynced) model.Status = StatusKind.Desynced;
            else if (hostSilent || silent) model.Status = StatusKind.Unstable;
            else if (!input.IsHost && input.WaitingForHost) model.Status = StatusKind.WaitingForHost;
            else if (!input.IsHost && input.TicksBehind >= CatchingUpTicks) model.Status = StatusKind.CatchingUp;
            else model.Status = StatusKind.InSync;
            model.StatusText = t(StatusKey(model.Status), new object[] { input.TicksBehind });

            // Every row but your own is a ping and nothing else. The host measures each guest. A guest measures only the
            // host, so the host's row shows the guest's own ping to it, and the other guests show the ping the host
            // measured for them. Your own row has nothing to measure.
            var you = input.Players.FirstOrDefault(p => p.IsYou);
            foreach (var player in OrderedPlayers(input.Players))
            {
                var row = new PanelRow { Name = player.Name, IsYou = player.IsYou };
                if (player.IsYou)
                {
                    row.PingText = YourPingText;
                    row.Quality = Quality.Good;
                }
                else if (player.IsHost)
                {
                    row.PingText = PingText(you?.RttMs, input.HostSilenceSeconds, t);
                    row.Quality = PingQuality.Classify(you?.RttMs, input.HostSilenceSeconds);
                }
                else
                {
                    row.PingText = PingText(player.RttMs, player.SilenceSeconds, t);
                    row.Quality = Classify(player);
                }
                model.Rows.Add(row);
            }

            // The pill shows one number: the worst ping the host sees, or this guest's own ping.
            var measured = input.IsHost ? others : input.Players.Where(p => p.IsYou).ToList();
            double? headline = measured.Where(p => p.RttMs != null).Select(p => (double?)p.RttMs).DefaultIfEmpty(null).Max();
            double? headlineSilence = measured.Select(p => p.SilenceSeconds).Where(s => s != null).DefaultIfEmpty(null).Max();
            if (!input.IsHost) headlineSilence = input.HostSilenceSeconds;
            model.SummaryQuality = model.Status == StatusKind.Disconnected ? Quality.Silent : PingQuality.Classify(headline, headlineSilence);
            int count = input.Players.Count;
            string people = t(count == 1 ? "BeaverBuddies.Panel.PlayersOne" : "BeaverBuddies.Panel.PlayersMany", new object[] { count });
            model.Summary = model.Status == StatusKind.Disconnected
                ? t("BeaverBuddies.Panel.StatusDisconnected", Array.Empty<object>())
                : headline == null ? people : people + "  " + PingText(headline, null, t);

            model.TickRateText = input.TickRate == null
                ? Measuring(t)
                : t("BeaverBuddies.Panel.TickRateValue", new object[] { input.TickRate.Value.ToString("0.0", CultureInfo.InvariantCulture) });
            model.SpeedText = input.Speed <= 0
                ? t("BeaverBuddies.Panel.Paused", Array.Empty<object>())
                : t("BeaverBuddies.Panel.SpeedValue", new object[] { input.Speed.ToString("0.#", CultureInfo.InvariantCulture) });
            if (!input.IsHost)
                model.BehindText = t(input.TicksBehind == 1 ? "BeaverBuddies.Panel.TicksOne" : "BeaverBuddies.Panel.TicksMany",
                    new object[] { input.TicksBehind });

            if (input.IsHost)
            {
                int? worst = input.Players.Where(p => !p.IsYou && !p.IsHost && p.TicksBehind != null)
                    .Select(p => p.TicksBehind).DefaultIfEmpty(null).Max();
                if (worst != null)
                    model.GuestsBehindText = t(worst == 1 ? "BeaverBuddies.Panel.TicksOne" : "BeaverBuddies.Panel.TicksMany",
                        new object[] { worst.Value });
                if (input.HostPacingHolding)
                    model.PacingText = t("BeaverBuddies.Panel.PacingHolding", new object[0]);
                else if (input.FrameRatePacingPercent < 100 && input.FrameRatePacingPercent <= input.HostPacingPercent)
                    // The guest's frame rate is what is holding the host back, so say so.
                    model.PacingText = t("BeaverBuddies.Panel.PacingFpsValue", new object[] { input.FrameRatePacingPercent });
                else if (input.HostPacingPercent < 100)
                    model.PacingText = t("BeaverBuddies.Panel.PacingValue", new object[] { input.HostPacingPercent });

                int? lowestFps = input.Players.Where(p => !p.IsYou && !p.IsHost && p.Fps != null)
                    .Select(p => p.Fps).DefaultIfEmpty(null).Min();
                if (lowestFps != null) model.GuestFpsText = t("BeaverBuddies.Panel.FpsValue", new object[] { lowestFps.Value });
                model.FpsFloorText = input.GuestFpsFloor <= 0
                    ? t("BeaverBuddies.Panel.FpsFloorOff", new object[0])
                    : t("BeaverBuddies.Panel.FpsValue", new object[] { input.GuestFpsFloor });
            }

            // The host lists how each guest reaches it; a guest shows only how it reaches the host.
            var linked = input.IsHost ? input.Players.Where(p => !p.IsYou && !p.IsHost) : input.Players.Where(p => p.IsYou);
            var transports = linked.Select(p => p.Transport).Where(x => !string.IsNullOrEmpty(x)).Distinct().Select(x => LinkName(x, t)).ToList();
            model.LinkText = transports.Count == 0 ? null : string.Join(", ", transports);
            return model;
        }

        static Quality Classify(PanelPlayer p) => PingQuality.Classify(p.RttMs, p.SilenceSeconds);

        static IEnumerable<PanelPlayer> OrderedPlayers(IEnumerable<PanelPlayer> players) =>
            players.OrderBy(p => p.IsHost ? 0 : 1).ThenBy(p => p.Id);

        static string PingText(double? rtt, double? silence, Func<string, object[], string> t)
        {
            if (silence != null && silence.Value >= PingQuality.SilentSeconds) return t("BeaverBuddies.Panel.NoResponse", Array.Empty<object>());
            return rtt == null ? Measuring(t) : t("BeaverBuddies.Panel.PingValue", new object[] { ((int)Math.Round(rtt.Value)).ToString(CultureInfo.InvariantCulture) });
        }

        static string Measuring(Func<string, object[], string> t) => t("BeaverBuddies.Panel.Measuring", Array.Empty<object>());

        static string LinkName(string transport, Func<string, object[], string> t)
        {
            if (transport == "Direct") return t("BeaverBuddies.Panel.LinkDirect", Array.Empty<object>());
            if (transport == "Steam") return t("BeaverBuddies.Panel.LinkSteam", Array.Empty<object>());
            return transport;
        }

        static string StatusKey(StatusKind kind)
        {
            switch (kind)
            {
                case StatusKind.CatchingUp: return "BeaverBuddies.Panel.StatusCatchingUp";
                case StatusKind.WaitingForHost: return "BeaverBuddies.Panel.StatusWaiting";
                case StatusKind.Unstable: return "BeaverBuddies.Panel.StatusUnstable";
                case StatusKind.Desynced: return "BeaverBuddies.Panel.StatusDesynced";
                case StatusKind.Disconnected: return "BeaverBuddies.Panel.StatusDisconnected";
                default: return "BeaverBuddies.Panel.StatusInSync";
            }
        }
    }
}

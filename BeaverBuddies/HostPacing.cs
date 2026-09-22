using System;

namespace BeaverBuddies
{
    // Eases the host's speed only when a guest genuinely cannot keep up.
    //
    // A guest that falls behind speeds itself up (CatchUpSpeed). That recovers from hitches, but a computer that
    // cannot sustain the chosen speed at all falls further behind every second however hard it tries, until the
    // session is unplayable. The only thing that helps then is the host running a little slower.
    //
    // The host learns each guest's lag from the status feed, about once a second. The rule:
    //  - Nothing happens while the worst guest is fewer than HighTicks behind, or is behind but closing the gap.
    //    A guest that hitched and is recovering is left to it.
    //  - If the worst guest is more than HighTicks behind and has not gained for several samples in a row, the
    //    host drops one step. The first step needs four such samples, because lag also grows for as long as a
    //    single stall lasts (a save, a long garbage collection, the window in the background) and a computer that
    //    is fast enough recovers from that by itself. Once the host is already easing, two samples are enough.
    //  - Once every guest is within LowTicks, the host climbs back one smaller step per sample.
    //  Dropping faster than climbing settles just below what the slowest computer can do, then keeps probing
    //  upwards, so a guest that was only slow for a while gets full speed back.
    //
    //  - Easing has a floor, and with the large colony speed limit removed 30% of speed 7 is still about 3.5
    //    ticks a second. A guest that has stopped altogether (a long save, a long collection, a stalled
    //    connection) keeps falling behind at that rate while the host queues events for it, until Steam's send
    //    buffer fills and the connection is dropped. So beyond StopTicks the host holds still until the guest
    //    is back within ResumeTicks. Holding does not move the easing percentage: a single stall says nothing
    //    about what the guest can sustain.
    //
    // This changes how fast the host works through ticks, never which tick anything happens on, so it cannot
    // change what anyone simulates. Speed 1 and a paused game are never eased; the hold applies at every speed.
    public sealed class HostPacing
    {
        public const int HighTicks = 15;
        public const int LowTicks = 4;
        public const int DownStepPercent = 15;
        public const int UpStepPercent = 5;
        public const int MinPercent = 30;
        // About five seconds of ticks at a true speed 7.
        public const int StopTicks = 60;
        public const int ResumeTicks = 10;
        private const int SamplesBeforeFirstDrop = 4;
        private const int SamplesBeforeNextDrop = 2;

        private int _previousBehind = -1;
        private int _samplesNotGaining;

        // Whole percent, so repeated steps cannot drift.
        public int Percent { get; private set; } = 100;

        public bool IsEasing => Percent < 100;

        // True while the host holds still for a guest that is very far behind.
        public bool IsHolding { get; private set; }

        // The thresholds above are ticks at a true speed 7. With a speed boost a tick is shorter, and a guest that keeps up
        // runs more ticks behind for the same time on the network (at speed 30 with 150 ms of ping, 12 to 16 ticks): a
        // host that had eased once never climbed back. Above speed 7 they scale with the speed, so they count the same
        // stretch of time.
        public static int Scaled(int ticks, float speed) => speed > 7 ? (int)Math.Round(ticks * speed / 7f) : ticks;

        // One status sample. worstGuestTicksBehind is null when no guest has reported a tick. speed is the speed the
        // players chose (with its boost).
        public void Sample(int? worstGuestTicksBehind, bool running, float speed = 7)
        {
            int highTicks = Scaled(HighTicks, speed), lowTicks = Scaled(LowTicks, speed);
            int stopTicks = Scaled(StopTicks, speed), resumeTicks = Scaled(ResumeTicks, speed);
            if (worstGuestTicksBehind == null)
            {
                // Nobody to wait for.
                Percent = 100;
                IsHolding = false;
                _previousBehind = -1;
                _samplesNotGaining = 0;
                return;
            }
            if (worstGuestTicksBehind.Value > stopTicks) IsHolding = true;
            else if (worstGuestTicksBehind.Value <= resumeTicks) IsHolding = false;
            if (!running)
            {
                // Paused: the guest catches up on its own, and lag while paused says nothing about its speed.
                _previousBehind = -1;
                _samplesNotGaining = 0;
                return;
            }
            int behind = worstGuestTicksBehind.Value;
            if (IsHolding)
            {
                // The lag now only says how fast the guest catches up with a host that is standing still.
                _previousBehind = -1;
                _samplesNotGaining = 0;
                return;
            }
            if (behind > highTicks)
            {
                bool gaining = _previousBehind >= 0 && behind < _previousBehind;
                _samplesNotGaining = gaining || _previousBehind < 0 ? 0 : _samplesNotGaining + 1;
                if (_samplesNotGaining >= (IsEasing ? SamplesBeforeNextDrop : SamplesBeforeFirstDrop))
                {
                    Percent = Math.Max(MinPercent, Percent - DownStepPercent);
                    _samplesNotGaining = 0;
                }
            }
            else
            {
                _samplesNotGaining = 0;
                if (behind <= lowTicks)
                {
                    Percent = Math.Min(100, Percent + UpStepPercent);
                }
            }
            _previousBehind = behind;
        }

        // The speed the host should actually run at for the speed the players chose.
        public float Apply(float targetSpeed)
        {
            return Apply(targetSpeed, 100);
        }

        // As above, with a second reason to ease off (FrameRatePacing). The lower of the two percentages wins:
        // whichever guest problem is worse decides, and they are never multiplied together.
        public float Apply(float targetSpeed, int otherPercent)
        {
            if (IsHolding)
            {
                return 0;
            }
            int percent = Math.Min(Percent, otherPercent);
            if (percent >= 100 || targetSpeed <= 1)
            {
                return targetSpeed;
            }
            return Math.Max(1f, targetSpeed * percent / 100f);
        }
    }
}

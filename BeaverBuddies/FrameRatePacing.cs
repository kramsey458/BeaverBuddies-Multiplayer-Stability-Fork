using System;

namespace BeaverBuddies
{
    // Counts a player's own frames per second, one-second windows, from the game's per-frame update.
    // A guest sends the latest figure to the host with each reply to the host's ping probe.
    public sealed class FrameRateMeter
    {
        // A gap this long between frames is loading or a save, not play: start again.
        private const double ResetGapSeconds = 2;

        private double _windowStart = -1;
        private double _lastFrame = -1;
        private int _frames;

        // Frames in the last full one-second window; 0 until one has completed.
        public int Fps { get; private set; }

        public void Frame(double nowSeconds)
        {
            if (_windowStart < 0 || nowSeconds < _lastFrame || nowSeconds - _lastFrame > ResetGapSeconds)
            {
                _windowStart = nowSeconds;
                _frames = 0;
                Fps = 0;
            }
            else
            {
                _frames++;
                double elapsed = nowSeconds - _windowStart;
                if (elapsed >= 1)
                {
                    Fps = Math.Max(1, (int)Math.Round(_frames / elapsed));
                    _windowStart = nowSeconds;
                    _frames = 0;
                }
            }
            _lastFrame = nowSeconds;
        }
    }

    // Eases the host's speed when a guest's frame rate falls below a floor the host chose.
    //
    // HostPacing looks at how many ticks behind a guest is. A guest can keep up in ticks and still be having a
    // bad time: with the large colony speed limit removed, a slower computer spends most of each second on the
    // simulation and draws a dozen frames. It is never far behind, so HostPacing never reacts. This looks at the
    // guest's frame rate instead. It is off unless the host picks a floor.
    //
    // Guests report their frames per second about once a second, only while their game window has focus (a
    // window in the background is throttled by the system, which says nothing about the computer). The rule:
    //  - Below the floor for three reports in a row: the host drops 10% of the chosen speed, to a floor of 30%.
    //  - At or above the floor with some headroom (RecoverAbove) for three reports in a row: back up 5%.
    //  - In between: hold. The gap between the two marks keeps it from see-sawing, because easing off is exactly
    //    what raises the guest's frame rate.
    //  - A paused game, speed 1, no floor, or no report: nothing is eased, and with no floor or no guest the
    //    percentage returns to 100 at once.
    //
    // Like HostPacing, this changes how fast the host works through ticks, never which tick anything happens
    // on, so it cannot change what anyone simulates.
    public sealed class FrameRatePacing
    {
        public const int DownStepPercent = 10;
        public const int UpStepPercent = 5;
        public const int MinPercent = 30;
        private const int SamplesBeforeStep = 3;

        // The floors a host can choose from. 0 is off.
        public static readonly int[] Floors = { 0, 20, 30, 45, 60 };

        private int _samplesBelow;
        private int _samplesAbove;

        public int Percent { get; private set; } = 100;

        public bool IsEasing => Percent < 100;

        // A guest has to be this far clear of the floor before the host speeds back up.
        public static int RecoverAbove(int floor) => floor + Math.Max(5, floor / 4);

        public static int NextFloor(int floor)
        {
            int index = Array.IndexOf(Floors, floor);
            return Floors[(index + 1) % Floors.Length];
        }

        // One report. worstGuestFps is null when no guest has reported a frame rate.
        public void Sample(int floor, int? worstGuestFps, bool running)
        {
            if (floor <= 0 || worstGuestFps == null)
            {
                Percent = 100;
                _samplesBelow = _samplesAbove = 0;
                return;
            }
            if (!running)
            {
                // Paused or speed 1: the frame rate says nothing about what the chosen speed costs the guest.
                _samplesBelow = _samplesAbove = 0;
                return;
            }
            int fps = worstGuestFps.Value;
            if (fps < floor)
            {
                _samplesAbove = 0;
                if (++_samplesBelow >= SamplesBeforeStep)
                {
                    Percent = Math.Max(MinPercent, Percent - DownStepPercent);
                    _samplesBelow = 0;
                }
            }
            else if (fps >= RecoverAbove(floor))
            {
                _samplesBelow = 0;
                if (++_samplesAbove >= SamplesBeforeStep)
                {
                    Percent = Math.Min(100, Percent + UpStepPercent);
                    _samplesAbove = 0;
                }
            }
            else
            {
                _samplesBelow = _samplesAbove = 0;
            }
        }
    }
}

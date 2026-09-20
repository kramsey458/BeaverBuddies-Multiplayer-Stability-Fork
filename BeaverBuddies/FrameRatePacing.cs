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
    // window in the background is throttled by the system, which says nothing about the computer). One-second
    // figures are noisy: in a real session a guest averaging 25 fps reported anything from 2 to 50, because a
    // garbage collection or an autosave takes most of one second. So the rule works on the median of the last
    // five reports (SmoothedFps), which a bad second or two cannot move:
    //  - Median below the floor: the host drops 10% of the chosen speed, to a floor of 30%, and starts a fresh
    //    set of five reports, so the next decision only sees frame rates from after the drop.
    //  - Median at or above the floor with some headroom (RecoverAbove) for six reports in a row: back up 5%.
    //  - In between: hold.
    //  - The percentage the host was at when it had to drop is remembered as trouble: it does not climb back to
    //    it for a minute of play. Then it tries once. If that fails from the same percentage the wait doubles,
    //    up to four minutes. Without this it climbed straight back into the same trouble every twenty seconds.
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
        public const int WindowSize = 5;
        public const int SamplesBeforeRise = 6;
        public const int FirstRetryAfterSamples = 60;
        public const int LongestRetryAfterSamples = 240;

        // The floors a host can choose from. 0 is off.
        public static readonly int[] Floors = { 0, 20, 30, 45, 60 };

        private readonly int[] _window = new int[WindowSize];
        private readonly int[] _sorted = new int[WindowSize];
        private int _windowCount;
        private int _windowNext;
        private int _samplesAbove;
        private int _floor;
        // The percentage the host last had to drop from, and how long it stays out of reach.
        private int _troublePercent;
        private int _samplesUntilRetry;
        private int _retryAfterSamples = FirstRetryAfterSamples;

        public int Percent { get; private set; } = 100;

        public bool IsEasing => Percent < 100;

        // The median of the last five reports, which is what the rule last looked at. Null until there are five.
        public int? SmoothedFps { get; private set; }

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
                Forget();
                return;
            }
            if (floor != _floor)
            {
                // What was trouble for one floor says nothing about another.
                Forget();
                _floor = floor;
            }
            if (!running)
            {
                // Paused or speed 1: the frame rate says nothing about what the chosen speed costs the guest.
                ClearWindow();
                return;
            }
            if (_samplesUntilRetry > 0) _samplesUntilRetry--;

            _window[_windowNext] = worstGuestFps.Value;
            _windowNext = (_windowNext + 1) % WindowSize;
            if (_windowCount < WindowSize) _windowCount++;
            if (_windowCount < WindowSize) return;
            Array.Copy(_window, _sorted, WindowSize);
            Array.Sort(_sorted);
            int fps = _sorted[WindowSize / 2];
            SmoothedFps = fps;

            if (fps < floor)
            {
                if (Percent > MinPercent)
                {
                    _retryAfterSamples = Percent == _troublePercent
                        ? Math.Min(LongestRetryAfterSamples, _retryAfterSamples * 2)
                        : FirstRetryAfterSamples;
                    _troublePercent = Percent;
                    _samplesUntilRetry = _retryAfterSamples;
                    Percent = Math.Max(MinPercent, Percent - DownStepPercent);
                }
                ClearWindow();
            }
            else if (fps >= RecoverAbove(floor))
            {
                if (++_samplesAbove >= SamplesBeforeRise)
                {
                    _samplesAbove = 0;
                    int next = Math.Min(100, Percent + UpStepPercent);
                    if (_samplesUntilRetry == 0 || next < _troublePercent) Percent = next;
                }
            }
            else
            {
                _samplesAbove = 0;
            }
        }

        private void ClearWindow()
        {
            _windowCount = _windowNext = _samplesAbove = 0;
        }

        private void Forget()
        {
            ClearWindow();
            SmoothedFps = null;
            _troublePercent = 0;
            _samplesUntilRetry = 0;
            _retryAfterSamples = FirstRetryAfterSamples;
        }
    }
}

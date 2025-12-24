using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace MDDFoundation
{
    public sealed class DutyCycleThrottle
    {
        private readonly double _maxUsage;        // 0..1
        private readonly long _windowTicks;       // Stopwatch ticks
        private long _windowStart;

        private long _busyTicks;
        private long _sleepTicks;

        private long _curBusyStart = -1;
        private long _busyStreakTicks;

        private long _minSleepTicks;              // dynamic pacing sleep duration (Stopwatch ticks)

        // Tunables (constants, not parameters)
        private static readonly long BusyBurstThresholdTicks = (long)(Stopwatch.Frequency * 0.100);  // 100ms

        // We keep a small "settle-up" amount to be paid at the end of the window,
        // so that we don't oversleep early and end up underutilizing.
        // We'll target about 5% of required sleep, but never less than ~1ms.
        private static readonly double EndOfWindowReserveFraction = 0.05;

        private int callcount = 0;
        private int minsleepcount = 0;

        public void StartBusy() => _curBusyStart = Stopwatch.GetTimestamp();

        public DutyCycleThrottle(double maxUsage, TimeSpan window)
        {
            if (maxUsage <= 0 || maxUsage > 1) throw new ArgumentOutOfRangeException(nameof(maxUsage));
            if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));

            _maxUsage = maxUsage;

            _windowTicks = (long)(window.TotalSeconds * Stopwatch.Frequency);
            if (_windowTicks <= 0) throw new ArgumentOutOfRangeException(nameof(window));

            _windowStart = Stopwatch.GetTimestamp();

            // Start with a small micro-sleep (~1ms) and let it self-tune.
            _minSleepTicks = MsToTicksCeil(1);
        }

        public async Task ThrottleIfNeededAsync(CancellationToken token)
        {
            // If max usage is 100%, do nothing.
            if (_maxUsage >= 1.0)
                return;

            callcount++;

            long now = Stopwatch.GetTimestamp();

            // If caller forgot StartBusy(), don't let that explode.
            if (_curBusyStart < 0)
            {
                _curBusyStart = now;
                return;
            }

            // Account for busy time since last StartBusy()/Throttle call.
            long deltaBusy = now - _curBusyStart;
            if (deltaBusy > 0)
            {
                _busyTicks += deltaBusy;
                _busyStreakTicks += deltaBusy;
            }

            // Move the busy start forward so we don't double-count.
            _curBusyStart = now;

            // Micro-pacing: if we've been busy too long continuously, take a small nap.
            if (_busyStreakTicks >= BusyBurstThresholdTicks)
            {
                await PaceSleepAsync(_minSleepTicks, token).ConfigureAwait(false);
                minsleepcount++;
                _busyStreakTicks = 0;
                // After sleeping, reset the busy start to "now" so future busy deltas are correct.
                _curBusyStart = Stopwatch.GetTimestamp();
                now = _curBusyStart;
            }

            // If window ended, settle up to hit the exact duty cycle, then retune.
            long elapsed = now - _windowStart;
            if (elapsed >= _windowTicks)
            {
                // Required sleep to achieve maxUsage:
                // usage = busy / (busy + sleep)  =>  sleep = busy * (1-usage)/usage
                long targetSleepTicks = ComputeTargetSleepTicks(_busyTicks, _maxUsage);

                long remainingTicks = targetSleepTicks - _sleepTicks;
                if (remainingTicks > 0)
                {
                    await PaceSleepAsync(remainingTicks, token).ConfigureAwait(false);
                    _curBusyStart = Stopwatch.GetTimestamp();
                    now = _curBusyStart;
                }

                if (Debugger.IsAttached)
                {
                    double actualUsage = (_busyTicks + _sleepTicks) > 0 ?
                        (_busyTicks / (double)(_busyTicks + _sleepTicks)) : 0.0;
                    Debug.Write($"{DateTime.Now:u}-[DutyCycleThrottle] Window ended. " +
                        $"Busy: {_busyTicks:N0} ticks, Sleep: {_sleepTicks:N0} ticks, " +
                        $"Remaining Sleep: {remainingTicks:N0} ticks ({TicksToMsCeil(remainingTicks)} ms), " +
                        $"Actual Usage: {actualUsage:P2}, " +
                        $"Calls: {callcount}, MinSleep Calls: {minsleepcount}, " +
                        $"MinSleepTicks: {_minSleepTicks:N0} ticks ({TicksToMsCeil(_minSleepTicks)} ms)");
                }

                // Recalculate minSleepTicks for next window
                RetuneMinSleepTicks(_busyTicks, targetSleepTicks);


                if (Debugger.IsAttached)
                {
                    Debug.WriteLine($", New MinSleepTicks: {_minSleepTicks:N0} ticks ({TicksToMsCeil(_minSleepTicks)} ms)");
                }

                // Reset window counters
                _windowStart = now;
                _busyTicks = 0;
                _sleepTicks = 0;
                _busyStreakTicks = 0;
                callcount = 0;
                minsleepcount = 0;
            }
        }

        private void RetuneMinSleepTicks(long busyTicks, long targetSleepTicks)
        {
            // If we barely did any work or have no target sleep, keep it tiny.
            if (busyTicks <= 0 || targetSleepTicks <= 0)
            {
                _minSleepTicks = MsToTicksCeil(1);
                return;
            }

            // Reserve a little sleep for the end-of-window settle-up so we don't oversleep early.
            long reserveTicks = (long)(targetSleepTicks * EndOfWindowReserveFraction);
            long oneMs = MsToTicksCeil(1);
            if (reserveTicks < oneMs) reserveTicks = oneMs;
            if (reserveTicks > targetSleepTicks) reserveTicks = targetSleepTicks;

            long distributableTicks = targetSleepTicks - reserveTicks;
            if (distributableTicks <= 0)
            {
                _minSleepTicks = oneMs;
                return;
            }

            // Estimate how many micro-sleeps we'll get next window:
            // roughly one per busy-burst threshold of continuous busy time.
            // Clamp to at least 1 so we don't divide by zero.
            long estimatedPaceCount = _windowTicks / BusyBurstThresholdTicks;
            if (estimatedPaceCount < 1) estimatedPaceCount = 1;

            // Choose a min sleep such that paceCount * minSleep ~= distributableSleep.
            long candidate = distributableTicks / estimatedPaceCount;

            // Clamp candidate to sane bounds:
            // - at least ~1ms (or we won't reliably sleep at all)
            // - at most ~25ms so we don't create chunky pauses
            long maxCandidate = MsToTicksCeil(25);

            if (candidate < oneMs) candidate = oneMs;
            if (candidate > maxCandidate) candidate = maxCandidate;

            _minSleepTicks = candidate;
        }

        private async Task PaceSleepAsync(long ticksToSleep, CancellationToken token)
        {
            if (ticksToSleep <= 0)
                return;

            int ms = TicksToMsCeil(ticksToSleep);

            if (ms <= 0)
                return;

            long s0 = Stopwatch.GetTimestamp();
            await Task.Delay(ms, token).ConfigureAwait(false);
            long s1 = Stopwatch.GetTimestamp();

            long actual = s1 - s0;
            if (actual > 0)
                _sleepTicks += actual;
        }

        private static long ComputeTargetSleepTicks(long busyTicks, double maxUsage)
        {
            // sleep = busy * (1-u)/u
            if (busyTicks <= 0) return 0;
            if (maxUsage >= 1.0) return 0;

            double desired = busyTicks * (1.0 - maxUsage) / maxUsage;
            if (desired <= 0) return 0;

            // Avoid overflow; cap at long.MaxValue.
            if (desired >= long.MaxValue) return long.MaxValue;
            return (long)desired;
        }

        private static int TicksToMsCeil(long ticks)
        {
            double seconds = ticks / (double)Stopwatch.Frequency;
            double ms = seconds * 1000.0;
            if (ms <= 0) return 0;
            if (ms >= int.MaxValue) return int.MaxValue;
            return (int)Math.Ceiling(ms);
        }
        private static long MsToTicksCeil(int ms)
        {
            if (ms <= 0) return 0;
            double ticks = (ms / 1000.0) * Stopwatch.Frequency;
            if (ticks >= long.MaxValue) return long.MaxValue;
            return (long)Math.Ceiling(ticks);
        }
    }
}

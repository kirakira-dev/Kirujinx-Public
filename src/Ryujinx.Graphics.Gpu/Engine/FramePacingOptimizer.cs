using System;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.Graphics.Gpu.Engine
{
    public class FramePacingOptimizer
    {
        private const int FrameHistorySize = 60;
        private const double JitterThresholdMs = 5.0;
        private const double HeavyLoadThresholdMs = 50.0;

        private readonly long[] _frameTimes;
        private int _frameIndex;
        private long _lastFrameTicks;

        private volatile bool _heavyLoadMode;
        private volatile int _frameSkipBudget;
        private long _heavyLoadStartTicks;

        private long _framesProcessed;
        private long _framesSkipped;
        private double _averageFrameTimeMs;

        public bool InHeavyLoadMode => _heavyLoadMode;
        public int FrameSkipBudget => _frameSkipBudget;
        public double AverageFrameTimeMs => _averageFrameTimeMs;
        public long FramesProcessed => _framesProcessed;
        public long FramesSkipped => _framesSkipped;

        public FramePacingOptimizer()
        {
            _frameTimes = new long[FrameHistorySize];
            _lastFrameTicks = Stopwatch.GetTimestamp();
        }

        public void BeginFrame()
        {
            long currentTicks = Stopwatch.GetTimestamp();
            long frameTicks = currentTicks - _lastFrameTicks;
            _lastFrameTicks = currentTicks;

            _frameTimes[_frameIndex] = frameTicks;
            _frameIndex = (_frameIndex + 1) % FrameHistorySize;
            _framesProcessed++;

            UpdateAverageFrameTime();

            double frameTimeMs = (double)frameTicks / Stopwatch.Frequency * 1000.0;
            if (frameTimeMs > HeavyLoadThresholdMs)
            {
                EnterHeavyLoadMode();
            }
        }

        public bool EndFrame()
        {
            if (_heavyLoadMode)
            {
                long ticksPerMs = Stopwatch.Frequency / 1000;
                long elapsedMs = (Stopwatch.GetTimestamp() - _heavyLoadStartTicks) / ticksPerMs;

                if (elapsedMs > 500 && _averageFrameTimeMs < HeavyLoadThresholdMs / 2)
                {
                    ExitHeavyLoadMode();
                }

                if (_frameSkipBudget > 0 && ShouldSkipFrame())
                {
                    Interlocked.Decrement(ref _frameSkipBudget);
                    Interlocked.Increment(ref _framesSkipped);
                    return false;
                }
            }

            return true;
        }

        public long GetRecommendedSleepTime(long targetFrameTimeNs, long elapsedNs)
        {
            if (_heavyLoadMode)
            {
                return 0;
            }

            long remainingNs = targetFrameTimeNs - elapsedNs;

            if (_averageFrameTimeMs > 16.7 * 1.1)
            {
                remainingNs = Math.Max(0, remainingNs - 1_000_000);
            }

            return Math.Max(0, remainingNs);
        }

        public int GetRecommendedVSyncInterval(int normalInterval)
        {
            if (_heavyLoadMode && _averageFrameTimeMs > 33.3)
            {
                return 0;
            }

            return normalInterval;
        }

        private void EnterHeavyLoadMode()
        {
            if (!_heavyLoadMode)
            {
                _heavyLoadMode = true;
                _heavyLoadStartTicks = Stopwatch.GetTimestamp();
                _frameSkipBudget = 3;
            }
        }

        private void ExitHeavyLoadMode()
        {
            _heavyLoadMode = false;
            _frameSkipBudget = 0;
        }

        private bool ShouldSkipFrame()
        {
            return _averageFrameTimeMs > HeavyLoadThresholdMs * 1.5;
        }

        private void UpdateAverageFrameTime()
        {
            long totalTicks = 0;
            int count = (int)Math.Min(_framesProcessed, FrameHistorySize);

            for (int i = 0; i < count; i++)
            {
                totalTicks += _frameTimes[i];
            }

            if (count > 0)
            {
                double avgTicks = (double)totalTicks / count;
                _averageFrameTimeMs = avgTicks / Stopwatch.Frequency * 1000.0;
            }
        }

        public bool DetectJitter()
        {
            if (_framesProcessed < FrameHistorySize)
            {
                return false;
            }

            double mean = _averageFrameTimeMs;
            double sumSquares = 0;

            for (int i = 0; i < FrameHistorySize; i++)
            {
                double frameTimeMs = (double)_frameTimes[i] / Stopwatch.Frequency * 1000.0;
                double diff = frameTimeMs - mean;
                sumSquares += diff * diff;
            }

            double stdDev = Math.Sqrt(sumSquares / FrameHistorySize);
            return stdDev > JitterThresholdMs;
        }

        public string GetStatistics()
        {
            return $"Avg Frame: {_averageFrameTimeMs:F2}ms, Processed: {_framesProcessed}, Skipped: {_framesSkipped}, Heavy Load: {_heavyLoadMode}";
        }

        public void Reset()
        {
            Array.Clear(_frameTimes, 0, _frameTimes.Length);
            _frameIndex = 0;
            _lastFrameTicks = Stopwatch.GetTimestamp();
            _heavyLoadMode = false;
            _frameSkipBudget = 0;
            _framesProcessed = 0;
            _framesSkipped = 0;
            _averageFrameTimeMs = 0;
        }
    }
}

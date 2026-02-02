using System;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.Common.Utilities
{
    public static class FpsLockManager
    {
        private static bool _lockTo30Fps;
        private static long _lastFrameTimestamp;
        private static long _frameCount;
        private static double _currentFps;
        private static double _frameTimeMs;
        
        private const double TargetFrameTimeMs = 33.333;
        private const double LoadingBudgetMs = 15.0;
        private const long FpsUpdateInterval = 30;
        
        private static readonly object _syncLock = new();

        static FpsLockManager()
        {
            _lastFrameTimestamp = Stopwatch.GetTimestamp();
        }

        public static bool LockTo30Fps
        {
            get => _lockTo30Fps;
            set => _lockTo30Fps = value;
        }

        public static double CurrentFps => _currentFps;
        public static double FrameTimeMs => _frameTimeMs;
        public static bool IsLocked => _lockTo30Fps;
        
        public static double TargetFrameTime => _lockTo30Fps ? TargetFrameTimeMs : 16.666;
        
        public static double LoadingBudget => _lockTo30Fps ? LoadingBudgetMs : 5.0;

        public static bool ShouldPrioritizeLoading()
        {
            if (!_lockTo30Fps)
            {
                return false;
            }

            return true;
        }

        public static double GetRemainingFrameBudgetMs()
        {
            if (!_lockTo30Fps)
            {
                return 0;
            }

            long currentTimestamp = Stopwatch.GetTimestamp();
            double elapsedMs = (currentTimestamp - _lastFrameTimestamp) * 1000.0 / Stopwatch.Frequency;
            double remaining = TargetFrameTimeMs - elapsedMs;

            return Math.Max(0, remaining);
        }

        public static double GetLoadingBudgetMs()
        {
            if (!_lockTo30Fps)
            {
                return 5.0;
            }

            double remaining = GetRemainingFrameBudgetMs();

            return Math.Min(LoadingBudgetMs, remaining);
        }

        public static int GetMaxWorkItemsForLoading()
        {
            if (!_lockTo30Fps)
            {
                return 4;
            }

            return 12;
        }

        public static int GetMaxShadersPerFrame()
        {
            if (!_lockTo30Fps)
            {
                return 2;
            }

            return 6;
        }

        public static int GetMaxTexturesPerFrame()
        {
            if (!_lockTo30Fps)
            {
                return 5;
            }

            return 15;
        }

        public static void BeginFrame()
        {
            long currentTimestamp = Stopwatch.GetTimestamp();
            
            lock (_syncLock)
            {
                double frameTime = (currentTimestamp - _lastFrameTimestamp) * 1000.0 / Stopwatch.Frequency;
                _frameTimeMs = frameTime;
                _frameCount++;

                if (_frameCount % FpsUpdateInterval == 0)
                {
                    _currentFps = 1000.0 / frameTime;
                }

                _lastFrameTimestamp = currentTimestamp;
            }
        }

        public static void EndFrameAndWait()
        {
            if (!_lockTo30Fps)
            {
                return;
            }

            long currentTimestamp = Stopwatch.GetTimestamp();
            double elapsedMs = (currentTimestamp - _lastFrameTimestamp) * 1000.0 / Stopwatch.Frequency;
            double remainingMs = TargetFrameTimeMs - elapsedMs;

            if (remainingMs > 1.0)
            {
                int sleepMs = (int)(remainingMs - 1.0);
                if (sleepMs > 0)
                {
                    Thread.Sleep(sleepMs);
                }

                while (true)
                {
                    currentTimestamp = Stopwatch.GetTimestamp();
                    elapsedMs = (currentTimestamp - _lastFrameTimestamp) * 1000.0 / Stopwatch.Frequency;
                    if (elapsedMs >= TargetFrameTimeMs)
                    {
                        break;
                    }
                    Thread.SpinWait(100);
                }
            }
        }

        public static void UseLoadingBudget(Action loadingWork)
        {
            if (loadingWork == null)
            {
                return;
            }

            double budget = GetLoadingBudgetMs();
            if (budget <= 0)
            {
                return;
            }

            long startTime = Stopwatch.GetTimestamp();
            
            try
            {
                loadingWork();
            }
            catch
            {
            }

            long endTime = Stopwatch.GetTimestamp();
            double usedMs = (endTime - startTime) * 1000.0 / Stopwatch.Frequency;
        }

        public static bool CanDoMoreLoadingWork()
        {
            if (!_lockTo30Fps)
            {
                return false;
            }

            return GetRemainingFrameBudgetMs() > 2.0;
        }

        public static string GetStatistics()
        {
            return $"FPS: {_currentFps:F1}, Frame: {_frameTimeMs:F2}ms, " +
                   $"Locked: {_lockTo30Fps}, Budget: {GetLoadingBudgetMs():F1}ms";
        }
    }
}

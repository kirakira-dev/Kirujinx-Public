using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Ryujinx.Common.Utilities
{
    public static class StutterReductionManager
    {
        private static bool _enabled;
        private static readonly ConcurrentDictionary<ulong, long> _recentTranslations;
        private static readonly ConcurrentDictionary<ulong, int> _shaderCompilationTimes;
        private static long _totalTranslationTime;
        private static long _totalShaderTime;
        private static int _translationCount;
        private static int _shaderCount;
        private static int _stutterEvents;

        private const long StutterThresholdMs = 16;
        private const int RecentTranslationWindow = 1000;

        static StutterReductionManager()
        {
            _recentTranslations = new ConcurrentDictionary<ulong, long>();
            _shaderCompilationTimes = new ConcurrentDictionary<ulong, int>();
        }

        public static bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        public static float AverageTranslationTimeMs => _translationCount > 0 
            ? (float)_totalTranslationTime / _translationCount 
            : 0;

        public static float AverageShaderTimeMs => _shaderCount > 0 
            ? (float)_totalShaderTime / _shaderCount 
            : 0;

        public static int StutterEvents => _stutterEvents;
        public static int TranslationCount => _translationCount;
        public static int ShaderCount => _shaderCount;

        public static void RecordTranslation(ulong address, long elapsedMs)
        {
            if (!_enabled)
            {
                return;
            }

            Interlocked.Add(ref _totalTranslationTime, elapsedMs);
            Interlocked.Increment(ref _translationCount);

            if (elapsedMs > StutterThresholdMs)
            {
                Interlocked.Increment(ref _stutterEvents);
            }

            long currentTime = Environment.TickCount64;
            _recentTranslations[address] = currentTime;

            CleanupOldEntries(currentTime);
        }

        public static void RecordShaderCompilation(ulong hash, int elapsedMs)
        {
            if (!_enabled)
            {
                return;
            }

            Interlocked.Add(ref _totalShaderTime, elapsedMs);
            Interlocked.Increment(ref _shaderCount);

            if (elapsedMs > StutterThresholdMs)
            {
                Interlocked.Increment(ref _stutterEvents);
            }

            _shaderCompilationTimes[hash] = elapsedMs;
        }

        public static bool WasRecentlyTranslated(ulong address)
        {
            if (_recentTranslations.TryGetValue(address, out long timestamp))
            {
                return Environment.TickCount64 - timestamp < RecentTranslationWindow;
            }
            return false;
        }

        public static int GetShaderCompilationTime(ulong hash)
        {
            return _shaderCompilationTimes.TryGetValue(hash, out int time) ? time : -1;
        }

        public static bool ShouldYieldForCompilation()
        {
            if (!_enabled)
            {
                return false;
            }

            return _stutterEvents > 10 && AverageTranslationTimeMs > StutterThresholdMs;
        }

        public static int GetRecommendedBatchSize()
        {
            if (!_enabled)
            {
                return 4;
            }

            if (AverageTranslationTimeMs < 5)
            {
                return 16;
            }
            else if (AverageTranslationTimeMs < 10)
            {
                return 8;
            }
            else if (AverageTranslationTimeMs < 20)
            {
                return 4;
            }
            else
            {
                return 2;
            }
        }

        public static int GetRecommendedThreadCount()
        {
            int processorCount = Environment.ProcessorCount;

            if (!_enabled)
            {
                return Math.Max(2, processorCount / 4);
            }

            if (OnDemandResourceManager.EnableOnDemandUsage)
            {
                return Math.Max(4, processorCount - 1);
            }

            return Math.Max(2, processorCount / 2);
        }

        private static void CleanupOldEntries(long currentTime)
        {
            foreach (var kvp in _recentTranslations)
            {
                if (currentTime - kvp.Value > RecentTranslationWindow * 10)
                {
                    _recentTranslations.TryRemove(kvp.Key, out _);
                }
            }
        }

        public static void Reset()
        {
            _recentTranslations.Clear();
            _shaderCompilationTimes.Clear();
            Interlocked.Exchange(ref _totalTranslationTime, 0);
            Interlocked.Exchange(ref _totalShaderTime, 0);
            Interlocked.Exchange(ref _translationCount, 0);
            Interlocked.Exchange(ref _shaderCount, 0);
            Interlocked.Exchange(ref _stutterEvents, 0);
        }

        public static string GetStatistics()
        {
            return $"Translations: {_translationCount} (avg {AverageTranslationTimeMs:F2}ms), " +
                   $"Shaders: {_shaderCount} (avg {AverageShaderTimeMs:F2}ms), " +
                   $"Stutters: {_stutterEvents}";
        }
    }
}

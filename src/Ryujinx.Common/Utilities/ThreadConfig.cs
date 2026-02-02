using System;
using System.Threading;

namespace Ryujinx.Common.Utilities
{
    public static class ThreadConfig
    {
        public const float PerformanceMultiplier = 1.5f;

        private static int? _cachedProcessorCount;
        private static int? _cachedOptimalThreadCount;
        private static bool _initialized = false;

        public static int ProcessorCount
        {
            get
            {
                _cachedProcessorCount ??= Environment.ProcessorCount;
                return _cachedProcessorCount.Value;
            }
        }

        public static int OptimalThreadCount
        {
            get
            {
                if (!_cachedOptimalThreadCount.HasValue)
                {
                    int baseCount = ProcessorCount;
                    int optimal = (int)Math.Ceiling(baseCount * PerformanceMultiplier);
                    _cachedOptimalThreadCount = Math.Max(2, optimal);
                }
                return _cachedOptimalThreadCount.Value;
            }
        }

        public static int GetScaledThreadCount(int baseCount)
        {
            return Math.Max(1, (int)Math.Ceiling(baseCount * PerformanceMultiplier));
        }

        public static int GetBackgroundTranslationThreadCount()
        {
            int baseCount = Math.Max(1, (ProcessorCount - 4) / 2);
            int scaled = GetScaledThreadCount(baseCount);
            return Math.Clamp(scaled, 2, Math.Max(8, ProcessorCount));
        }

        public static int GetShaderCompilationThreadCount()
        {
            int baseCount = Math.Max(1, (ProcessorCount - 2) / 2);
            int scaled = GetScaledThreadCount(baseCount);
            return Math.Clamp(scaled, 2, Math.Max(8, ProcessorCount - 2));
        }

        public static int GetPtcTranslationThreadCount()
        {
            int baseCount = ProcessorCount;
            int scaled = GetScaledThreadCount(baseCount);
            if (scaled > 4)
            {
                scaled--;
            }
            return Math.Max(2, scaled);
        }

        public static int GetDiskCacheLoaderThreadCount()
        {
            return Math.Max(4, GetShaderCompilationThreadCount() * 2);
        }

        public static int GetVideoDecoderThreadCount()
        {
            int baseCount = Math.Max(2, ProcessorCount / 2);
            return GetScaledThreadCount(baseCount);
        }

        public static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;

            int minWorkerThreads = GetScaledThreadCount(ProcessorCount);
            int minCompletionPortThreads = Math.Max(4, ProcessorCount / 2);

            ThreadPool.GetMinThreads(out int currentWorker, out int currentCompletion);

            if (currentWorker < minWorkerThreads || currentCompletion < minCompletionPortThreads)
            {
                ThreadPool.SetMinThreads(
                    Math.Max(currentWorker, minWorkerThreads),
                    Math.Max(currentCompletion, minCompletionPortThreads)
                );
            }

            ThreadPool.GetMaxThreads(out int maxWorker, out int maxCompletion);
            int desiredMaxWorker = Math.Max(maxWorker, minWorkerThreads * 4);
            int desiredMaxCompletion = Math.Max(maxCompletion, minCompletionPortThreads * 4);

            if (maxWorker < desiredMaxWorker || maxCompletion < desiredMaxCompletion)
            {
                ThreadPool.SetMaxThreads(
                    Math.Max(maxWorker, desiredMaxWorker),
                    Math.Max(maxCompletion, desiredMaxCompletion)
                );
            }
        }

        public static (int MinWorker, int MinCompletion, int MaxWorker, int MaxCompletion) GetThreadPoolStats()
        {
            ThreadPool.GetMinThreads(out int minWorker, out int minCompletion);
            ThreadPool.GetMaxThreads(out int maxWorker, out int maxCompletion);
            return (minWorker, minCompletion, maxWorker, maxCompletion);
        }

        public static int GetRecommendedParallelism()
        {
            return Math.Max(2, GetScaledThreadCount(ProcessorCount - 1));
        }

        public static int GetMemoryIntensiveTaskThreads()
        {
            int baseCount = Math.Max(1, ProcessorCount / 4);
            return GetScaledThreadCount(baseCount);
        }
    }
}

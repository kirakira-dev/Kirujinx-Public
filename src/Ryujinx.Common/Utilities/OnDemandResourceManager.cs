using System;
using System.Threading;

namespace Ryujinx.Common.Utilities
{
    public static class OnDemandResourceManager
    {
        private static bool _enableOnDemandUsage;
        private static bool _initialized;

        public static bool EnableOnDemandUsage
        {
            get => _enableOnDemandUsage;
            set
            {
                _enableOnDemandUsage = value;
                if (value && !_initialized)
                {
                    ApplyMaximumResourceUsage();
                    _initialized = true;
                }
            }
        }

        public static float ThreadMultiplier => _enableOnDemandUsage ? 2.5f : 1.5f;
        public static int MinThreadPoolThreads => _enableOnDemandUsage ? Environment.ProcessorCount * 4 : Environment.ProcessorCount * 2;
        public static int MaxThreadPoolThreads => _enableOnDemandUsage ? Environment.ProcessorCount * 8 : Environment.ProcessorCount * 4;

        public static int GetOptimalThreadCount(int baseCount)
        {
            if (_enableOnDemandUsage)
            {
                return Math.Max(baseCount, (int)Math.Ceiling(Environment.ProcessorCount * ThreadMultiplier));
            }
            return baseCount;
        }

        public static int GetShaderCompilationThreads()
        {
            int processorCount = Environment.ProcessorCount;
            if (_enableOnDemandUsage)
            {
                return Math.Max(8, processorCount - 1);
            }
            int baseCount = Math.Max(1, (processorCount - 2) / 2);
            return Math.Clamp((int)Math.Ceiling(baseCount * 1.5f), 2, Math.Max(8, processorCount - 2));
        }

        public static int GetTranslationThreads()
        {
            int processorCount = Environment.ProcessorCount;
            if (_enableOnDemandUsage)
            {
                return Math.Max(8, processorCount);
            }
            int baseCount = Math.Max(1, (processorCount - 4) / 2);
            return Math.Clamp((int)Math.Ceiling(baseCount * 1.5f), 2, Math.Max(8, processorCount));
        }

        public static int GetDiskCacheLoaderThreads()
        {
            int processorCount = Environment.ProcessorCount;
            if (_enableOnDemandUsage)
            {
                return Math.Max(16, processorCount * 2);
            }
            int baseCount = GetShaderCompilationThreads();
            int scaled = (int)Math.Ceiling(baseCount * 1.5f) * 2;
            return Math.Max(4, Math.Min(scaled, processorCount * 2));
        }

        public static int GetVideoDecoderThreads()
        {
            int processorCount = Environment.ProcessorCount;
            if (_enableOnDemandUsage)
            {
                return Math.Max(8, processorCount);
            }
            int baseThreads = Math.Max(2, processorCount / 2);
            return Math.Min(Math.Max(6, processorCount), (int)Math.Ceiling(baseThreads * 1.5f));
        }

        private static void ApplyMaximumResourceUsage()
        {
            int processorCount = Environment.ProcessorCount;
            int minWorkerThreads = processorCount * 4;
            int minCompletionThreads = processorCount * 2;
            int maxWorkerThreads = processorCount * 8;
            int maxCompletionThreads = processorCount * 4;

            ThreadPool.SetMinThreads(minWorkerThreads, minCompletionThreads);
            ThreadPool.SetMaxThreads(maxWorkerThreads, maxCompletionThreads);
        }

        public static void Initialize()
        {
            if (_enableOnDemandUsage && !_initialized)
            {
                ApplyMaximumResourceUsage();
                _initialized = true;
            }
        }
    }
}

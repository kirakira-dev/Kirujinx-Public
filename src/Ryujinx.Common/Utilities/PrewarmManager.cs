using Ryujinx.Common.Logging;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Common.Utilities
{
    public static class PrewarmManager
    {
        private static bool _isInitialized = false;
        private static readonly object _lock = new();

        public static event Action<string, int, int> ProgressChanged;
        public static event Action<TimeSpan> PrewarmCompleted;
        public static bool IsInitialized => _isInitialized;

        public static void Initialize()
        {
            lock (_lock)
            {
                if (_isInitialized)
                {
                    return;
                }

                var stopwatch = Stopwatch.StartNew();
                Logger.Info?.Print(LogClass.Application, "Starting prewarm initialization...");

                int totalSteps = 4;
                int currentStep = 0;

                try
                {
                    ReportProgress("Prewarming object pools...", ++currentStep, totalSteps);
                    PrewarmObjectPools();

                    ReportProgress("Prewarming audio system...", ++currentStep, totalSteps);
                    PrewarmAudioPools();

                    ReportProgress("Prewarming memory allocators...", ++currentStep, totalSteps);
                    PrewarmMemoryAllocators();

                    ReportProgress("Starting background systems...", ++currentStep, totalSteps);
                    InitializeBackgroundSystems();

                    _isInitialized = true;
                    stopwatch.Stop();

                    Logger.Info?.Print(LogClass.Application, $"Prewarm initialization completed in {stopwatch.ElapsedMilliseconds}ms");
                    PrewarmCompleted?.Invoke(stopwatch.Elapsed);
                }
                catch (Exception ex)
                {
                    Logger.Error?.Print(LogClass.Application, $"Prewarm initialization failed: {ex.Message}");
                }
            }
        }

        public static Task InitializeAsync()
        {
            return Task.Run(Initialize);
        }

        private static void ReportProgress(string message, int current, int total)
        {
            Logger.Debug?.Print(LogClass.Application, $"Prewarm [{current}/{total}]: {message}");
            ProgressChanged?.Invoke(message, current, total);
        }

        private static void PrewarmObjectPools()
        {
            try
            {
                var tempArrays = new byte[16][];
                for (int i = 0; i < 16; i++)
                {
                    tempArrays[i] = new byte[1024 * (i + 1)];
                }

                for (int i = 0; i < 16; i++)
                {
                    tempArrays[i] = null;
                }
            }
            catch
            {
            }
        }

        private static void PrewarmAudioPools()
        {
            try
            {
                var audioType = Type.GetType("Ryujinx.Audio.Renderer.Server.CommandBuffer, Ryujinx.Audio");
                if (audioType != null)
                {
                    var prewarmMethod = audioType.GetMethod("PrewarmCommandPools",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    prewarmMethod?.Invoke(null, null);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"Audio prewarm skipped: {ex.Message}");
            }
        }

        private static void PrewarmMemoryAllocators()
        {
            try
            {
                for (int i = 0; i < 32; i++)
                {
                    var temp = new byte[1024 * (i + 1)];
                    Array.Fill<byte>(temp, 0);
                }

                for (int i = 0; i < 8; i++)
                {
                    var temp = new byte[65536 * (i + 1)];
                    Array.Fill<byte>(temp, 0);
                }

                GC.Collect(0, GCCollectionMode.Optimized, false);
            }
            catch
            {
            }
        }

        private static void InitializeBackgroundSystems()
        {
            try
            {
                int processorCount = Environment.ProcessorCount;
                int scaledThreads = (int)Math.Ceiling(processorCount * 1.5f);

                ThreadPool.GetMinThreads(out int workerThreads, out int completionPortThreads);
                int minWorkerThreads = Math.Max(scaledThreads, Math.Max(workerThreads, 8));
                int minCompletionThreads = Math.Max(processorCount / 2, Math.Max(completionPortThreads, 4));

                ThreadPool.SetMinThreads(minWorkerThreads, minCompletionThreads);

                ThreadPool.GetMaxThreads(out int maxWorker, out int maxCompletion);
                int desiredMaxWorker = Math.Max(maxWorker, minWorkerThreads * 4);
                int desiredMaxCompletion = Math.Max(maxCompletion, minCompletionThreads * 4);

                if (maxWorker < desiredMaxWorker || maxCompletion < desiredMaxCompletion)
                {
                    ThreadPool.SetMaxThreads(
                        Math.Max(maxWorker, desiredMaxWorker),
                        Math.Max(maxCompletion, desiredMaxCompletion)
                    );
                }

                for (int i = 0; i < 8; i++)
                {
                    ThreadPool.QueueUserWorkItem(_ => { });
                }

                Logger.Debug?.Print(LogClass.Application,
                    $"ThreadPool configured: min={minWorkerThreads}/{minCompletionThreads}, " +
                    $"max={desiredMaxWorker}/{desiredMaxCompletion} (1.5x multiplier applied)");
            }
            catch
            {
            }
        }

        public static void PrewarmGpu()
        {
            Logger.Debug?.Print(LogClass.Application, "GPU prewarm requested");
        }

        public static void Reset()
        {
            lock (_lock)
            {
                _isInitialized = false;
            }
        }
    }
}

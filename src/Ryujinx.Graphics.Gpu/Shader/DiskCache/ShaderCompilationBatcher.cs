using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Graphics.Gpu.Shader.DiskCache
{
    internal sealed class ShaderCompilationBatcher : IDisposable
    {
        private readonly struct CompilationRequest
        {
            public readonly int Id;
            public readonly ShaderSource[] Sources;
            public readonly ShaderInfo Info;
            public readonly Action<IProgram, int> Callback;
            public readonly int Priority;
            public readonly long QueueTime;

            public CompilationRequest(int id, ShaderSource[] sources, ShaderInfo info, Action<IProgram, int> callback, int priority)
            {
                Id = id;
                Sources = sources;
                Info = info;
                Callback = callback;
                Priority = priority;
                QueueTime = Stopwatch.GetTimestamp();
            }
        }

        private readonly IRenderer _renderer;
        private readonly ConcurrentQueue<CompilationRequest> _criticalQueue;
        private readonly ConcurrentQueue<CompilationRequest> _highQueue;
        private readonly ConcurrentQueue<CompilationRequest> _normalQueue;
        private readonly ConcurrentQueue<CompilationRequest> _lowQueue;
        private readonly Thread[] _compilerThreads;
        private readonly SemaphoreSlim _workSemaphore;
        private volatile bool _running;

        private int _compiledCount;
        private int _pendingCount;
        private int _failedCount;
        private long _totalCompileTimeMs;
        private int _batchesProcessed;

        private const int BatchSize = 4;
        private const int MaxPendingPerThread = 8;

        public int CompiledCount => _compiledCount;
        public int PendingCount => _pendingCount;
        public int FailedCount => _failedCount;
        public float AverageCompileTimeMs => _compiledCount > 0 ? (float)_totalCompileTimeMs / _compiledCount : 0;
        public bool HasPendingWork => _pendingCount > 0;

        public ShaderCompilationBatcher(IRenderer renderer, int threadCount = 0)
        {
            _renderer = renderer;
            _criticalQueue = new ConcurrentQueue<CompilationRequest>();
            _highQueue = new ConcurrentQueue<CompilationRequest>();
            _normalQueue = new ConcurrentQueue<CompilationRequest>();
            _lowQueue = new ConcurrentQueue<CompilationRequest>();
            _workSemaphore = new SemaphoreSlim(0);
            _running = true;

            if (threadCount <= 0)
            {
                int processorCount = Environment.ProcessorCount;
                int baseCount = Math.Max(2, (processorCount - 2) / 2);
                threadCount = (int)Math.Ceiling(baseCount * 1.5f);
                threadCount = Math.Clamp(threadCount, 2, Math.Max(8, processorCount - 2));
            }

            _compilerThreads = new Thread[threadCount];
            for (int i = 0; i < threadCount; i++)
            {
                _compilerThreads[i] = new Thread(CompilerLoop)
                {
                    Name = $"GPU.ShaderBatchCompiler.{i}",
                    Priority = ThreadPriority.BelowNormal,
                    IsBackground = true
                };
                _compilerThreads[i].Start();
            }

            Logger.Info?.Print(LogClass.Gpu, $"ShaderCompilationBatcher started with {threadCount} compiler threads");
        }

        public void QueueCompilation(int id, ShaderSource[] sources, ShaderInfo info, Action<IProgram, int> callback, int priority = 1)
        {
            Interlocked.Increment(ref _pendingCount);

            var request = new CompilationRequest(id, sources, info, callback, priority);

            switch (priority)
            {
                case >= 3:
                    _criticalQueue.Enqueue(request);
                    break;
                case 2:
                    _highQueue.Enqueue(request);
                    break;
                case 1:
                    _normalQueue.Enqueue(request);
                    break;
                default:
                    _lowQueue.Enqueue(request);
                    break;
            }

            _workSemaphore.Release();
        }

        public void QueueBatch(IEnumerable<(int Id, ShaderSource[] Sources, ShaderInfo Info, Action<IProgram, int> Callback)> batch, int priority = 1)
        {
            foreach (var (id, sources, info, callback) in batch)
            {
                QueueCompilation(id, sources, info, callback, priority);
            }
        }

        public IProgram CompileSync(ShaderSource[] sources, ShaderInfo info)
        {
            long startTime = Stopwatch.GetTimestamp();

            IProgram program = _renderer.CreateProgram(sources, info);

            long elapsedMs = (Stopwatch.GetTimestamp() - startTime) * 1000 / Stopwatch.Frequency;
            Interlocked.Add(ref _totalCompileTimeMs, elapsedMs);
            Interlocked.Increment(ref _compiledCount);

            return program;
        }

        public async Task<IProgram> CompileAsync(ShaderSource[] sources, ShaderInfo info, CancellationToken cancellationToken = default)
        {
            TaskCompletionSource<IProgram> tcs = new();

            QueueCompilation(
                -1,
                sources,
                info,
                (program, _) => tcs.TrySetResult(program),
                2);

            using var registration = cancellationToken.Register(() => tcs.TrySetCanceled());

            return await tcs.Task.ConfigureAwait(false);
        }

        public void ProcessBatchNow(int maxCount = BatchSize)
        {
            List<CompilationRequest> batch = new(maxCount);

            while (batch.Count < maxCount && TryDequeue(out CompilationRequest request))
            {
                batch.Add(request);
            }

            if (batch.Count == 0)
            {
                return;
            }

            Parallel.ForEach(batch, request =>
            {
                ProcessRequest(request);
            });

            Interlocked.Increment(ref _batchesProcessed);
        }

        public void WaitForCompletion(int timeoutMs = 10000)
        {
            int waited = 0;
            while (_pendingCount > 0 && waited < timeoutMs)
            {
                Thread.Sleep(10);
                waited += 10;
            }
        }

        public void WaitForCritical(int timeoutMs = 5000)
        {
            int waited = 0;
            while (!_criticalQueue.IsEmpty && waited < timeoutMs)
            {
                Thread.Sleep(5);
                waited += 5;
            }
        }

        private void CompilerLoop()
        {
            CompilationRequest[] localBatch = new CompilationRequest[BatchSize];

            while (_running)
            {
                if (!_workSemaphore.Wait(100))
                {
                    continue;
                }

                int batchCount = 0;

                while (batchCount < BatchSize && TryDequeue(out CompilationRequest request))
                {
                    localBatch[batchCount++] = request;

                    if (batchCount < BatchSize)
                    {
                        _workSemaphore.Wait(0);
                    }
                }

                for (int i = 0; i < batchCount; i++)
                {
                    if (!_running)
                    {
                        break;
                    }

                    ProcessRequest(localBatch[i]);
                }

                if (batchCount > 1)
                {
                    Interlocked.Increment(ref _batchesProcessed);
                }
            }
        }

        private bool TryDequeue(out CompilationRequest request)
        {
            if (_criticalQueue.TryDequeue(out request))
            {
                return true;
            }

            if (_highQueue.TryDequeue(out request))
            {
                return true;
            }

            if (_normalQueue.TryDequeue(out request))
            {
                return true;
            }

            if (_lowQueue.TryDequeue(out request))
            {
                return true;
            }

            return false;
        }

        private void ProcessRequest(CompilationRequest request)
        {
            long startTime = Stopwatch.GetTimestamp();
            IProgram program = null;

            try
            {
                program = _renderer.CreateProgram(request.Sources, request.Info);

                long elapsedMs = (Stopwatch.GetTimestamp() - startTime) * 1000 / Stopwatch.Frequency;
                Interlocked.Add(ref _totalCompileTimeMs, elapsedMs);
                Interlocked.Increment(ref _compiledCount);
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"Shader compilation failed for id {request.Id}: {ex.Message}");
                Interlocked.Increment(ref _failedCount);
            }
            finally
            {
                Interlocked.Decrement(ref _pendingCount);
            }

            try
            {
                request.Callback?.Invoke(program, request.Id);
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"Shader compilation callback failed for id {request.Id}: {ex.Message}");
            }
        }

        public string GetStatistics()
        {
            return $"Compiled: {_compiledCount}, Failed: {_failedCount}, Pending: {_pendingCount}, " +
                   $"Avg: {AverageCompileTimeMs:F2}ms, Batches: {_batchesProcessed}";
        }

        public void Dispose()
        {
            _running = false;

            for (int i = 0; i < _compilerThreads.Length; i++)
            {
                _workSemaphore.Release();
            }

            foreach (Thread thread in _compilerThreads)
            {
                thread.Join(1000);
            }

            _workSemaphore.Dispose();

            Logger.Info?.Print(LogClass.Gpu, $"ShaderCompilationBatcher stats: {GetStatistics()}");
        }
    }
}

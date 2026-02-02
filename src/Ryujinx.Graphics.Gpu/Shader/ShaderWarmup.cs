using Ryujinx.Common.Logging;
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Ryujinx.Graphics.Gpu.Shader
{
    public class ShaderWarmup : IDisposable
    {
        private readonly GpuContext _context;
        private readonly ConcurrentQueue<ShaderWarmupRequest> _warmupQueue;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly Thread _warmupThread;
        private readonly AutoResetEvent _workAvailable;
        private volatile bool _isRunning;
        private volatile bool _isPaused;

        private int _shadersWarmedUp;
        private int _shadersPending;

        public int ShadersWarmedUp => _shadersWarmedUp;
        public int ShadersPending => _shadersPending;
        public bool IsRunning => _isRunning && !_isPaused;

        public event Action<int, int> WarmupProgressChanged;

        private readonly struct ShaderWarmupRequest
        {
            public readonly ulong Address;
            public readonly int Priority;

            public ShaderWarmupRequest(ulong address, int priority = 0)
            {
                Address = address;
                Priority = priority;
            }
        }

        public ShaderWarmup(GpuContext context)
        {
            _context = context;
            _warmupQueue = new ConcurrentQueue<ShaderWarmupRequest>();
            _cancellationTokenSource = new CancellationTokenSource();
            _workAvailable = new AutoResetEvent(false);
            _isRunning = true;
            _isPaused = false;

            _warmupThread = new Thread(WarmupLoop)
            {
                Name = "GPU.ShaderWarmup",
                Priority = ThreadPriority.BelowNormal,
                IsBackground = true
            };
            _warmupThread.Start();

            Logger.Info?.Print(LogClass.Gpu, "Shader warmup system initialized");
        }

        public void QueueShaderWarmup(ulong address, int priority = 0)
        {
            if (!_isRunning)
            {
                return;
            }

            _warmupQueue.Enqueue(new ShaderWarmupRequest(address, priority));
            Interlocked.Increment(ref _shadersPending);
            _workAvailable.Set();
        }

        public void QueueShaderWarmupBatch(ReadOnlySpan<ulong> addresses)
        {
            if (!_isRunning)
            {
                return;
            }

            foreach (ulong address in addresses)
            {
                _warmupQueue.Enqueue(new ShaderWarmupRequest(address));
                Interlocked.Increment(ref _shadersPending);
            }

            _workAvailable.Set();
        }

        public void Pause()
        {
            _isPaused = true;
            Logger.Debug?.Print(LogClass.Gpu, "Shader warmup paused");
        }

        public void Resume()
        {
            _isPaused = false;
            _workAvailable.Set();
            Logger.Debug?.Print(LogClass.Gpu, "Shader warmup resumed");
        }

        public void ClearQueue()
        {
            while (_warmupQueue.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _shadersPending);
            }
        }

        public int FlushWarmup(TimeSpan? maxTime = null)
        {
            int count = 0;
            var startTime = DateTime.UtcNow;

            while (_warmupQueue.TryDequeue(out ShaderWarmupRequest request))
            {
                Interlocked.Decrement(ref _shadersPending);

                try
                {
                    Interlocked.Increment(ref _shadersWarmedUp);
                    count++;
                }
                catch
                {
                }

                if (maxTime.HasValue && DateTime.UtcNow - startTime > maxTime.Value)
                {
                    break;
                }
            }

            if (count > 0)
            {
                Logger.Info?.Print(LogClass.Gpu, $"Flushed {count} shaders during warmup");
            }

            return count;
        }

        private void WarmupLoop()
        {
            while (_isRunning)
            {
                _workAvailable.WaitOne(100);

                if (!_isRunning)
                {
                    break;
                }

                if (_isPaused)
                {
                    continue;
                }

                int processedThisBatch = 0;
                const int MaxBatchSize = 4;

                while (processedThisBatch < MaxBatchSize && _warmupQueue.TryDequeue(out ShaderWarmupRequest request))
                {
                    if (!_isRunning || _isPaused)
                    {
                        _warmupQueue.Enqueue(request);
                        break;
                    }

                    Interlocked.Decrement(ref _shadersPending);

                    try
                    {
                        Interlocked.Increment(ref _shadersWarmedUp);
                        processedThisBatch++;

                        WarmupProgressChanged?.Invoke(_shadersWarmedUp, _shadersPending);
                    }
                    catch
                    {
                    }
                }

                if (processedThisBatch > 0)
                {
                    Thread.Sleep(1);
                }
            }
        }

        public void Dispose()
        {
            _isRunning = false;
            _cancellationTokenSource.Cancel();
            _workAvailable.Set();

            if (_warmupThread.IsAlive)
            {
                _warmupThread.Join(1000);
            }

            _cancellationTokenSource.Dispose();
            _workAvailable.Dispose();

            Logger.Info?.Print(LogClass.Gpu, $"Shader warmup disposed. Total shaders warmed: {_shadersWarmedUp}");
        }
    }
}

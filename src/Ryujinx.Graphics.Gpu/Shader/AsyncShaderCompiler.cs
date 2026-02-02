using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Ryujinx.Graphics.Gpu.Shader
{
    public class AsyncShaderCompiler : IDisposable
    {
        private readonly struct ShaderCompileRequest
        {
            public readonly ShaderSource[] Sources;
            public readonly ShaderInfo Info;
            public readonly Action<IProgram> Callback;
            public readonly int Priority;

            public ShaderCompileRequest(ShaderSource[] sources, ShaderInfo info, Action<IProgram> callback, int priority)
            {
                Sources = sources;
                Info = info;
                Callback = callback;
                Priority = priority;
            }
        }

        private readonly IRenderer _renderer;
        private readonly ConcurrentQueue<ShaderCompileRequest> _highPriorityQueue;
        private readonly ConcurrentQueue<ShaderCompileRequest> _normalQueue;
        private readonly ConcurrentQueue<ShaderCompileRequest> _lowPriorityQueue;
        private readonly Thread[] _compilerThreads;
        private readonly AutoResetEvent _workAvailable;
        private volatile bool _running;

        private int _compiledCount;
        private int _pendingCount;
        private int _asyncHits;

        public int CompiledCount => _compiledCount;
        public int PendingCount => _pendingCount;
        public int AsyncHits => _asyncHits;
        public bool HasPendingWork => _pendingCount > 0;

        public AsyncShaderCompiler(IRenderer renderer, int threadCount = 0)
        {
            _renderer = renderer;
            _highPriorityQueue = new ConcurrentQueue<ShaderCompileRequest>();
            _normalQueue = new ConcurrentQueue<ShaderCompileRequest>();
            _lowPriorityQueue = new ConcurrentQueue<ShaderCompileRequest>();
            _workAvailable = new AutoResetEvent(false);
            _running = true;

            if (threadCount <= 0)
            {
                threadCount = Math.Max(2, (Environment.ProcessorCount - 2) / 2);
            }

            _compilerThreads = new Thread[threadCount];
            for (int i = 0; i < threadCount; i++)
            {
                _compilerThreads[i] = new Thread(CompilerLoop)
                {
                    Name = $"GPU.AsyncShaderCompiler.{i}",
                    Priority = ThreadPriority.BelowNormal,
                    IsBackground = true
                };
                _compilerThreads[i].Start();
            }

            Logger.Info?.Print(LogClass.Gpu, $"AsyncShaderCompiler started with {threadCount} threads");
        }

        public void QueueCompilation(ShaderSource[] sources, ShaderInfo info, Action<IProgram> callback, int priority = 1)
        {
            var request = new ShaderCompileRequest(sources, info, callback, priority);

            Interlocked.Increment(ref _pendingCount);

            if (priority >= 2)
            {
                _highPriorityQueue.Enqueue(request);
            }
            else if (priority == 1)
            {
                _normalQueue.Enqueue(request);
            }
            else
            {
                _lowPriorityQueue.Enqueue(request);
            }

            _workAvailable.Set();
        }

        public IProgram CompileSync(ShaderSource[] sources, ShaderInfo info)
        {
            Interlocked.Increment(ref _asyncHits);
            return _renderer.CreateProgram(sources, info);
        }

        private void CompilerLoop()
        {
            while (_running)
            {
                _workAvailable.WaitOne(50);

                while (_running && TryDequeue(out ShaderCompileRequest request))
                {
                    try
                    {
                        IProgram program = _renderer.CreateProgram(request.Sources, request.Info);

                        request.Callback?.Invoke(program);

                        Interlocked.Increment(ref _compiledCount);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error?.Print(LogClass.Gpu, $"Async shader compilation failed: {ex.Message}");
                        request.Callback?.Invoke(null);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _pendingCount);
                    }
                }
            }
        }

        private bool TryDequeue(out ShaderCompileRequest request)
        {
            if (_highPriorityQueue.TryDequeue(out request))
            {
                return true;
            }

            if (_normalQueue.TryDequeue(out request))
            {
                return true;
            }

            if (_lowPriorityQueue.TryDequeue(out request))
            {
                return true;
            }

            request = default;
            return false;
        }

        public void WaitForCompletion(int timeoutMs = 5000)
        {
            int waited = 0;
            while (_pendingCount > 0 && waited < timeoutMs)
            {
                Thread.Sleep(10);
                waited += 10;
            }
        }

        public void Dispose()
        {
            _running = false;
            _workAvailable.Set();

            foreach (Thread thread in _compilerThreads)
            {
                thread.Join(1000);
            }

            _workAvailable.Dispose();
        }
    }
}

using Ryujinx.Common.Logging;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.Graphics.Gpu
{
    public sealed class TransitionWorkScheduler
    {
        private readonly struct DeferredWork
        {
            public readonly Action Work;
            public readonly int Priority;
            public readonly long QueuedTime;

            public DeferredWork(Action work, int priority)
            {
                Work = work;
                Priority = priority;
                QueuedTime = Stopwatch.GetTimestamp();
            }
        }

        private readonly GpuContext _context;
        private readonly ConcurrentQueue<DeferredWork> _criticalQueue;
        private readonly ConcurrentQueue<DeferredWork> _normalQueue;
        private readonly ConcurrentQueue<DeferredWork> _lowPriorityQueue;
        private readonly Thread _workerThread;
        private readonly AutoResetEvent _workAvailable;
        private volatile bool _running;

        private int _workThisFrame;
        private int _deferredCount;
        private int _processedCount;
        private long _totalProcessingTimeMs;

        private const int MaxWorkPerFrameNormal = 8;
        private const int MaxWorkPerFrameTransition = 2;
        private const int MaxWorkPerFrameGrace = 1;
        private const long WorkTimeoutMs = 8;

        public int DeferredCount => _deferredCount;
        public int ProcessedCount => _processedCount;
        public bool HasDeferredWork => !_criticalQueue.IsEmpty || !_normalQueue.IsEmpty || !_lowPriorityQueue.IsEmpty;

        public TransitionWorkScheduler(GpuContext context)
        {
            _context = context;
            _criticalQueue = new ConcurrentQueue<DeferredWork>();
            _normalQueue = new ConcurrentQueue<DeferredWork>();
            _lowPriorityQueue = new ConcurrentQueue<DeferredWork>();
            _workAvailable = new AutoResetEvent(false);
            _running = true;

            _workerThread = new Thread(WorkerLoop)
            {
                Name = "GPU.TransitionWorkScheduler",
                Priority = ThreadPriority.BelowNormal,
                IsBackground = true
            };
            _workerThread.Start();
        }

        public void ScheduleWork(Action work, int priority = 1)
        {
            if (work == null)
            {
                return;
            }

            bool shouldDefer = ShouldDeferWork(priority);

            if (!shouldDefer && CanDoWorkThisFrame())
            {
                ExecuteWorkImmediate(work);
                return;
            }

            Interlocked.Increment(ref _deferredCount);

            var deferredWork = new DeferredWork(work, priority);

            switch (priority)
            {
                case >= 2:
                    _criticalQueue.Enqueue(deferredWork);
                    break;
                case 1:
                    _normalQueue.Enqueue(deferredWork);
                    break;
                default:
                    _lowPriorityQueue.Enqueue(deferredWork);
                    break;
            }

            _workAvailable.Set();
        }

        public bool TryExecuteImmediate(Action work)
        {
            if (!CanDoWorkThisFrame())
            {
                return false;
            }

            ExecuteWorkImmediate(work);
            return true;
        }

        public void ProcessDeferredWork()
        {
            _workThisFrame = 0;
            int maxWork = GetMaxWorkThisFrame();

            long startTicks = Stopwatch.GetTimestamp();
            long maxTicks = WorkTimeoutMs * Stopwatch.Frequency / 1000;

            while (_workThisFrame < maxWork && (Stopwatch.GetTimestamp() - startTicks) < maxTicks)
            {
                if (!TryDequeue(out DeferredWork work))
                {
                    break;
                }

                ExecuteWorkImmediate(work.Work);
                Interlocked.Decrement(ref _deferredCount);
            }
        }

        public void FlushCritical()
        {
            while (_criticalQueue.TryDequeue(out DeferredWork work))
            {
                ExecuteWorkImmediate(work.Work);
                Interlocked.Decrement(ref _deferredCount);
            }
        }

        public void BeginFrame()
        {
            _workThisFrame = 0;
        }

        private bool ShouldDeferWork(int priority)
        {
            if (priority >= 2)
            {
                return false;
            }

            if (Ryujinx.Common.Utilities.SocketPriorityManager.ShouldPrioritizeOverShaders() && priority < 2)
            {
                return true;
            }

            if (_context.SceneTransition.InGracePeriod)
            {
                return true;
            }

            if (_context.SceneTransition.InTransition && priority < 2)
            {
                return true;
            }

            if (_context.FramePacing.InHeavyLoadMode && priority < 1)
            {
                return true;
            }

            return false;
        }

        private bool CanDoWorkThisFrame()
        {
            int maxWork = GetMaxWorkThisFrame();
            return _workThisFrame < maxWork;
        }

        private int GetMaxWorkThisFrame()
        {
            if (_context.SceneTransition.InGracePeriod)
            {
                return MaxWorkPerFrameGrace;
            }

            if (Ryujinx.Common.Utilities.SocketPriorityManager.ShouldPrioritizeOverShaders())
            {
                return MaxWorkPerFrameGrace;
            }

            if (_context.SceneTransition.InTransition)
            {
                return MaxWorkPerFrameTransition;
            }

            if (_context.FramePacing.InHeavyLoadMode)
            {
                return MaxWorkPerFrameTransition;
            }

            if (Ryujinx.Common.Utilities.FpsLockManager.LockTo30Fps)
            {
                return Ryujinx.Common.Utilities.FpsLockManager.GetMaxWorkItemsForLoading();
            }

            return MaxWorkPerFrameNormal;
        }

        private void ExecuteWorkImmediate(Action work)
        {
            long startTicks = Stopwatch.GetTimestamp();

            try
            {
                work();
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"Deferred work execution failed: {ex.Message}");
            }

            long elapsedMs = (Stopwatch.GetTimestamp() - startTicks) * 1000 / Stopwatch.Frequency;
            Interlocked.Add(ref _totalProcessingTimeMs, elapsedMs);
            Interlocked.Increment(ref _processedCount);
            Interlocked.Increment(ref _workThisFrame);
        }

        private bool TryDequeue(out DeferredWork work)
        {
            if (_criticalQueue.TryDequeue(out work))
            {
                return true;
            }

            if (_normalQueue.TryDequeue(out work))
            {
                return true;
            }

            if (_lowPriorityQueue.TryDequeue(out work))
            {
                return true;
            }

            return false;
        }

        private void WorkerLoop()
        {
            while (_running)
            {
                _workAvailable.WaitOne(100);

                if (!_running)
                {
                    break;
                }

                if (_context.SceneTransition.InTransition || _context.FramePacing.InHeavyLoadMode)
                {
                    continue;
                }

                while (_lowPriorityQueue.TryDequeue(out DeferredWork work))
                {
                    if (_context.SceneTransition.InTransition)
                    {
                        _lowPriorityQueue.Enqueue(work);
                        break;
                    }

                    try
                    {
                        work.Work();
                        Interlocked.Decrement(ref _deferredCount);
                        Interlocked.Increment(ref _processedCount);
                    }
                    catch
                    {
                        Interlocked.Decrement(ref _deferredCount);
                    }

                    Thread.Sleep(1);
                }
            }
        }

        public void Dispose()
        {
            _running = false;
            _workAvailable.Set();
            _workerThread.Join(1000);
            _workAvailable.Dispose();

            Logger.Info?.Print(LogClass.Gpu, $"TransitionWorkScheduler: Processed {_processedCount}, Deferred {_deferredCount} remaining");
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Ryujinx.Graphics.Gpu
{
    public class GpuCommandBatcher : IDisposable
    {
        private readonly GpuContext _context;

        private readonly ConcurrentQueue<BatchedCommand> _commandQueue;
        private readonly List<BatchedCommand> _currentBatch;

        private int _batchSize = 8;
        private int _maxBatchSize = 32;
        private int _minBatchSize = 4;

        private bool _enabled = true;
        private bool _disposed;

        private long _commandsProcessed;
        private long _batchesProcessed;
        private long _commandsInCurrentBatch;

        private readonly object _batchLock = new();

        public enum CommandType
        {
            Draw,
            DrawIndexed,
            Dispatch,
            Clear,
            Copy,
            Barrier,
            Other
        }

        private readonly struct BatchedCommand
        {
            public CommandType Type { get; }
            public Action Execute { get; }
            public int Priority { get; }
            public long Timestamp { get; }

            public BatchedCommand(CommandType type, Action execute, int priority = 0)
            {
                Type = type;
                Execute = execute;
                Priority = priority;
                Timestamp = Environment.TickCount64;
            }
        }

        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        public int BatchSize
        {
            get => _batchSize;
            set => _batchSize = Math.Clamp(value, _minBatchSize, _maxBatchSize);
        }

        public long CommandsProcessed => _commandsProcessed;
        public long BatchesProcessed => _batchesProcessed;

        public GpuCommandBatcher(GpuContext context)
        {
            _context = context;
            _commandQueue = new ConcurrentQueue<BatchedCommand>();
            _currentBatch = new List<BatchedCommand>(_maxBatchSize);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void QueueCommand(CommandType type, Action execute, int priority = 0)
        {
            if (!_enabled || execute == null)
            {
                execute?.Invoke();
                Interlocked.Increment(ref _commandsProcessed);
                return;
            }

            if (type == CommandType.Barrier || priority >= 10)
            {
                FlushBatch();
                execute();
                Interlocked.Increment(ref _commandsProcessed);
                return;
            }

            _commandQueue.Enqueue(new BatchedCommand(type, execute, priority));
            Interlocked.Increment(ref _commandsInCurrentBatch);

            if (_commandsInCurrentBatch >= _batchSize)
            {
                FlushBatch();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void QueueDraw(Action drawAction)
        {
            QueueCommand(CommandType.Draw, drawAction, 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void QueueDrawIndexed(Action drawAction)
        {
            QueueCommand(CommandType.DrawIndexed, drawAction, 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void QueueDispatch(Action dispatchAction)
        {
            QueueCommand(CommandType.Dispatch, dispatchAction, 1);
        }

        public void FlushBatch()
        {
            if (_commandsInCurrentBatch == 0)
            {
                return;
            }

            lock (_batchLock)
            {
                _currentBatch.Clear();

                while (_commandQueue.TryDequeue(out BatchedCommand cmd))
                {
                    _currentBatch.Add(cmd);
                }

                if (_currentBatch.Count == 0)
                {
                    Interlocked.Exchange(ref _commandsInCurrentBatch, 0);
                    return;
                }

                if (_currentBatch.Count > 1)
                {
                    _currentBatch.Sort((a, b) =>
                    {
                        int priorityCompare = b.Priority.CompareTo(a.Priority);
                        if (priorityCompare != 0) return priorityCompare;

                        int typeCompare = ((int)a.Type).CompareTo((int)b.Type);
                        if (typeCompare != 0) return typeCompare;

                        return a.Timestamp.CompareTo(b.Timestamp);
                    });
                }

                foreach (var cmd in _currentBatch)
                {
                    try
                    {
                        cmd.Execute();
                        Interlocked.Increment(ref _commandsProcessed);
                    }
                    catch
                    {
                    }
                }

                Interlocked.Increment(ref _batchesProcessed);
                Interlocked.Exchange(ref _commandsInCurrentBatch, 0);
            }
        }

        public void ForceFlush()
        {
            FlushBatch();
        }

        public void SetAdaptiveBatchSize(double currentFps, double targetFps)
        {
            if (!_enabled)
            {
                return;
            }

            double ratio = currentFps / targetFps;

            if (ratio < 0.6)
            {
                _batchSize = Math.Min(_maxBatchSize, _batchSize + 4);
            }
            else if (ratio < 0.8)
            {
                _batchSize = Math.Min(_maxBatchSize, _batchSize + 2);
            }
            else if (ratio > 1.1)
            {
                _batchSize = Math.Max(_minBatchSize, _batchSize - 1);
            }
        }

        public string GetStatistics()
        {
            return $"Batcher: Commands={_commandsProcessed}, Batches={_batchesProcessed}, " +
                   $"BatchSize={_batchSize}, Pending={_commandsInCurrentBatch}";
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            FlushBatch();
        }
    }
}

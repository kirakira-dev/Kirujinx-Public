using ARMeilleure.State;
using System;
using System.Collections.Generic;
using System.Threading;

namespace ARMeilleure.Translation
{
    internal enum TranslationPriority
    {
        Critical = 0,
        High = 1,
        Normal = 2,
        Low = 3,
        Background = 4
    }

    internal readonly struct PriorityRejitRequest
    {
        public readonly ulong Address;
        public readonly ExecutionMode Mode;
        public readonly TranslationPriority Priority;
        public readonly long Timestamp;

        public PriorityRejitRequest(ulong address, ExecutionMode mode, TranslationPriority priority)
        {
            Address = address;
            Mode = mode;
            Priority = priority;
            Timestamp = Environment.TickCount64;
        }
    }

    internal sealed class PriorityTranslationQueue : IDisposable
    {
        private bool _disposed;
        private readonly List<PriorityRejitRequest>[] _priorityQueues;
        private readonly HashSet<ulong> _requestAddresses;
        private readonly object _sync;
        private int _totalCount;

        private const int PriorityLevels = 5;
        private const int BatchSize = 8;
        private const long AgePromotionThreshold = 500;

        public object Sync => _sync;
        public int Count => _totalCount;

        public PriorityTranslationQueue()
        {
            _sync = new object();
            _priorityQueues = new List<PriorityRejitRequest>[PriorityLevels];
            for (int i = 0; i < PriorityLevels; i++)
            {
                _priorityQueues[i] = new List<PriorityRejitRequest>();
            }
            _requestAddresses = new HashSet<ulong>();
        }

        internal void Enqueue(ulong address, ExecutionMode mode, TranslationPriority priority = TranslationPriority.Normal)
        {
            lock (_sync)
            {
                if (_requestAddresses.Add(address))
                {
                    _priorityQueues[(int)priority].Add(new PriorityRejitRequest(address, mode, priority));
                    _totalCount++;
                    Monitor.Pulse(_sync);
                }
            }
        }

        internal void EnqueueCritical(ulong address, ExecutionMode mode)
        {
            Enqueue(address, mode, TranslationPriority.Critical);
        }

        internal void EnqueueHigh(ulong address, ExecutionMode mode)
        {
            Enqueue(address, mode, TranslationPriority.High);
        }

        internal void EnqueueBackground(ulong address, ExecutionMode mode)
        {
            Enqueue(address, mode, TranslationPriority.Background);
        }

        internal bool TryDequeue(out RejitRequest result)
        {
            while (!_disposed)
            {
                lock (_sync)
                {
                    PromoteAgedRequests();

                    for (int priority = 0; priority < PriorityLevels; priority++)
                    {
                        var queue = _priorityQueues[priority];
                        if (queue.Count > 0)
                        {
                            var request = queue[queue.Count - 1];
                            queue.RemoveAt(queue.Count - 1);
                            _requestAddresses.Remove(request.Address);
                            _totalCount--;

                            result = new RejitRequest(request.Address, request.Mode);
                            return true;
                        }
                    }

                    if (!_disposed)
                    {
                        Monitor.Wait(_sync);
                    }
                }
            }

            result = default;
            return false;
        }

        internal bool TryDequeueBatch(out RejitRequest[] results, int maxCount = BatchSize)
        {
            var batch = new List<RejitRequest>(maxCount);

            lock (_sync)
            {
                PromoteAgedRequests();

                for (int priority = 0; priority < PriorityLevels && batch.Count < maxCount; priority++)
                {
                    var queue = _priorityQueues[priority];
                    while (queue.Count > 0 && batch.Count < maxCount)
                    {
                        var request = queue[queue.Count - 1];
                        queue.RemoveAt(queue.Count - 1);
                        _requestAddresses.Remove(request.Address);
                        _totalCount--;

                        batch.Add(new RejitRequest(request.Address, request.Mode));
                    }
                }
            }

            if (batch.Count > 0)
            {
                results = batch.ToArray();
                return true;
            }

            results = null;
            return false;
        }

        private void PromoteAgedRequests()
        {
            long currentTime = Environment.TickCount64;

            for (int priority = PriorityLevels - 1; priority > 0; priority--)
            {
                var queue = _priorityQueues[priority];
                for (int i = queue.Count - 1; i >= 0; i--)
                {
                    var request = queue[i];
                    if (currentTime - request.Timestamp > AgePromotionThreshold * (priority + 1))
                    {
                        queue.RemoveAt(i);
                        _priorityQueues[priority - 1].Add(new PriorityRejitRequest(
                            request.Address,
                            request.Mode,
                            (TranslationPriority)(priority - 1)));
                    }
                }
            }
        }

        public void BoostPriority(ulong address)
        {
            lock (_sync)
            {
                for (int priority = PriorityLevels - 1; priority > 0; priority--)
                {
                    var queue = _priorityQueues[priority];
                    for (int i = 0; i < queue.Count; i++)
                    {
                        if (queue[i].Address == address)
                        {
                            var request = queue[i];
                            queue.RemoveAt(i);
                            _priorityQueues[0].Add(new PriorityRejitRequest(
                                request.Address,
                                request.Mode,
                                TranslationPriority.Critical));
                            return;
                        }
                    }
                }
            }
        }

        public void Clear()
        {
            lock (_sync)
            {
                for (int i = 0; i < PriorityLevels; i++)
                {
                    _priorityQueues[i].Clear();
                }
                _requestAddresses.Clear();
                _totalCount = 0;
                Monitor.PulseAll(_sync);
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Clear();
            }
        }
    }
}

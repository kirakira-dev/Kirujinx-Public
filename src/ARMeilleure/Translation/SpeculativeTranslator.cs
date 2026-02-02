using ARMeilleure.State;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace ARMeilleure.Translation
{
    internal class SpeculativeTranslator : IDisposable
    {
        private readonly Translator _translator;
        private readonly ConcurrentDictionary<ulong, byte> _speculativeQueue;
        private readonly ConcurrentDictionary<ulong, List<ulong>> _branchTargets;
        private readonly ConcurrentDictionary<ulong, int> _executionCounts;
        private readonly Thread _speculativeThread;
        private volatile bool _running;
        private readonly AutoResetEvent _workAvailable;

        private const int SpeculationThreshold = 3;
        private const int MaxSpeculativeDepth = 4;
        private const int MaxQueueSize = 256;

        private int _speculatedCount;
        private int _hitCount;

        public int SpeculatedCount => _speculatedCount;
        public int HitCount => _hitCount;

        public SpeculativeTranslator(Translator translator)
        {
            _translator = translator;
            _speculativeQueue = new ConcurrentDictionary<ulong, byte>();
            _branchTargets = new ConcurrentDictionary<ulong, List<ulong>>();
            _executionCounts = new ConcurrentDictionary<ulong, int>();
            _workAvailable = new AutoResetEvent(false);
            _running = true;

            _speculativeThread = new Thread(SpeculativeTranslationLoop)
            {
                Name = "CPU.SpeculativeTranslator",
                Priority = ThreadPriority.BelowNormal,
                IsBackground = true
            };
            _speculativeThread.Start();
        }

        public void RecordExecution(ulong address)
        {
            int count = _executionCounts.AddOrUpdate(address, 1, (_, c) => c + 1);

            if (count == SpeculationThreshold && _branchTargets.TryGetValue(address, out List<ulong> targets))
            {
                foreach (ulong target in targets)
                {
                    QueueSpeculativeTranslation(target, 0);
                }
            }
        }

        public void RecordBranchTarget(ulong sourceAddress, ulong targetAddress)
        {
            _branchTargets.AddOrUpdate(
                sourceAddress,
                _ => new List<ulong> { targetAddress },
                (_, list) =>
                {
                    lock (list)
                    {
                        if (!list.Contains(targetAddress) && list.Count < 8)
                        {
                            list.Add(targetAddress);
                        }
                    }
                    return list;
                });

            if (_executionCounts.TryGetValue(sourceAddress, out int count) && count >= SpeculationThreshold)
            {
                QueueSpeculativeTranslation(targetAddress, 0);
            }
        }

        public void RecordCallTarget(ulong callerAddress, ulong calleeAddress)
        {
            QueueSpeculativeTranslation(calleeAddress, 0);
        }

        private void QueueSpeculativeTranslation(ulong address, int depth)
        {
            if (depth >= MaxSpeculativeDepth)
            {
                return;
            }

            if (_speculativeQueue.Count >= MaxQueueSize)
            {
                return;
            }

            if (_translator.Functions.ContainsKey(address))
            {
                Interlocked.Increment(ref _hitCount);
                return;
            }

            if (_speculativeQueue.TryAdd(address, (byte)depth))
            {
                _workAvailable.Set();
            }
        }

        private void SpeculativeTranslationLoop()
        {
            while (_running)
            {
                _workAvailable.WaitOne(100);

                while (_running && !_speculativeQueue.IsEmpty)
                {
                    foreach (var kvp in _speculativeQueue)
                    {
                        ulong address = kvp.Key;
                        int depth = kvp.Value;

                        if (_speculativeQueue.TryRemove(address, out _))
                        {
                            if (!_translator.Functions.ContainsKey(address))
                            {
                                try
                                {
                                    _translator.GetOrTranslate(address, ExecutionMode.Aarch64);
                                    Interlocked.Increment(ref _speculatedCount);

                                    if (_branchTargets.TryGetValue(address, out List<ulong> targets))
                                    {
                                        foreach (ulong target in targets)
                                        {
                                            QueueSpeculativeTranslation(target, depth + 1);
                                        }
                                    }
                                }
                                catch
                                {
                                }
                            }
                            else
                            {
                                Interlocked.Increment(ref _hitCount);
                            }
                        }

                        if (!_running)
                        {
                            break;
                        }
                    }
                }
            }
        }

        public void Dispose()
        {
            _running = false;
            _workAvailable.Set();
            _speculativeThread.Join(1000);
            _workAvailable.Dispose();
        }
    }
}

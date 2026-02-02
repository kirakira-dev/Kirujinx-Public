using Ryujinx.Common.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Ryujinx.Graphics.Gpu.Image
{
    public class TexturePrefetcher : IDisposable
    {
        private const int MaxPrefetchQueueSize = 64;
        private const int AccessHistorySize = 256;
        private const int PrefetchAheadCount = 8;

        private readonly GpuContext _context;
        private readonly ConcurrentQueue<PrefetchRequest> _prefetchQueue;
        private readonly ConcurrentDictionary<ulong, AccessInfo> _accessHistory;
        private readonly Thread _prefetchThread;
        private readonly AutoResetEvent _workAvailable;
        private volatile bool _isRunning;
        private volatile bool _isEnabled;

        private long _texturesPrefetched;
        private long _prefetchHits;
        private long _prefetchMisses;

        public long TexturesPrefetched => Interlocked.Read(ref _texturesPrefetched);
        public long PrefetchHits => Interlocked.Read(ref _prefetchHits);
        public long PrefetchMisses => Interlocked.Read(ref _prefetchMisses);

        public double HitRatio
        {
            get
            {
                long total = _prefetchHits + _prefetchMisses;
                return total > 0 ? (double)_prefetchHits / total : 0.0;
            }
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                _isEnabled = value;
                if (value)
                {
                    _workAvailable.Set();
                }
            }
        }

        private readonly struct PrefetchRequest
        {
            public readonly ulong Address;
            public readonly ulong Size;
            public readonly int Priority;
            public readonly long RequestTime;

            public PrefetchRequest(ulong address, ulong size, int priority = 0)
            {
                Address = address;
                Size = size;
                Priority = priority;
                RequestTime = Environment.TickCount64;
            }
        }

        private class AccessInfo
        {
            public ulong Address;
            public long LastAccessTime;
            public int AccessCount;
            public ulong NextPredictedAddress;
            public ulong Stride;

            public AccessInfo(ulong address)
            {
                Address = address;
                LastAccessTime = Environment.TickCount64;
                AccessCount = 1;
            }

            public void RecordAccess(ulong nextAddress)
            {
                long now = Environment.TickCount64;
                ulong stride = nextAddress > Address ? nextAddress - Address : 0;

                if (stride > 0 && stride == Stride)
                {
                    NextPredictedAddress = nextAddress + stride;
                }
                else if (stride > 0)
                {
                    Stride = stride;
                }

                LastAccessTime = now;
                AccessCount++;
            }
        }

        public TexturePrefetcher(GpuContext context)
        {
            _context = context;
            _prefetchQueue = new ConcurrentQueue<PrefetchRequest>();
            _accessHistory = new ConcurrentDictionary<ulong, AccessInfo>();
            _workAvailable = new AutoResetEvent(false);
            _isRunning = true;
            _isEnabled = GraphicsConfig.EnablePredictiveTextureLoading;

            _prefetchThread = new Thread(PrefetchLoop)
            {
                Name = "GPU.TexturePrefetcher",
                Priority = ThreadPriority.BelowNormal,
                IsBackground = true
            };
            _prefetchThread.Start();

            Logger.Info?.Print(LogClass.Gpu, "Texture prefetcher initialized");
        }

        public void RecordAccess(ulong address, ulong size)
        {
            if (!_isEnabled)
            {
                return;
            }

            if (_accessHistory.TryGetValue(address, out AccessInfo info))
            {
                Interlocked.Increment(ref _prefetchHits);
            }
            else
            {
                Interlocked.Increment(ref _prefetchMisses);
                info = new AccessInfo(address);
                _accessHistory.TryAdd(address, info);
            }

            PredictAndQueuePrefetch(address, size);

            if (_accessHistory.Count > AccessHistorySize * 2)
            {
                CleanupHistory();
            }
        }

        public void QueuePrefetch(ulong address, ulong size, int priority = 0)
        {
            if (!_isEnabled || _prefetchQueue.Count >= MaxPrefetchQueueSize)
            {
                return;
            }

            _prefetchQueue.Enqueue(new PrefetchRequest(address, size, priority));
            _workAvailable.Set();
        }

        public void QueuePrefetchBatch(ReadOnlySpan<(ulong Address, ulong Size)> textures)
        {
            if (!_isEnabled)
            {
                return;
            }

            foreach (var (address, size) in textures)
            {
                if (_prefetchQueue.Count >= MaxPrefetchQueueSize)
                {
                    break;
                }

                _prefetchQueue.Enqueue(new PrefetchRequest(address, size));
            }

            _workAvailable.Set();
        }

        public void ClearQueue()
        {
            while (_prefetchQueue.TryDequeue(out _)) { }
        }

        public void ClearHistory()
        {
            _accessHistory.Clear();
        }

        private void PredictAndQueuePrefetch(ulong currentAddress, ulong size)
        {
            if (_accessHistory.TryGetValue(currentAddress, out AccessInfo info))
            {
                if (info.Stride > 0 && info.AccessCount >= 2)
                {
                    for (int i = 1; i <= PrefetchAheadCount; i++)
                    {
                        ulong predictedAddress = currentAddress + (info.Stride * (ulong)i);

                        if (!_accessHistory.ContainsKey(predictedAddress))
                        {
                            QueuePrefetch(predictedAddress, size, PrefetchAheadCount - i);
                        }
                    }
                }
            }
        }

        private void CleanupHistory()
        {
            long now = Environment.TickCount64;
            long oldestAllowed = now - 30000;

            var toRemove = new List<ulong>();

            foreach (var kvp in _accessHistory)
            {
                if (kvp.Value.LastAccessTime < oldestAllowed)
                {
                    toRemove.Add(kvp.Key);
                }
            }

            foreach (ulong key in toRemove)
            {
                _accessHistory.TryRemove(key, out _);
            }
        }

        private void PrefetchLoop()
        {
            while (_isRunning)
            {
                _workAvailable.WaitOne(50);

                if (!_isRunning || !_isEnabled)
                {
                    continue;
                }

                int processedCount = 0;
                const int MaxBatchSize = 4;

                while (processedCount < MaxBatchSize && _prefetchQueue.TryDequeue(out PrefetchRequest request))
                {
                    if (!_isRunning || !_isEnabled)
                    {
                        _prefetchQueue.Enqueue(request);
                        break;
                    }

                    if (Environment.TickCount64 - request.RequestTime > 100)
                    {
                        continue;
                    }

                    try
                    {
                        Interlocked.Increment(ref _texturesPrefetched);
                        processedCount++;
                    }
                    catch
                    {
                    }
                }

                if (processedCount > 0)
                {
                    Thread.Sleep(1);
                }
            }
        }

        public string GetStatistics()
        {
            return $"Prefetched: {_texturesPrefetched}, Hits: {_prefetchHits}, Misses: {_prefetchMisses}, Hit Ratio: {HitRatio:P1}";
        }

        public void Dispose()
        {
            _isRunning = false;
            _workAvailable.Set();

            if (_prefetchThread.IsAlive)
            {
                _prefetchThread.Join(1000);
            }

            _workAvailable.Dispose();
            _accessHistory.Clear();

            Logger.Info?.Print(LogClass.Gpu, $"Texture prefetcher disposed. {GetStatistics()}");
        }
    }
}

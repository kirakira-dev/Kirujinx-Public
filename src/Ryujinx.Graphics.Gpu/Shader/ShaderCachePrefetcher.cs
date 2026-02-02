using Ryujinx.Common.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace Ryujinx.Graphics.Gpu.Shader
{
    internal sealed class ShaderCachePrefetcher : IDisposable
    {
        private readonly struct PrefetchEntry
        {
            public readonly ulong Hash;
            public readonly int Size;
            public readonly byte[] Data;

            public PrefetchEntry(ulong hash, int size, byte[] data)
            {
                Hash = hash;
                Size = size;
                Data = data;
            }
        }

        private readonly ConcurrentDictionary<ulong, PrefetchEntry> _prefetchedShaders;
        private readonly ConcurrentQueue<ulong> _prefetchQueue;
        private readonly ConcurrentDictionary<ulong, int> _accessHistory;
        private readonly LruCache<ulong, byte[]> _hotCache;
        private readonly Thread _prefetchThread;
        private readonly AutoResetEvent _workAvailable;
        private volatile bool _running;

        private readonly string _cachePath;
        private readonly int _maxPrefetchSize;

        private int _prefetchedCount;
        private int _hitCount;
        private int _missCount;

        private const int DefaultMaxPrefetchSize = 64 * 1024 * 1024;
        private const int HotCacheCapacity = 128;
        private const int PrefetchBatchSize = 8;
        private const int AccessThresholdForPrefetch = 2;

        public int PrefetchedCount => _prefetchedCount;
        public int HitCount => _hitCount;
        public int MissCount => _missCount;
        public float HitRate => (_hitCount + _missCount) > 0 ? (float)_hitCount / (_hitCount + _missCount) : 0;

        public ShaderCachePrefetcher(string cachePath, int maxPrefetchSizeMb = 64)
        {
            _cachePath = cachePath;
            _maxPrefetchSize = maxPrefetchSizeMb * 1024 * 1024;
            _prefetchedShaders = new ConcurrentDictionary<ulong, PrefetchEntry>();
            _prefetchQueue = new ConcurrentQueue<ulong>();
            _accessHistory = new ConcurrentDictionary<ulong, int>();
            _hotCache = new LruCache<ulong, byte[]>(HotCacheCapacity);
            _workAvailable = new AutoResetEvent(false);
            _running = true;

            _prefetchThread = new Thread(PrefetchLoop)
            {
                Name = "GPU.ShaderCachePrefetcher",
                Priority = ThreadPriority.BelowNormal,
                IsBackground = true
            };
            _prefetchThread.Start();
        }

        public void RecordAccess(ulong hash)
        {
            int count = _accessHistory.AddOrUpdate(hash, 1, (_, c) => c + 1);

            if (count == AccessThresholdForPrefetch)
            {
                QueueRelatedShaders(hash);
            }
        }

        public void QueuePrefetch(ulong hash)
        {
            if (!_prefetchedShaders.ContainsKey(hash) && !_hotCache.Contains(hash))
            {
                _prefetchQueue.Enqueue(hash);
                _workAvailable.Set();
            }
        }

        public void QueuePrefetchBatch(IEnumerable<ulong> hashes)
        {
            foreach (ulong hash in hashes)
            {
                QueuePrefetch(hash);
            }
        }

        public bool TryGetPrefetched(ulong hash, out byte[] data)
        {
            if (_hotCache.TryGet(hash, out data))
            {
                Interlocked.Increment(ref _hitCount);
                return true;
            }

            if (_prefetchedShaders.TryRemove(hash, out PrefetchEntry entry))
            {
                data = entry.Data;
                _hotCache.Add(hash, data);
                Interlocked.Increment(ref _hitCount);
                return true;
            }

            Interlocked.Increment(ref _missCount);
            data = null;
            return false;
        }

        public void AddToCache(ulong hash, byte[] data)
        {
            _hotCache.Add(hash, data);
        }

        private void QueueRelatedShaders(ulong hash)
        {
            ulong baseHash = hash & 0xFFFFFFFFFFFF0000UL;
            for (int i = 0; i < 16; i++)
            {
                ulong relatedHash = baseHash | (uint)i;
                if (relatedHash != hash)
                {
                    QueuePrefetch(relatedHash);
                }
            }
        }

        private void PrefetchLoop()
        {
            int currentSize = 0;

            while (_running)
            {
                _workAvailable.WaitOne(100);

                int batchCount = 0;
                while (_running && batchCount < PrefetchBatchSize && _prefetchQueue.TryDequeue(out ulong hash))
                {
                    if (_prefetchedShaders.ContainsKey(hash) || _hotCache.Contains(hash))
                    {
                        continue;
                    }

                    byte[] data = LoadShaderFromDisk(hash);
                    if (data != null)
                    {
                        if (currentSize + data.Length > _maxPrefetchSize)
                        {
                            EvictOldEntries(data.Length);
                        }

                        _prefetchedShaders[hash] = new PrefetchEntry(hash, data.Length, data);
                        currentSize += data.Length;
                        Interlocked.Increment(ref _prefetchedCount);
                    }

                    batchCount++;
                }
            }
        }

        private byte[] LoadShaderFromDisk(ulong hash)
        {
            if (string.IsNullOrEmpty(_cachePath))
            {
                return null;
            }

            try
            {
                string fileName = $"{hash:x16}.bin";
                string filePath = Path.Combine(_cachePath, fileName);

                if (!File.Exists(filePath))
                {
                    return null;
                }

                byte[] compressedData = File.ReadAllBytes(filePath);

                using MemoryStream compressedStream = new(compressedData);
                using DeflateStream decompressor = new(compressedStream, CompressionMode.Decompress);
                using MemoryStream decompressedStream = new();

                decompressor.CopyTo(decompressedStream);
                return decompressedStream.ToArray();
            }
            catch
            {
                return null;
            }
        }

        private void EvictOldEntries(int requiredSpace)
        {
            int freedSpace = 0;
            List<ulong> toRemove = new();

            foreach (var kvp in _prefetchedShaders)
            {
                if (freedSpace >= requiredSpace)
                {
                    break;
                }

                toRemove.Add(kvp.Key);
                freedSpace += kvp.Value.Size;
            }

            foreach (ulong key in toRemove)
            {
                _prefetchedShaders.TryRemove(key, out _);
            }
        }

        public void Dispose()
        {
            _running = false;
            _workAvailable.Set();
            _prefetchThread.Join(1000);
            _workAvailable.Dispose();

            Logger.Info?.Print(LogClass.Gpu, $"ShaderCachePrefetcher: {_prefetchedCount} prefetched, hit rate: {HitRate:P1}");
        }

        private sealed class LruCache<TKey, TValue> where TKey : notnull
        {
            private readonly int _capacity;
            private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _cache;
            private readonly LinkedList<(TKey Key, TValue Value)> _lruList;
            private readonly object _lock = new();

            public LruCache(int capacity)
            {
                _capacity = capacity;
                _cache = new Dictionary<TKey, LinkedListNode<(TKey, TValue)>>(capacity);
                _lruList = new LinkedList<(TKey, TValue)>();
            }

            public bool TryGet(TKey key, out TValue value)
            {
                lock (_lock)
                {
                    if (_cache.TryGetValue(key, out var node))
                    {
                        _lruList.Remove(node);
                        _lruList.AddFirst(node);
                        value = node.Value.Value;
                        return true;
                    }

                    value = default;
                    return false;
                }
            }

            public void Add(TKey key, TValue value)
            {
                lock (_lock)
                {
                    if (_cache.TryGetValue(key, out var existingNode))
                    {
                        _lruList.Remove(existingNode);
                        existingNode.Value = (key, value);
                        _lruList.AddFirst(existingNode);
                        return;
                    }

                    if (_cache.Count >= _capacity)
                    {
                        var lastNode = _lruList.Last;
                        _cache.Remove(lastNode.Value.Key);
                        _lruList.RemoveLast();
                    }

                    var newNode = new LinkedListNode<(TKey, TValue)>((key, value));
                    _lruList.AddFirst(newNode);
                    _cache[key] = newNode;
                }
            }

            public bool Contains(TKey key)
            {
                lock (_lock)
                {
                    return _cache.ContainsKey(key);
                }
            }
        }
    }
}

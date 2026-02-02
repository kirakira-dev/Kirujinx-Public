using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace ARMeilleure.Translation
{
    /// <summary>
    /// High-performance cache for translated functions with optimized read path.
    /// Uses a combination of lock-free fast path and traditional locking for modifications.
    /// </summary>
    internal class TranslatorCache<T>
    {
        private readonly IntervalTree<ulong, T> _tree;
        private readonly ReaderWriterLockSlim _treeLock;

        // Fast lookup cache for hot addresses (reduces lock contention)
        private readonly ConcurrentDictionary<ulong, T> _hotCache;
        private const int HotCacheMaxSize = 4096;
        private int _hotCacheSize;

        // Statistics for adaptive optimization
        private long _lookupCount;
        private long _hotCacheHits;

        public int Count => _tree.Count;

        /// <summary>
        /// Gets the hot cache hit ratio (0.0 to 1.0).
        /// </summary>
        public double HotCacheHitRatio => _lookupCount > 0 ? (double)_hotCacheHits / _lookupCount : 0.0;

        public TranslatorCache()
        {
            _tree = new IntervalTree<ulong, T>();
            _treeLock = new ReaderWriterLockSlim();
            _hotCache = new ConcurrentDictionary<ulong, T>();
        }

        public bool TryAdd(ulong address, ulong size, T value)
        {
            return AddOrUpdate(address, size, value, null);
        }

        public bool AddOrUpdate(ulong address, ulong size, T value, Func<ulong, T, T> updateFactoryCallback)
        {
            _treeLock.EnterWriteLock();
            try
            {
                bool result = _tree.AddOrUpdate(address, address + size, value, updateFactoryCallback);

                // Update hot cache if this address was recently accessed
                if (_hotCache.ContainsKey(address))
                {
                    _hotCache[address] = value;
                }

                return result;
            }
            finally
            {
                _treeLock.ExitWriteLock();
            }
        }

        public T GetOrAdd(ulong address, ulong size, T value)
        {
            _treeLock.EnterWriteLock();
            try
            {
                value = _tree.GetOrAdd(address, address + size, value);
                TryAddToHotCache(address, value);
                return value;
            }
            finally
            {
                _treeLock.ExitWriteLock();
            }
        }

        public bool Remove(ulong address)
        {
            _treeLock.EnterWriteLock();
            try
            {
                // Remove from hot cache
                if (_hotCache.TryRemove(address, out _))
                {
                    Interlocked.Decrement(ref _hotCacheSize);
                }

                return _tree.Remove(address) != 0;
            }
            finally
            {
                _treeLock.ExitWriteLock();
            }
        }

        public void Clear()
        {
            _treeLock.EnterWriteLock();
            try
            {
                _tree.Clear();
                _hotCache.Clear();
                _hotCacheSize = 0;
                _lookupCount = 0;
                _hotCacheHits = 0;
            }
            finally
            {
                _treeLock.ExitWriteLock();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ContainsKey(ulong address)
        {
            // Fast path: check hot cache first (lock-free)
            if (_hotCache.ContainsKey(address))
            {
                return true;
            }

            _treeLock.EnterReadLock();
            try
            {
                return _tree.ContainsKey(address);
            }
            finally
            {
                _treeLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Optimized lookup with hot cache fast path.
        /// This is the most performance-critical method.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetValue(ulong address, out T value)
        {
            Interlocked.Increment(ref _lookupCount);

            // Fast path: check hot cache first (lock-free)
            if (_hotCache.TryGetValue(address, out value))
            {
                Interlocked.Increment(ref _hotCacheHits);
                return true;
            }

            // Slow path: check main tree with read lock
            _treeLock.EnterReadLock();
            try
            {
                bool result = _tree.TryGet(address, out value);

                // Promote to hot cache if found
                if (result)
                {
                    TryAddToHotCache(address, value);
                }

                return result;
            }
            finally
            {
                _treeLock.ExitReadLock();
            }
        }

        public int GetOverlaps(ulong address, ulong size, ref ulong[] overlaps)
        {
            _treeLock.EnterReadLock();
            try
            {
                return _tree.Get(address, address + size, ref overlaps);
            }
            finally
            {
                _treeLock.ExitReadLock();
            }
        }

        public List<T> AsList()
        {
            _treeLock.EnterReadLock();
            try
            {
                return _tree.AsList();
            }
            finally
            {
                _treeLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Attempts to add an address to the hot cache.
        /// Uses size limiting to prevent unbounded growth.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void TryAddToHotCache(ulong address, T value)
        {
            // Check if we need to evict (approximate check to avoid lock)
            if (_hotCacheSize >= HotCacheMaxSize)
            {
                // Simple eviction: clear half the cache when full
                // This is rare enough that the cost is acceptable
                if (Interlocked.CompareExchange(ref _hotCacheSize, HotCacheMaxSize / 2, _hotCacheSize) == _hotCacheSize)
                {
                    // We won the race, do the eviction
                    int toRemove = HotCacheMaxSize / 2;
                    foreach (var key in _hotCache.Keys)
                    {
                        if (toRemove-- <= 0) break;
                        if (_hotCache.TryRemove(key, out _))
                        {
                            Interlocked.Decrement(ref _hotCacheSize);
                        }
                    }
                }
            }

            if (_hotCache.TryAdd(address, value))
            {
                Interlocked.Increment(ref _hotCacheSize);
            }
        }
    }
}

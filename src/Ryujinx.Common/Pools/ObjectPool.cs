using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Ryujinx.Common
{
    /// <summary>
    /// High-performance object pool with thread-local caching to minimize contention.
    /// </summary>
    public class ObjectPool<T>(Func<T> factory, int size = -1)
        where T : class
    {
        private int _size = size;
        private readonly ConcurrentBag<T> _globalPool = new();

        // Thread-local cache to reduce contention
        [ThreadStatic]
        private static T[] _threadLocalCache;
        [ThreadStatic]
        private static int _threadLocalCount;

        private const int ThreadLocalCacheSize = 8;

        // Statistics for monitoring
        private long _allocations;
        private long _releases;
        private long _threadLocalHits;

        /// <summary>
        /// Gets the total number of allocations made.
        /// </summary>
        public long TotalAllocations => Interlocked.Read(ref _allocations);

        /// <summary>
        /// Gets the thread-local cache hit ratio.
        /// </summary>
        public double ThreadLocalHitRatio => _allocations > 0 ? (double)_threadLocalHits / _allocations : 0.0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Allocate()
        {
            Interlocked.Increment(ref _allocations);

            // Fast path: try thread-local cache first
            if (_threadLocalCache != null && _threadLocalCount > 0)
            {
                Interlocked.Increment(ref _threadLocalHits);
                return _threadLocalCache[--_threadLocalCount];
            }

            // Medium path: try global pool
            if (_globalPool.TryTake(out T instance))
            {
                return instance;
            }

            // Slow path: create new instance
            return factory();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Release(T obj)
        {
            if (obj == null)
            {
                return;
            }

            Interlocked.Increment(ref _releases);

            // Fast path: try thread-local cache first
            _threadLocalCache ??= new T[ThreadLocalCacheSize];

            if (_threadLocalCount < ThreadLocalCacheSize)
            {
                _threadLocalCache[_threadLocalCount++] = obj;
                return;
            }

            // Overflow to global pool
            if (_size < 0 || _globalPool.Count < _size)
            {
                _globalPool.Add(obj);
            }
        }

        /// <summary>
        /// Allocates multiple objects at once for batch operations.
        /// More efficient than calling Allocate() multiple times.
        /// </summary>
        public void AllocateBatch(T[] buffer, int count)
        {
            for (int i = 0; i < count; i++)
            {
                buffer[i] = Allocate();
            }
        }

        /// <summary>
        /// Releases multiple objects at once for batch operations.
        /// </summary>
        public void ReleaseBatch(T[] buffer, int count)
        {
            for (int i = 0; i < count; i++)
            {
                Release(buffer[i]);
                buffer[i] = null;
            }
        }

        public void Clear()
        {
            _globalPool.Clear();
            if (_threadLocalCache != null)
            {
                Array.Clear(_threadLocalCache, 0, _threadLocalCount);
                _threadLocalCount = 0;
            }
        }

        /// <summary>
        /// Prewarms the pool by creating objects ahead of time.
        /// Call this during initialization to avoid allocation hitches during gameplay.
        /// </summary>
        public void Prewarm(int count)
        {
            for (int i = 0; i < count; i++)
            {
                _globalPool.Add(factory());
            }
        }
    }
}

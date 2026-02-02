using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Graphics.Gpu.Shader.DiskCache
{
    internal sealed class OptimizedShaderLoader : IDisposable
    {
        private readonly struct ShaderLoadRequest
        {
            public readonly int Index;
            public readonly long Offset;
            public readonly int Size;
            public readonly int Priority;

            public ShaderLoadRequest(int index, long offset, int size, int priority)
            {
                Index = index;
                Offset = offset;
                Size = size;
                Priority = priority;
            }
        }

        private readonly struct LoadedShaderData
        {
            public readonly int Index;
            public readonly byte[] Data;
            public readonly bool Success;

            public LoadedShaderData(int index, byte[] data, bool success)
            {
                Index = index;
                Data = data;
                Success = success;
            }
        }

        private readonly string _cachePath;
        private readonly ConcurrentQueue<ShaderLoadRequest> _highPriorityQueue;
        private readonly ConcurrentQueue<ShaderLoadRequest> _normalQueue;
        private readonly ConcurrentQueue<ShaderLoadRequest> _lowPriorityQueue;
        private readonly ConcurrentDictionary<int, LoadedShaderData> _loadedShaders;
        private readonly ConcurrentDictionary<int, byte[]> _shaderCache;
        private readonly Thread[] _loaderThreads;
        private readonly AutoResetEvent _workAvailable;
        private volatile bool _running;

        private MemoryMappedFile _mappedFile;
        private MemoryMappedViewAccessor _viewAccessor;
        private long _fileSize;

        private int _loadedCount;
        private int _totalRequested;
        private int _cacheHits;

        private const int MaxCacheSize = 256;
        private const int PrefetchAhead = 16;

        public int LoadedCount => _loadedCount;
        public int CacheHits => _cacheHits;
        public bool HasPendingWork => !_highPriorityQueue.IsEmpty || !_normalQueue.IsEmpty || !_lowPriorityQueue.IsEmpty;

        public OptimizedShaderLoader(string cachePath, int threadCount = 0)
        {
            _cachePath = cachePath;
            _highPriorityQueue = new ConcurrentQueue<ShaderLoadRequest>();
            _normalQueue = new ConcurrentQueue<ShaderLoadRequest>();
            _lowPriorityQueue = new ConcurrentQueue<ShaderLoadRequest>();
            _loadedShaders = new ConcurrentDictionary<int, LoadedShaderData>();
            _shaderCache = new ConcurrentDictionary<int, byte[]>();
            _workAvailable = new AutoResetEvent(false);
            _running = true;

            if (threadCount <= 0)
            {
                threadCount = Math.Max(2, Environment.ProcessorCount / 4);
            }

            _loaderThreads = new Thread[threadCount];
            for (int i = 0; i < threadCount; i++)
            {
                _loaderThreads[i] = new Thread(LoaderLoop)
                {
                    Name = $"GPU.OptimizedShaderLoader.{i}",
                    Priority = ThreadPriority.AboveNormal,
                    IsBackground = true
                };
            }
        }

        public bool Initialize(string dataFilePath)
        {
            try
            {
                if (!File.Exists(dataFilePath))
                {
                    return false;
                }

                FileInfo fileInfo = new(dataFilePath);
                _fileSize = fileInfo.Length;

                if (_fileSize > 0)
                {
                    _mappedFile = MemoryMappedFile.CreateFromFile(
                        dataFilePath,
                        FileMode.Open,
                        null,
                        0,
                        MemoryMappedFileAccess.Read);

                    _viewAccessor = _mappedFile.CreateViewAccessor(0, _fileSize, MemoryMappedFileAccess.Read);
                }

                for (int i = 0; i < _loaderThreads.Length; i++)
                {
                    _loaderThreads[i].Start();
                }

                Logger.Info?.Print(LogClass.Gpu, $"OptimizedShaderLoader initialized with {_loaderThreads.Length} threads, cache file size: {_fileSize / 1024}KB");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"Failed to initialize OptimizedShaderLoader: {ex.Message}");
                return false;
            }
        }

        public void QueueLoad(int index, long offset, int size, int priority = 1)
        {
            if (_shaderCache.TryGetValue(index, out byte[] cached))
            {
                _loadedShaders[index] = new LoadedShaderData(index, cached, true);
                Interlocked.Increment(ref _cacheHits);
                return;
            }

            Interlocked.Increment(ref _totalRequested);

            var request = new ShaderLoadRequest(index, offset, size, priority);

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

        public void QueueLoadBatch(IEnumerable<(int Index, long Offset, int Size)> requests, int priority = 1)
        {
            foreach (var (index, offset, size) in requests)
            {
                QueueLoad(index, offset, size, priority);
            }
        }

        public void PrefetchRange(int startIndex, int count, long baseOffset, int averageSize)
        {
            for (int i = 0; i < Math.Min(count, PrefetchAhead); i++)
            {
                int index = startIndex + i;
                if (!_shaderCache.ContainsKey(index) && !_loadedShaders.ContainsKey(index))
                {
                    QueueLoad(index, baseOffset + (i * averageSize), averageSize, 0);
                }
            }
        }

        public bool TryGetLoaded(int index, out byte[] data)
        {
            if (_loadedShaders.TryRemove(index, out LoadedShaderData loaded) && loaded.Success)
            {
                data = loaded.Data;

                if (_shaderCache.Count < MaxCacheSize)
                {
                    _shaderCache.TryAdd(index, data);
                }

                return true;
            }

            if (_shaderCache.TryGetValue(index, out data))
            {
                Interlocked.Increment(ref _cacheHits);
                return true;
            }

            data = null;
            return false;
        }

        public byte[] LoadSync(int index, long offset, int size)
        {
            if (_shaderCache.TryGetValue(index, out byte[] cached))
            {
                Interlocked.Increment(ref _cacheHits);
                return cached;
            }

            byte[] data = LoadFromMappedFile(offset, size);

            if (data != null && _shaderCache.Count < MaxCacheSize)
            {
                _shaderCache.TryAdd(index, data);
            }

            Interlocked.Increment(ref _loadedCount);
            return data;
        }

        public async Task<byte[]> LoadAsync(int index, long offset, int size, CancellationToken cancellationToken = default)
        {
            if (_shaderCache.TryGetValue(index, out byte[] cached))
            {
                Interlocked.Increment(ref _cacheHits);
                return cached;
            }

            QueueLoad(index, offset, size, 2);

            while (!cancellationToken.IsCancellationRequested)
            {
                if (TryGetLoaded(index, out byte[] data))
                {
                    return data;
                }

                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            }

            return null;
        }

        public void WaitForAll(int timeoutMs = 5000)
        {
            int waited = 0;
            while (HasPendingWork && waited < timeoutMs)
            {
                Thread.Sleep(10);
                waited += 10;
            }
        }

        private void LoaderLoop()
        {
            while (_running)
            {
                _workAvailable.WaitOne(50);

                while (_running && TryDequeue(out ShaderLoadRequest request))
                {
                    try
                    {
                        byte[] data = LoadFromMappedFile(request.Offset, request.Size);

                        _loadedShaders[request.Index] = new LoadedShaderData(
                            request.Index,
                            data,
                            data != null);

                        Interlocked.Increment(ref _loadedCount);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning?.Print(LogClass.Gpu, $"Failed to load shader {request.Index}: {ex.Message}");
                        _loadedShaders[request.Index] = new LoadedShaderData(request.Index, null, false);
                    }
                }
            }
        }

        private bool TryDequeue(out ShaderLoadRequest request)
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

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte[] LoadFromMappedFile(long offset, int size)
        {
            if (_viewAccessor == null || offset < 0 || size <= 0 || offset + size > _fileSize)
            {
                return null;
            }

            byte[] buffer = ArrayPool<byte>.Shared.Rent(size);

            try
            {
                _viewAccessor.ReadArray(offset, buffer, 0, size);

                byte[] result = new byte[size];
                Buffer.BlockCopy(buffer, 0, result, 0, size);
                return result;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public void ClearCache()
        {
            _shaderCache.Clear();
        }

        public void Dispose()
        {
            _running = false;
            _workAvailable.Set();

            foreach (Thread thread in _loaderThreads)
            {
                thread.Join(1000);
            }

            _viewAccessor?.Dispose();
            _mappedFile?.Dispose();
            _workAvailable.Dispose();

            Logger.Info?.Print(LogClass.Gpu, $"OptimizedShaderLoader: Loaded {_loadedCount} shaders, {_cacheHits} cache hits");
        }
    }
}

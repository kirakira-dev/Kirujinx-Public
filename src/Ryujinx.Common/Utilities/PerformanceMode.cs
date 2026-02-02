using System;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.Common.Utilities
{
    public static class PerformanceMode
    {
        private static bool _enabled;
        private static bool _dynamicResolutionEnabled = true;
        private static bool _gpuBatchingEnabled = true;
        private static bool _frameSkipEnabled = true;

        private static int _targetFps = 30;
        private static double _currentFps;
        private static double _averageFps;
        private static long _frameCount;
        private static long _lastFpsUpdateTicks;
        private static long _droppedFrames;

        private static float _currentResolutionScale = 1.0f;
        private static float _minResolutionScale = 0.5f;
        private static float _maxResolutionScale = 1.0f;

        private static int _gpuBatchSize = 8;
        private static int _frameSkipCounter;
        private static int _frameSkipInterval = 2;

        private static readonly object _lock = new();

        private const int FpsHistorySize = 60;
        private static readonly double[] _fpsHistory = new double[FpsHistorySize];
        private static int _fpsHistoryIndex;

        public static bool Enabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                if (value)
                {
                    Reset();
                }
            }
        }

        public static bool DynamicResolutionEnabled
        {
            get => _dynamicResolutionEnabled;
            set => _dynamicResolutionEnabled = value;
        }

        public static bool GpuBatchingEnabled
        {
            get => _gpuBatchingEnabled;
            set => _gpuBatchingEnabled = value;
        }

        public static bool FrameSkipEnabled
        {
            get => _frameSkipEnabled;
            set => _frameSkipEnabled = value;
        }

        public static int TargetFps
        {
            get => _targetFps;
            set => _targetFps = Math.Max(15, Math.Min(60, value));
        }

        public static double CurrentFps => _currentFps;
        public static double AverageFps => _averageFps;
        public static float CurrentResolutionScale => _currentResolutionScale;
        public static int GpuBatchSize => _gpuBatchSize;
        public static long DroppedFrames => _droppedFrames;

        public static bool ShouldRenderFrame()
        {
            if (!_enabled || !_frameSkipEnabled)
            {
                return true;
            }

            if (_currentFps >= _targetFps * 0.9)
            {
                return true;
            }

            _frameSkipCounter++;

            if (_frameSkipCounter >= _frameSkipInterval)
            {
                _frameSkipCounter = 0;
                return true;
            }

            double fpsDelta = _targetFps - _currentFps;
            if (fpsDelta > 15)
            {
                _frameSkipInterval = 3;
            }
            else if (fpsDelta > 10)
            {
                _frameSkipInterval = 2;
            }
            else
            {
                _frameSkipInterval = 4;
            }

            Interlocked.Increment(ref _droppedFrames);
            return false;
        }

        public static float GetDynamicResolutionScale()
        {
            if (!_enabled || !_dynamicResolutionEnabled)
            {
                return 1.0f;
            }

            return _currentResolutionScale;
        }

        public static int GetGpuBatchSize()
        {
            if (!_enabled || !_gpuBatchingEnabled)
            {
                return 1;
            }

            return _gpuBatchSize;
        }

        public static void RecordFrame(double frameTimeMs)
        {
            if (!_enabled)
            {
                return;
            }

            lock (_lock)
            {
                _frameCount++;
                double fps = frameTimeMs > 0 ? 1000.0 / frameTimeMs : 60.0;

                _fpsHistory[_fpsHistoryIndex] = fps;
                _fpsHistoryIndex = (_fpsHistoryIndex + 1) % FpsHistorySize;

                long currentTicks = Stopwatch.GetTimestamp();
                if (_lastFpsUpdateTicks == 0)
                {
                    _lastFpsUpdateTicks = currentTicks;
                }

                long elapsedTicks = currentTicks - _lastFpsUpdateTicks;
                double elapsedMs = elapsedTicks * 1000.0 / Stopwatch.Frequency;

                if (elapsedMs >= 500)
                {
                    _currentFps = fps;

                    double sum = 0;
                    int count = 0;
                    for (int i = 0; i < FpsHistorySize; i++)
                    {
                        if (_fpsHistory[i] > 0)
                        {
                            sum += _fpsHistory[i];
                            count++;
                        }
                    }
                    _averageFps = count > 0 ? sum / count : fps;

                    _lastFpsUpdateTicks = currentTicks;

                    UpdateDynamicSettings();
                }
            }
        }

        private static void UpdateDynamicSettings()
        {
            if (_dynamicResolutionEnabled)
            {
                double fpsRatio = _averageFps / _targetFps;

                if (fpsRatio < 0.7)
                {
                    _currentResolutionScale = Math.Max(_minResolutionScale, _currentResolutionScale - 0.1f);
                }
                else if (fpsRatio < 0.85)
                {
                    _currentResolutionScale = Math.Max(_minResolutionScale, _currentResolutionScale - 0.05f);
                }
                else if (fpsRatio > 1.1 && _currentResolutionScale < _maxResolutionScale)
                {
                    _currentResolutionScale = Math.Min(_maxResolutionScale, _currentResolutionScale + 0.02f);
                }
            }

            if (_gpuBatchingEnabled)
            {
                if (_averageFps < _targetFps * 0.7)
                {
                    _gpuBatchSize = 16;
                }
                else if (_averageFps < _targetFps * 0.85)
                {
                    _gpuBatchSize = 12;
                }
                else if (_averageFps < _targetFps)
                {
                    _gpuBatchSize = 8;
                }
                else
                {
                    _gpuBatchSize = 4;
                }
            }
        }

        public static void Reset()
        {
            lock (_lock)
            {
                _currentResolutionScale = _maxResolutionScale;
                _gpuBatchSize = 8;
                _frameSkipCounter = 0;
                _frameCount = 0;
                _droppedFrames = 0;
                _lastFpsUpdateTicks = 0;
                _currentFps = 0;
                _averageFps = 0;
                Array.Clear(_fpsHistory);
                _fpsHistoryIndex = 0;
            }
        }

        public static void SetResolutionLimits(float min, float max)
        {
            _minResolutionScale = Math.Max(0.25f, Math.Min(1.0f, min));
            _maxResolutionScale = Math.Max(_minResolutionScale, Math.Min(2.0f, max));
        }

        public static string GetStatistics()
        {
            return $"FPS: {_currentFps:F1} (Avg: {_averageFps:F1}), " +
                   $"Target: {_targetFps}, " +
                   $"Resolution: {_currentResolutionScale:F2}x, " +
                   $"Batch: {_gpuBatchSize}, " +
                   $"Dropped: {_droppedFrames}";
        }
    }
}

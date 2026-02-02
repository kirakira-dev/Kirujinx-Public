using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Ryujinx.Graphics.Gpu
{
    public class FrameSkipManager
    {
        private readonly GpuContext _context;

        private bool _enabled;
        private int _targetFps = 30;

        private double _currentFps;
        private double _smoothedFps;

        private int _skipCounter;
        private int _skipInterval = 3;
        private int _maxSkipInterval = 4;
        private int _minSkipInterval = 2;

        private long _totalFrames;
        private long _skippedFrames;
        private long _renderedFrames;

        private long _lastFrameTicks;
        private double _lastFrameTimeMs;

        private int _consecutiveSlowFrames;
        private const int SlowFrameThreshold = 3;

        private bool _inSkipMode;
        private int _framesUntilNextRender;

        public bool Enabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                if (!value)
                {
                    _inSkipMode = false;
                    _skipCounter = 0;
                }
            }
        }

        public int TargetFps
        {
            get => _targetFps;
            set => _targetFps = Math.Clamp(value, 15, 60);
        }

        public double CurrentFps => _currentFps;
        public long SkippedFrames => _skippedFrames;
        public long RenderedFrames => _renderedFrames;

        public double SkipRatio => _totalFrames > 0 ? (double)_skippedFrames / _totalFrames * 100 : 0;

        public FrameSkipManager(GpuContext context)
        {
            _context = context;
            _lastFrameTicks = Stopwatch.GetTimestamp();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ShouldRenderFrame()
        {
            if (!_enabled)
            {
                return true;
            }

            Interlocked.Increment(ref _totalFrames);

            long currentTicks = Stopwatch.GetTimestamp();
            double frameTimeMs = (currentTicks - _lastFrameTicks) * 1000.0 / Stopwatch.Frequency;
            _lastFrameTicks = currentTicks;
            _lastFrameTimeMs = frameTimeMs;

            _currentFps = frameTimeMs > 0 ? 1000.0 / frameTimeMs : _targetFps;
            _smoothedFps = _smoothedFps * 0.8 + _currentFps * 0.2;

            double targetFrameTime = 1000.0 / _targetFps;
            bool isSlow = frameTimeMs > targetFrameTime * 1.3;

            if (isSlow)
            {
                _consecutiveSlowFrames++;
            }
            else
            {
                _consecutiveSlowFrames = Math.Max(0, _consecutiveSlowFrames - 1);
            }

            if (_consecutiveSlowFrames >= SlowFrameThreshold && !_inSkipMode)
            {
                EnterSkipMode();
            }
            else if (_smoothedFps >= _targetFps * 0.95 && _inSkipMode)
            {
                ExitSkipMode();
            }

            if (!_inSkipMode)
            {
                Interlocked.Increment(ref _renderedFrames);
                return true;
            }

            _framesUntilNextRender--;

            if (_framesUntilNextRender <= 0)
            {
                _framesUntilNextRender = _skipInterval;
                Interlocked.Increment(ref _renderedFrames);
                return true;
            }

            Interlocked.Increment(ref _skippedFrames);
            return false;
        }

        private void EnterSkipMode()
        {
            _inSkipMode = true;

            double fpsRatio = _smoothedFps / _targetFps;

            if (fpsRatio < 0.5)
            {
                _skipInterval = _maxSkipInterval;
            }
            else if (fpsRatio < 0.7)
            {
                _skipInterval = 3;
            }
            else
            {
                _skipInterval = _minSkipInterval;
            }

            _framesUntilNextRender = _skipInterval;
            _consecutiveSlowFrames = 0;
        }

        private void ExitSkipMode()
        {
            _inSkipMode = false;
            _framesUntilNextRender = 0;
            _consecutiveSlowFrames = 0;
        }

        public void RecordFrameComplete()
        {
            if (_inSkipMode && _smoothedFps >= _targetFps * 0.9)
            {
                _skipInterval = Math.Max(_minSkipInterval, _skipInterval - 1);
            }
            else if (_inSkipMode && _smoothedFps < _targetFps * 0.7)
            {
                _skipInterval = Math.Min(_maxSkipInterval, _skipInterval + 1);
            }
        }

        public void Reset()
        {
            _inSkipMode = false;
            _skipCounter = 0;
            _framesUntilNextRender = 0;
            _consecutiveSlowFrames = 0;
            _totalFrames = 0;
            _skippedFrames = 0;
            _renderedFrames = 0;
            _smoothedFps = _targetFps;
            _lastFrameTicks = Stopwatch.GetTimestamp();
        }

        public void SetSkipLimits(int min, int max)
        {
            _minSkipInterval = Math.Max(1, min);
            _maxSkipInterval = Math.Max(_minSkipInterval, max);
            _skipInterval = Math.Clamp(_skipInterval, _minSkipInterval, _maxSkipInterval);
        }

        public string GetStatistics()
        {
            return $"FrameSkip: {(_inSkipMode ? "ON" : "OFF")}, " +
                   $"FPS: {_smoothedFps:F1}/{_targetFps}, " +
                   $"Skip: {SkipRatio:F1}%, " +
                   $"Rendered: {_renderedFrames}, Skipped: {_skippedFrames}";
        }
    }
}

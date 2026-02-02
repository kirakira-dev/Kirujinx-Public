using System;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.Graphics.Gpu
{
    public class DynamicResolutionScaler
    {
        private readonly GpuContext _context;

        private float _currentScale = 1.0f;
        private float _targetScale = 1.0f;
        private float _minScale = 0.5f;
        private float _maxScale = 1.0f;

        private int _targetFps = 30;
        private double _currentFps;
        private double _smoothedFps;

        private long _lastUpdateTicks;
        private int _framesSinceUpdate;
        private const int UpdateIntervalFrames = 15;

        private const float ScaleStep = 0.05f;
        private const float ScaleSmoothFactor = 0.3f;

        private bool _enabled = true;
        private int _consecutiveLowFrames;
        private int _consecutiveHighFrames;

        private const int LowFrameThreshold = 5;
        private const int HighFrameThreshold = 10;

        public float CurrentScale => _currentScale;
        public float TargetScale => _targetScale;
        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        public int TargetFps
        {
            get => _targetFps;
            set => _targetFps = Math.Max(15, Math.Min(120, value));
        }

        public DynamicResolutionScaler(GpuContext context)
        {
            _context = context;
            _lastUpdateTicks = Stopwatch.GetTimestamp();
        }

        public void SetScaleLimits(float min, float max)
        {
            _minScale = Math.Max(0.25f, Math.Min(1.0f, min));
            _maxScale = Math.Max(_minScale, Math.Min(2.0f, max));

            _currentScale = Math.Clamp(_currentScale, _minScale, _maxScale);
            _targetScale = Math.Clamp(_targetScale, _minScale, _maxScale);
        }

        public void RecordFrameTime(double frameTimeMs)
        {
            if (!_enabled)
            {
                return;
            }

            _framesSinceUpdate++;

            double instantFps = frameTimeMs > 0 ? 1000.0 / frameTimeMs : _targetFps;
            _currentFps = instantFps;

            _smoothedFps = _smoothedFps * 0.9 + instantFps * 0.1;

            if (_framesSinceUpdate >= UpdateIntervalFrames)
            {
                UpdateTargetScale();
                _framesSinceUpdate = 0;
            }

            SmoothScale();
        }

        private void UpdateTargetScale()
        {
            double fpsRatio = _smoothedFps / _targetFps;

            if (fpsRatio < 0.75)
            {
                _consecutiveLowFrames++;
                _consecutiveHighFrames = 0;

                if (_consecutiveLowFrames >= LowFrameThreshold)
                {
                    float reduction = fpsRatio < 0.5 ? ScaleStep * 3 : 
                                     fpsRatio < 0.65 ? ScaleStep * 2 : 
                                     ScaleStep;

                    _targetScale = Math.Max(_minScale, _targetScale - reduction);
                    _consecutiveLowFrames = 0;
                }
            }
            else if (fpsRatio > 1.05)
            {
                _consecutiveHighFrames++;
                _consecutiveLowFrames = 0;

                if (_consecutiveHighFrames >= HighFrameThreshold)
                {
                    _targetScale = Math.Min(_maxScale, _targetScale + ScaleStep * 0.5f);
                    _consecutiveHighFrames = 0;
                }
            }
            else
            {
                _consecutiveLowFrames = Math.Max(0, _consecutiveLowFrames - 1);
                _consecutiveHighFrames = Math.Max(0, _consecutiveHighFrames - 1);
            }
        }

        private void SmoothScale()
        {
            if (Math.Abs(_currentScale - _targetScale) > 0.001f)
            {
                _currentScale += (_targetScale - _currentScale) * ScaleSmoothFactor;

                _currentScale = Math.Clamp(_currentScale, _minScale, _maxScale);
            }
        }

        public int GetScaledWidth(int originalWidth)
        {
            if (!_enabled)
            {
                return originalWidth;
            }

            int scaled = (int)(originalWidth * _currentScale);
            return Math.Max(640, (scaled + 15) & ~15);
        }

        public int GetScaledHeight(int originalHeight)
        {
            if (!_enabled)
            {
                return originalHeight;
            }

            int scaled = (int)(originalHeight * _currentScale);
            return Math.Max(360, (scaled + 15) & ~15);
        }

        public void Reset()
        {
            _currentScale = _maxScale;
            _targetScale = _maxScale;
            _smoothedFps = _targetFps;
            _consecutiveLowFrames = 0;
            _consecutiveHighFrames = 0;
            _framesSinceUpdate = 0;
        }

        public void ForceScale(float scale)
        {
            _currentScale = Math.Clamp(scale, _minScale, _maxScale);
            _targetScale = _currentScale;
        }

        public string GetStatistics()
        {
            return $"DRS: {_currentScale:F2}x (target: {_targetScale:F2}x), " +
                   $"FPS: {_smoothedFps:F1}/{_targetFps}, " +
                   $"Low: {_consecutiveLowFrames}, High: {_consecutiveHighFrames}";
        }
    }
}

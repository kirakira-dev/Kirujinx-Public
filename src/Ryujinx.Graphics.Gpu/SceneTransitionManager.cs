using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.Graphics.Gpu
{
    public class SceneTransitionManager
    {
        private const int ShaderCompilationSpikeThreshold = 3;
        private const int TextureLoadSpikeThreshold = 5;
        private const int TransitionCooldownMs = 1000;
        private const int GracePeriodMs = 300;
        private const int ExtendedGracePeriodMs = 150;
        private const int FrameTimeHistorySize = 30;
        private const double FrameTimeSpikeThresholdMs = 25.0;

        private volatile bool _inTransition;
        private volatile bool _inGracePeriod;
        private volatile bool _inExtendedGrace;
        private long _transitionStartTicks;
        private long _gracePeriodEndTicks;
        private long _extendedGraceEndTicks;
        private long _lastTransitionEndTicks;

        private int _shadersCompiledThisFrame;
        private int _texturesLoadedThisFrame;
        private int _bufferUploadsThisFrame;
        private int _framesSinceTransitionStart;

        private long _totalShadersCompiled;
        private long _totalTexturesLoaded;
        private long _totalTransitions;
        private long _totalTransitionTimeMs;

        private float _adaptiveShaderThreshold;
        private float _adaptiveTextureThreshold;

        private readonly Queue<double> _frameTimeHistory;
        private readonly Queue<int> _shaderHistory;
        private readonly Queue<int> _textureHistory;
        private long _lastFrameTicks;
        private double _movingAverageFrameTime;

        private int _consecutiveHeavyFrames;
        private int _consecutiveLightFrames;

        public bool InTransition => _inTransition;
        public bool InGracePeriod => _inGracePeriod || _inExtendedGrace;
        public bool InStrictGracePeriod => _inGracePeriod;
        public long TotalTransitions => _totalTransitions;
        public float SyncTimeoutMultiplier => _inGracePeriod ? 0.05f : (_inExtendedGrace ? 0.1f : (_inTransition ? 0.3f : 1.0f));
        public bool ShouldDeferShaderCompilation => 
            (_inTransition || _inGracePeriod || _inExtendedGrace) || 
            Ryujinx.Common.Utilities.SocketPriorityManager.ShouldPrioritizeOverShaders() ||
            (Ryujinx.Common.Utilities.FpsLockManager.LockTo30Fps && !Ryujinx.Common.Utilities.FpsLockManager.CanDoMoreLoadingWork());
        public bool ShouldThrottleTextureLoads => _inGracePeriod;
        public int MaxTexturesPerFrame => _inGracePeriod ? 2 : (_inTransition ? 5 : 
            (Ryujinx.Common.Utilities.FpsLockManager.LockTo30Fps ? Ryujinx.Common.Utilities.FpsLockManager.GetMaxTexturesPerFrame() : 20));
        public int MaxShadersPerFrame => _inGracePeriod ? 1 : (_inTransition ? 2 : 
            (Ryujinx.Common.Utilities.FpsLockManager.LockTo30Fps ? Ryujinx.Common.Utilities.FpsLockManager.GetMaxShadersPerFrame() : 8));

        public event Action TransitionStarted;
        public event Action TransitionEnded;

        public SceneTransitionManager()
        {
            _adaptiveShaderThreshold = ShaderCompilationSpikeThreshold;
            _adaptiveTextureThreshold = TextureLoadSpikeThreshold;
            _frameTimeHistory = new Queue<double>(FrameTimeHistorySize);
            _shaderHistory = new Queue<int>(FrameTimeHistorySize);
            _textureHistory = new Queue<int>(FrameTimeHistorySize);
            _lastFrameTicks = Stopwatch.GetTimestamp();
        }

        public void RecordShaderCompilation()
        {
            Interlocked.Increment(ref _shadersCompiledThisFrame);
            Interlocked.Increment(ref _totalShadersCompiled);
        }

        public void RecordTextureLoad()
        {
            Interlocked.Increment(ref _texturesLoadedThisFrame);
            Interlocked.Increment(ref _totalTexturesLoaded);
        }

        public void RecordBufferUpload()
        {
            Interlocked.Increment(ref _bufferUploadsThisFrame);
        }

        public void EndFrame()
        {
            long currentTicks = Stopwatch.GetTimestamp();
            long ticksPerMs = Stopwatch.Frequency / 1000;

            double frameTimeMs = (double)(currentTicks - _lastFrameTicks) / Stopwatch.Frequency * 1000.0;
            _lastFrameTicks = currentTicks;

            RecordFrameMetrics(frameTimeMs);

            if (_inExtendedGrace && currentTicks >= _extendedGraceEndTicks)
            {
                _inExtendedGrace = false;
            }

            if (_inGracePeriod && currentTicks >= _gracePeriodEndTicks)
            {
                _inGracePeriod = false;
                _inExtendedGrace = true;
                _extendedGraceEndTicks = currentTicks + (ExtendedGracePeriodMs * ticksPerMs);
                Logger.Debug?.Print(LogClass.Gpu, "Scene transition grace period ended, entering extended grace");
            }

            if (_inTransition)
            {
                _framesSinceTransitionStart++;
                long elapsedMs = (currentTicks - _transitionStartTicks) / ticksPerMs;

                bool lowActivity = _shadersCompiledThisFrame < 2 && _texturesLoadedThisFrame < 3;
                bool frameTimeStable = frameTimeMs < FrameTimeSpikeThresholdMs;

                if (lowActivity && frameTimeStable)
                {
                    _consecutiveLightFrames++;
                    _consecutiveHeavyFrames = 0;
                }
                else
                {
                    _consecutiveHeavyFrames++;
                    _consecutiveLightFrames = 0;
                }

                bool timedOut = elapsedMs > TransitionCooldownMs;
                bool stableEnough = _consecutiveLightFrames >= 20;

                if (timedOut || stableEnough)
                {
                    EndTransition(currentTicks, ticksPerMs);
                }
            }
            else
            {
                bool shaderSpike = _shadersCompiledThisFrame >= _adaptiveShaderThreshold;
                bool textureSpike = _texturesLoadedThisFrame >= _adaptiveTextureThreshold;
                bool combinedSpike = _shadersCompiledThisFrame >= 2 && _texturesLoadedThisFrame >= 3;
                bool frameTimeSpike = frameTimeMs > FrameTimeSpikeThresholdMs && (_shadersCompiledThisFrame > 0 || _texturesLoadedThisFrame > 2);

                bool heavyFramePattern = DetectHeavyFramePattern();

                long msSinceLastTransition = (currentTicks - _lastTransitionEndTicks) / ticksPerMs;
                bool cooldownPassed = msSinceLastTransition > TransitionCooldownMs;

                if (cooldownPassed && (shaderSpike || textureSpike || combinedSpike || frameTimeSpike || heavyFramePattern))
                {
                    StartTransition(currentTicks, ticksPerMs);
                }
            }

            UpdateAdaptiveThresholds();

            _shaderHistory.Enqueue(_shadersCompiledThisFrame);
            _textureHistory.Enqueue(_texturesLoadedThisFrame);
            while (_shaderHistory.Count > FrameTimeHistorySize)
            {
                _shaderHistory.Dequeue();
            }
            while (_textureHistory.Count > FrameTimeHistorySize)
            {
                _textureHistory.Dequeue();
            }

            _shadersCompiledThisFrame = 0;
            _texturesLoadedThisFrame = 0;
            _bufferUploadsThisFrame = 0;
        }

        private void RecordFrameMetrics(double frameTimeMs)
        {
            _frameTimeHistory.Enqueue(frameTimeMs);
            while (_frameTimeHistory.Count > FrameTimeHistorySize)
            {
                _frameTimeHistory.Dequeue();
            }

            double sum = 0;
            foreach (double ft in _frameTimeHistory)
            {
                sum += ft;
            }
            _movingAverageFrameTime = _frameTimeHistory.Count > 0 ? sum / _frameTimeHistory.Count : 16.67;
        }

        private bool DetectHeavyFramePattern()
        {
            if (_frameTimeHistory.Count < 5)
            {
                return false;
            }

            int heavyCount = 0;
            foreach (double ft in _frameTimeHistory)
            {
                if (ft > FrameTimeSpikeThresholdMs)
                {
                    heavyCount++;
                }
            }

            return heavyCount >= 3;
        }

        private void StartTransition(long currentTicks, long ticksPerMs)
        {
            _inTransition = true;
            _inGracePeriod = true;
            _inExtendedGrace = false;
            _transitionStartTicks = currentTicks;
            _gracePeriodEndTicks = currentTicks + (GracePeriodMs * ticksPerMs);
            _framesSinceTransitionStart = 0;
            _consecutiveHeavyFrames = 0;
            _consecutiveLightFrames = 0;
            Interlocked.Increment(ref _totalTransitions);

            Logger.Info?.Print(LogClass.Gpu, $"Scene transition detected (shaders: {_shadersCompiledThisFrame}, textures: {_texturesLoadedThisFrame}, avg frame: {_movingAverageFrameTime:F1}ms)");
            TransitionStarted?.Invoke();
        }

        private void EndTransition(long currentTicks, long ticksPerMs)
        {
            long elapsedMs = (currentTicks - _transitionStartTicks) / ticksPerMs;
            _totalTransitionTimeMs += elapsedMs;

            _inTransition = false;
            _inGracePeriod = false;
            _inExtendedGrace = true;
            _extendedGraceEndTicks = currentTicks + (ExtendedGracePeriodMs * ticksPerMs);
            _lastTransitionEndTicks = currentTicks;
            _consecutiveHeavyFrames = 0;
            _consecutiveLightFrames = 0;

            Logger.Info?.Print(LogClass.Gpu, $"Scene transition ended after {elapsedMs}ms ({_framesSinceTransitionStart} frames)");
            TransitionEnded?.Invoke();
        }

        private void UpdateAdaptiveThresholds()
        {
            const float AdaptRate = 0.01f;

            if (_shadersCompiledThisFrame > 0)
            {
                float target = Math.Max(ShaderCompilationSpikeThreshold, _shadersCompiledThisFrame * 0.8f);
                _adaptiveShaderThreshold = _adaptiveShaderThreshold * (1 - AdaptRate) + target * AdaptRate;
            }

            if (_texturesLoadedThisFrame > 0)
            {
                float target = Math.Max(TextureLoadSpikeThreshold, _texturesLoadedThisFrame * 0.8f);
                _adaptiveTextureThreshold = _adaptiveTextureThreshold * (1 - AdaptRate) + target * AdaptRate;
            }
        }

        public long GetAdjustedSyncTimeout(long normalTimeoutNs)
        {
            if (_inGracePeriod)
            {
                return Math.Min(normalTimeoutNs, 10_000_000);
            }
            else if (_inTransition)
            {
                return Math.Min(normalTimeoutNs, 100_000_000);
            }

            return normalTimeoutNs;
        }

        public int GetRecommendedFrameSkip()
        {
            if (_inGracePeriod)
            {
                return 2;
            }
            else if (_inExtendedGrace)
            {
                return 1;
            }
            else if (_inTransition)
            {
                return 1;
            }

            return 0;
        }

        public bool ShouldAllowShaderCompilation()
        {
            if (_inGracePeriod)
            {
                return _shadersCompiledThisFrame < 1;
            }

            if (_inTransition || _inExtendedGrace)
            {
                return _shadersCompiledThisFrame < MaxShadersPerFrame;
            }

            return true;
        }

        public bool ShouldAllowTextureLoad()
        {
            if (_inGracePeriod)
            {
                return _texturesLoadedThisFrame < 2;
            }

            if (_inTransition || _inExtendedGrace)
            {
                return _texturesLoadedThisFrame < MaxTexturesPerFrame;
            }

            return true;
        }

        public double GetCurrentFrameTimeMs()
        {
            return _movingAverageFrameTime;
        }

        public bool IsFrameTimeCritical()
        {
            return _movingAverageFrameTime > FrameTimeSpikeThresholdMs * 1.5;
        }

        public string GetStatistics()
        {
            long avgTransitionMs = _totalTransitions > 0 ? _totalTransitionTimeMs / _totalTransitions : 0;
            return $"Transitions: {_totalTransitions}, Avg Duration: {avgTransitionMs}ms, " +
                   $"Shaders: {_totalShadersCompiled}, Textures: {_totalTexturesLoaded}";
        }

        public void ForceTransitionMode()
        {
            if (!_inTransition)
            {
                long currentTicks = Stopwatch.GetTimestamp();
                long ticksPerMs = Stopwatch.Frequency / 1000;
                StartTransition(currentTicks, ticksPerMs);
            }
        }

        public void Reset()
        {
            _inTransition = false;
            _inGracePeriod = false;
            _transitionStartTicks = 0;
            _gracePeriodEndTicks = 0;
            _lastTransitionEndTicks = 0;
            _shadersCompiledThisFrame = 0;
            _texturesLoadedThisFrame = 0;
            _bufferUploadsThisFrame = 0;
            _framesSinceTransitionStart = 0;
            _adaptiveShaderThreshold = ShaderCompilationSpikeThreshold;
            _adaptiveTextureThreshold = TextureLoadSpikeThreshold;
        }
    }
}

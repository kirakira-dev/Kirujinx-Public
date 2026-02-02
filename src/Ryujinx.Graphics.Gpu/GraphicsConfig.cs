namespace Ryujinx.Graphics.Gpu
{
    /// <summary>
    /// General GPU and graphics configuration.
    /// </summary>
    public static class GraphicsConfig
    {
        /// <summary>
        /// Resolution scale.
        /// </summary>
        public static float ResScale { get; set; } = 1f;

        /// <summary>
        /// Max Anisotropy. Values range from 0 - 16. Set to -1 to let the game decide.
        /// </summary>
        public static float MaxAnisotropy { get; set; } = -1;

        /// <summary>
        /// Base directory used to write shader code dumps.
        /// Set to null to disable code dumping.
        /// </summary>
        public static string ShadersDumpPath { get; set; }

        /// <summary>
        /// Fast GPU time calculates the internal GPU time ticks as if the GPU was capable of
        /// processing commands almost instantly, instead of using the host timer.
        /// This can avoid lower resolution on some games when GPU performance is poor.
        /// </summary>
        public static bool FastGpuTime { get; set; } = true;

        /// <summary>
        /// Enables or disables fast 2d engine texture copies entirely on CPU when possible.
        /// Reduces stuttering and # of textures in games that copy textures around for streaming,
        /// as textures will not need to be created for the copy, and the data does not need to be
        /// flushed from GPU.
        /// </summary>
        public static bool Fast2DCopy { get; set; } = true;

        /// <summary>
        /// Enables or disables the Just-in-Time compiler for GPU Macro code.
        /// </summary>
        public static bool EnableMacroJit { get; set; } = true;

        /// <summary>
        /// Enables or disables high-level emulation of common GPU Macro code.
        /// </summary>
        public static bool EnableMacroHLE { get; set; } = true;

        /// <summary>
        /// Title id of the current running game.
        /// Used by the shader cache.
        /// </summary>
        public static string TitleId { get; set; }

        /// <summary>
        /// Enables or disables the shader cache.
        /// </summary>
        public static bool EnableShaderCache { get; set; }

        /// <summary>
        /// Enables or disables shader SPIR-V compilation.
        /// </summary>
        public static bool EnableSpirvCompilationOnVulkan { get; set; } = true;

        /// <summary>
        /// Enables or disables recompression of compressed textures that are not natively supported by the host.
        /// </summary>
        public static bool EnableTextureRecompression { get; set; } = false;

        /// <summary>
        /// Enables or disables color space passthrough, if available.
        /// </summary>
        public static bool EnableColorSpacePassthrough { get; set; } = false;

        // ==================== PERFORMANCE OPTIMIZATIONS ====================

        /// <summary>
        /// Enables aggressive buffer coalescing to reduce GPU memory allocations.
        /// Can improve performance in games with many small buffers.
        /// </summary>
        public static bool EnableAggressiveBufferCoalescing { get; set; } = true;

        /// <summary>
        /// Minimum size for buffer coalescing. Buffers smaller than this will be combined.
        /// Default: 64KB. Lower values = more coalescing = better performance but more memory usage.
        /// </summary>
        public static int BufferCoalescingThreshold { get; set; } = 65536;

        /// <summary>
        /// Enables texture streaming to reduce VRAM usage and improve loading times.
        /// Textures are loaded progressively based on usage.
        /// </summary>
        public static bool EnableTextureStreaming { get; set; } = true;

        /// <summary>
        /// Maximum texture cache size in megabytes. Higher values reduce texture thrashing.
        /// Default: 2048 MB (2 GB).
        /// </summary>
        public static int TextureCacheMaxSizeMB { get; set; } = 2048;

        /// <summary>
        /// Enables deferred shader compilation on a background thread.
        /// Reduces stuttering when new shaders are encountered.
        /// </summary>
        public static bool EnableAsyncShaderCompilation { get; set; } = true;

        /// <summary>
        /// Number of shader compilation worker threads.
        /// Set to 0 for automatic (uses number of CPU cores - 2, minimum 1).
        /// </summary>
        public static int ShaderCompilationThreads { get; set; } = 0;

        /// <summary>
        /// Enables fast buffer updates using persistent mapping where supported.
        /// Improves performance of frequent uniform buffer updates.
        /// </summary>
        public static bool EnableFastBufferUpdates { get; set; } = true;

        /// <summary>
        /// Enables texture copy optimization using compute shaders when beneficial.
        /// </summary>
        public static bool EnableComputeTextureCopy { get; set; } = true;

        /// <summary>
        /// Skip validation of shader bytecode when loading from cache.
        /// Slightly faster loading but may cause issues with corrupted cache.
        /// </summary>
        public static bool SkipShaderCacheValidation { get; set; } = false;

        /// <summary>
        /// Enables preemptive texture loading based on predicted access patterns.
        /// </summary>
        public static bool EnablePredictiveTextureLoading { get; set; } = true;

        /// <summary>
        /// Gets the optimal number of shader compilation threads.
        /// Uses 1.5x multiplier for improved performance.
        /// </summary>
        public static int GetShaderCompilationThreadCount()
        {
            if (ShaderCompilationThreads > 0)
            {
                return ShaderCompilationThreads;
            }

            int cores = System.Environment.ProcessorCount;
            int baseCount = System.Math.Max(1, (cores - 2) / 2);
            int scaled = (int)System.Math.Ceiling(baseCount * 1.5f);
            return System.Math.Clamp(scaled, 2, System.Math.Max(8, cores - 2));
        }
    }
}

using ARMeilleure.Decoders;
using ARMeilleure.Memory;
using ARMeilleure.State;
using Ryujinx.Common.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace ARMeilleure.Translation.PTC
{
    public class FullTranslationCache
    {
        private const int InstructionSize = 4;
        private const int ScanBlockSize = 0x1000;
        private const int MaxFunctionsPerScan = 50000;

        public static bool EnableFullTranslation { get; set; } = false;

        public event Action<FullPtcState, int, int> StateChanged;

        private volatile int _translateCount;
        private volatile int _translateTotalCount;
        private volatile bool _isScanning;

        public bool IsScanning => _isScanning;
        public int TranslatedCount => _translateCount;
        public int TotalCount => _translateTotalCount;

        public void PerformFullTranslation(Translator translator, IMemoryManager memory, ulong codeStart, ulong codeSize)
        {
            if (!EnableFullTranslation || codeSize == 0)
            {
                return;
            }

            _isScanning = true;
            _translateCount = 0;

            Logger.Info?.Print(LogClass.Ptc, $"Starting full translation scan of code region: 0x{codeStart:X16} - 0x{codeStart + codeSize:X16} ({codeSize} bytes)");

            StateChanged?.Invoke(FullPtcState.Scanning, 0, 0);

            var functionAddresses = ScanForFunctionEntryPoints(memory, codeStart, codeSize);

            _translateTotalCount = functionAddresses.Count;

            if (_translateTotalCount == 0)
            {
                Logger.Info?.Print(LogClass.Ptc, "No function entry points found during scan");
                _isScanning = false;
                return;
            }

            Logger.Info?.Print(LogClass.Ptc, $"Found {_translateTotalCount} potential function entry points, starting translation...");

            StateChanged?.Invoke(FullPtcState.Translating, 0, _translateTotalCount);

            var queue = new ConcurrentQueue<ulong>(functionAddresses);

            int processorCount = Environment.ProcessorCount;
            int threadCount = Math.Max(2, (int)Math.Ceiling(processorCount * 1.5f));
            threadCount = Math.Min(threadCount, Math.Max(processorCount * 2, 16));

            using var progressEvent = new AutoResetEvent(false);

            var progressThread = new Thread(() => ReportProgress(progressEvent))
            {
                Name = "FullPtc.ProgressReporter",
                Priority = ThreadPriority.Lowest,
                IsBackground = true,
            };
            progressThread.Start();

            var sw = Stopwatch.StartNew();

            var threads = Enumerable.Range(0, threadCount)
                .Select(idx => new Thread(() => TranslateWorker(translator, queue, memory))
                {
                    IsBackground = true,
                    Name = $"FullPtc.TranslateThread.{idx}"
                })
                .ToList();

            foreach (var thread in threads)
            {
                thread.Start();
            }

            foreach (var thread in threads)
            {
                thread.Join();
            }

            progressEvent.Set();
            progressThread.Join();

            sw.Stop();

            _isScanning = false;

            StateChanged?.Invoke(FullPtcState.Completed, _translateCount, _translateTotalCount);

            Logger.Info?.Print(LogClass.Ptc,
                $"Full translation completed: {_translateCount} of {_translateTotalCount} functions translated in {sw.Elapsed.TotalSeconds:F2} seconds");
        }

        private List<ulong> ScanForFunctionEntryPoints(IMemoryManager memory, ulong codeStart, ulong codeSize)
        {
            var entryPoints = new HashSet<ulong>();
            ulong codeEnd = codeStart + codeSize;

            for (ulong addr = codeStart; addr < codeEnd && entryPoints.Count < MaxFunctionsPerScan; addr += InstructionSize)
            {
                try
                {
                    if (!memory.IsMapped(addr))
                    {
                        addr += ScanBlockSize - InstructionSize;
                        continue;
                    }

                    uint instruction = memory.Read<uint>(addr);

                    if (IsPotentialFunctionStart(instruction, addr, codeStart, codeEnd))
                    {
                        entryPoints.Add(addr);
                    }

                    if (IsBranchWithLink(instruction, addr, out ulong target))
                    {
                        if (target >= codeStart && target < codeEnd)
                        {
                            entryPoints.Add(target);
                        }
                    }
                }
                catch
                {
                    addr += ScanBlockSize - InstructionSize;
                }
            }

            entryPoints.Add(codeStart);

            return entryPoints.OrderBy(x => x).ToList();
        }

        private static bool IsPotentialFunctionStart(uint instruction, ulong addr, ulong codeStart, ulong codeEnd)
        {
            uint opcode = instruction >> 24;

            if ((instruction & 0xFFC003FF) == 0xA9007BFD)
            {
                return true;
            }

            if ((instruction & 0xFF0003FF) == 0xD10003FF)
            {
                return true;
            }

            if ((instruction & 0xFFE0001F) == 0xAA0003E0)
            {
                return true;
            }

            if (addr == codeStart)
            {
                return true;
            }

            return false;
        }

        private static bool IsBranchWithLink(uint instruction, ulong currentAddr, out ulong target)
        {
            target = 0;

            if ((instruction & 0xFC000000) == 0x94000000)
            {
                int imm26 = (int)(instruction & 0x03FFFFFF);
                if ((imm26 & 0x02000000) != 0)
                {
                    imm26 |= unchecked((int)0xFC000000);
                }

                target = currentAddr + (ulong)(imm26 * 4);
                return true;
            }

            return false;
        }

        private void TranslateWorker(Translator translator, ConcurrentQueue<ulong> queue, IMemoryManager memory)
        {
            while (queue.TryDequeue(out ulong address))
            {
                try
                {
                    if (translator.Functions.ContainsKey(address))
                    {
                        Interlocked.Increment(ref _translateCount);
                        continue;
                    }

                    if (!memory.IsMapped(address))
                    {
                        Interlocked.Increment(ref _translateCount);
                        continue;
                    }

                    var func = translator.GetOrTranslate(address, ExecutionMode.Aarch64);

                    if (func != null)
                    {
                        translator.RegisterFunction(address, func);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug?.Print(LogClass.Ptc, $"Failed to translate function at 0x{address:X16}: {ex.Message}");
                }

                Interlocked.Increment(ref _translateCount);
            }
        }

        private void ReportProgress(AutoResetEvent endEvent)
        {
            const int RefreshRate = 100;

            int lastCount = 0;

            while (!endEvent.WaitOne(RefreshRate))
            {
                int currentCount = _translateCount;
                if (currentCount != lastCount)
                {
                    StateChanged?.Invoke(FullPtcState.Translating, currentCount, _translateTotalCount);
                    lastCount = currentCount;
                }
            }
        }
    }

    public enum FullPtcState
    {
        Idle,
        Scanning,
        Translating,
        Completed,
        Failed
    }
}

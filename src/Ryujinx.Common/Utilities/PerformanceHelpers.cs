using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Common.Utilities
{
    public static class PerformanceHelpers
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AdaptiveSpinWait(ref int spinCount)
        {
            if (spinCount < 10)
            {
                Thread.SpinWait(1 << spinCount);
                spinCount++;
            }
            else if (spinCount < 20)
            {
                Thread.Yield();
                spinCount++;
            }
            else
            {
                Thread.Sleep(0);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void FastCopy(Span<byte> destination, ReadOnlySpan<byte> source)
        {
            if (source.Length <= 64 || !Vector.IsHardwareAccelerated)
            {
                source.CopyTo(destination);
                return;
            }

            FastCopyInternal(destination, source);
        }

        private static void FastCopyInternal(Span<byte> destination, ReadOnlySpan<byte> source)
        {
            int vectorSize = Vector<byte>.Count;
            int i = 0;

            int vectorizableLength = source.Length - (source.Length % vectorSize);
            for (; i < vectorizableLength; i += vectorSize)
            {
                var vector = new Vector<byte>(source.Slice(i, vectorSize));
                vector.CopyTo(destination.Slice(i, vectorSize));
            }

            for (; i < source.Length; i++)
            {
                destination[i] = source[i];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool FastEquals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            if (a.Length != b.Length)
            {
                return false;
            }

            return a.SequenceEqual(b);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint NextPowerOfTwo(uint value)
        {
            return BitOperations.RoundUpToPowerOf2(value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong NextPowerOfTwo(ulong value)
        {
            return BitOperations.RoundUpToPowerOf2(value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong AlignUp(ulong value, ulong alignment)
        {
            return (value + alignment - 1) & ~(alignment - 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong AlignDown(ulong value, ulong alignment)
        {
            return value & ~(alignment - 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsAligned(ulong value, ulong alignment)
        {
            return (value & (alignment - 1)) == 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void FastZero(Span<byte> destination)
        {
            destination.Clear();
        }

        public struct FastRwLock
        {
            private const int WriterFlag = unchecked((int)0x80000000);

            private int _state;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void EnterReadLock()
            {
                int spinCount = 0;
                while (true)
                {
                    int currentState = _state;
                    if (currentState >= 0 && Interlocked.CompareExchange(ref _state, currentState + 1, currentState) == currentState)
                    {
                        return;
                    }
                    AdaptiveSpinWait(ref spinCount);
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void ExitReadLock()
            {
                Interlocked.Decrement(ref _state);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void EnterWriteLock()
            {
                int spinCount = 0;
                while (true)
                {
                    if (Interlocked.CompareExchange(ref _state, WriterFlag, 0) == 0)
                    {
                        return;
                    }
                    AdaptiveSpinWait(ref spinCount);
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void ExitWriteLock()
            {
                Volatile.Write(ref _state, 0);
            }
        }

        public class SpscQueue<T>
        {
            private readonly T[] _buffer;
            private readonly int _mask;
            private volatile int _head;
            private volatile int _tail;

            public SpscQueue(int capacity)
            {
                capacity = (int)NextPowerOfTwo((uint)capacity);
                _buffer = new T[capacity];
                _mask = capacity - 1;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool TryEnqueue(T item)
            {
                int tail = _tail;
                int nextTail = (tail + 1) & _mask;

                if (nextTail == _head)
                {
                    return false;
                }

                _buffer[tail] = item;
                _tail = nextTail;
                return true;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool TryDequeue(out T item)
            {
                int head = _head;

                if (head == _tail)
                {
                    item = default;
                    return false;
                }

                item = _buffer[head];
                _buffer[head] = default;
                _head = (head + 1) & _mask;
                return true;
            }

            public int Count => (_tail - _head) & _mask;
            public bool IsEmpty => _head == _tail;
        }

        public static int GetOptimalBufferSize()
        {
            return 256 * 1024;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void PrefetchRead(ref byte location)
        {
            _ = Volatile.Read(ref location);
        }
    }
}

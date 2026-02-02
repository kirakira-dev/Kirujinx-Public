using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ryujinx.Common
{
    public static class BinaryReaderExtensions
    {
        public static long BytesRemaining(this BinaryReader reader)
        {
            try
            {
                return reader.BaseStream.Length - reader.BaseStream.Position;
            }
            catch
            {
                return 0;
            }
        }

        public static bool HasEnoughBytes(this BinaryReader reader, int count)
        {
            return BytesRemaining(reader) >= count;
        }

        public static bool TryReadInt32(this BinaryReader reader, out int value)
        {
            if (!HasEnoughBytes(reader, sizeof(int)))
            {
                value = 0;
                return false;
            }

            value = reader.ReadInt32();
            return true;
        }

        public static bool TryReadUInt32(this BinaryReader reader, out uint value)
        {
            if (!HasEnoughBytes(reader, sizeof(uint)))
            {
                value = 0;
                return false;
            }

            value = reader.ReadUInt32();
            return true;
        }

        public static bool TryReadInt64(this BinaryReader reader, out long value)
        {
            if (!HasEnoughBytes(reader, sizeof(long)))
            {
                value = 0;
                return false;
            }

            value = reader.ReadInt64();
            return true;
        }

        public static bool TryReadUInt64(this BinaryReader reader, out ulong value)
        {
            if (!HasEnoughBytes(reader, sizeof(ulong)))
            {
                value = 0;
                return false;
            }

            value = reader.ReadUInt64();
            return true;
        }

        public static T ReadStruct<T>(this BinaryReader reader) where T : unmanaged
        {
            int size = Unsafe.SizeOf<T>();
            byte[] bytes = reader.ReadBytes(size);

            if (bytes.Length < size)
            {
                return default;
            }

            return MemoryMarshal.Cast<byte, T>(bytes)[0];
        }

        public static bool TryReadStruct<T>(this BinaryReader reader, out T value) where T : unmanaged
        {
            int size = Unsafe.SizeOf<T>();

            if (!HasEnoughBytes(reader, size))
            {
                value = default;
                return false;
            }

            byte[] bytes = reader.ReadBytes(size);

            if (bytes.Length < size)
            {
                value = default;
                return false;
            }

            value = MemoryMarshal.Cast<byte, T>(bytes)[0];
            return true;
        }
    }
}

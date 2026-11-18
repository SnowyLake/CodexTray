namespace CodexTray.Core;

internal static class ProtobufFields
{
    public static ReadOnlySpan<byte> ReadBytes(ReadOnlySpan<byte> data, int fieldNumber)
    {
        return TryRead(data, fieldNumber, 2, out ReadOnlySpan<byte> value, out _) ? value : [];
    }

    public static long ReadInt64(ReadOnlySpan<byte> data, int fieldNumber, long fallback = 0)
    {
        return TryRead(data, fieldNumber, 0, out _, out ulong value) ? unchecked((long)value) : fallback;
    }

    private static bool TryRead(ReadOnlySpan<byte> data, int fieldNumber, int wireType, out ReadOnlySpan<byte> bytes, out ulong number)
    {
        bytes = default;
        number = 0;
        while (!data.IsEmpty && TryReadVarint(ref data, out ulong tag) && tag is >= 8 and <= uint.MaxValue)
        {
            int type = (int)(tag & 7);
            bool matches = (tag >> 3) == (uint)fieldNumber && type == wireType;
            if (type == 0)
            {
                if (!TryReadVarint(ref data, out ulong value))
                {
                    return false;
                }
                if (matches)
                {
                    number = value;
                    return true;
                }
                continue;
            }

            ulong length = type == 1 ? 8UL : type == 5 ? 4UL : ulong.MaxValue;
            if (type == 2 && !TryReadVarint(ref data, out length))
            {
                return false;
            }
            if (length > (ulong)data.Length)
            {
                return false;
            }
            if (matches)
            {
                bytes = data[..(int)length];
                return true;
            }
            data = data[(int)length..];
        }
        return false;
    }

    private static bool TryReadVarint(ref ReadOnlySpan<byte> data, out ulong value)
    {
        value = 0;
        for (int index = 0; index < Math.Min(10, data.Length); index++)
        {
            byte current = data[index];
            if (index == 9 && current > 1)
            {
                break;
            }
            value |= (ulong)(current & 127) << (index * 7);
            if (current < 128)
            {
                data = data[(index + 1)..];
                return true;
            }
        }
        return false;
    }
}

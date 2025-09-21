using System;
using System.Text;

namespace DemiCatPlugin;

internal static class ImGuiTextUtil
{
    public static byte[] MakeUtf8Buffer(string? text, int capacity)
    {
        if (capacity < 1)
        {
            capacity = 1;
        }

        var buffer = new byte[capacity];
        if (!string.IsNullOrEmpty(text))
        {
            var encoded = Encoding.UTF8.GetBytes(text);
            var length = Math.Min(encoded.Length, capacity - 1); // leave room for NUL
            Array.Copy(encoded, 0, buffer, 0, length);
            buffer[length] = 0;
        }
        else
        {
            buffer[0] = 0;
        }

        return buffer;
    }

    public static string ReadUtf8Buffer(byte[] buffer)
    {
        var nulIndex = Array.IndexOf(buffer, (byte)0);
        var length = nulIndex >= 0 ? nulIndex : buffer.Length;
        return Encoding.UTF8.GetString(buffer, 0, length);
    }
}

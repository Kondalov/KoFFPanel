using System;

namespace KoFFPanel.Domain.Utilities;

public static class ByteFormatter
{
    private static readonly string[] Suffixes = { "B", "KB", "MB", "GB", "TB", "PB", "EB" };

    public static string Format(long bytes, string zeroText = "0 B")
    {
        if (bytes <= 0) return zeroText;

        double number = bytes;
        int counter = 0;

        while (number >= 1024.0 && counter < Suffixes.Length - 1)
        {
            number /= 1024.0;
            counter++;
        }

        return $"{number:F2} {Suffixes[counter]}";
    }
}

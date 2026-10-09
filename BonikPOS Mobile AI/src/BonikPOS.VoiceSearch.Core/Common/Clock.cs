using System.Diagnostics;

namespace BonikPOS.VoiceSearch.Core.Common;

/// <summary>Helpers for high-resolution timing based on <see cref="Stopwatch"/> timestamps.</summary>
public static class Ticks
{
    public static long Now() => Stopwatch.GetTimestamp();

    public static double ElapsedMilliseconds(long startTimestamp) =>
        Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

    public static double ToMilliseconds(long from, long to) => Stopwatch.GetElapsedTime(from, to).TotalMilliseconds;
}

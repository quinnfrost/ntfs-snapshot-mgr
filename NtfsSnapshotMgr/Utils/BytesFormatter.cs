namespace NtfsSnapshotMgr.Utils;

/// <summary>Formats byte counts into human-readable strings.</summary>
public static class BytesFormatter
{
    private static readonly string[] SizeSuffixes = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>
    /// Format <paramref name="bytes"/> as a human-readable size string,
    /// e.g. "1.50 GB", "320 MB". Returns "无限制" when <paramref name="bytes"/> is -1.
    /// </summary>
    public static string Format(long bytes)
    {
        if (bytes == -1)
            return "无限制";

        if (bytes == 0)
            return "0 B";

        int magnitude = (int)Math.Log(Math.Abs(bytes), 1024);
        magnitude = Math.Min(magnitude, SizeSuffixes.Length - 1);

        double adjusted = bytes / Math.Pow(1024, magnitude);
        return $"{adjusted:N2} {SizeSuffixes[magnitude]}";
    }
}

namespace ScrapBook.UnitTests.Decoders;

internal sealed class FfmpegTheoryAttribute : TheoryAttribute
{
    public FfmpegTheoryAttribute()
    {
        if (FfprobePath is null) Skip = "FFmpeg and FFprobe are required for the real audio fixtures.";
    }

    public static string? FfprobePath { get; } = FindFfprobe();

    private static string? FindFfprobe()
    {
        var suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return paths.Where(Path.IsPathFullyQualified)
            .Where(path => File.Exists(Path.Combine(path, "ffmpeg" + suffix)))
            .Select(path => Path.Combine(path, "ffprobe" + suffix))
            .FirstOrDefault(File.Exists);
    }
}

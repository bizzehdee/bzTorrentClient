using bzTorrentClient.Engine.Logging;
using Xunit;

namespace bzTorrentClient.Engine.Tests.Logging;

public class FileDebugLoggerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"bztorrentclient-logging-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static string[] LogFiles(string directory) =>
        Directory.GetFiles(directory, "bztorrentclient-*.log");

    [Fact]
    public void Constructor_CreatesLogDirectoryIfMissing()
    {
        _ = new FileDebugLogger(_tempDir, maxFileSizeBytes: 1024, maxAge: TimeSpan.FromDays(7));

        Assert.True(Directory.Exists(_tempDir));
    }

    [Fact]
    public void Log_WritesMessageToFile()
    {
        var logger = new FileDebugLogger(_tempDir, maxFileSizeBytes: 1024, maxAge: TimeSpan.FromDays(7));

        logger.Log("hello world");

        var file = Assert.Single(LogFiles(_tempDir));
        Assert.Contains("hello world", File.ReadAllText(file));
    }

    [Fact]
    public void Log_ExceedingMaxFileSize_StartsANewFile()
    {
        var logger = new FileDebugLogger(_tempDir, maxFileSizeBytes: 50, maxAge: TimeSpan.FromDays(7));

        // Each line is well over 50 bytes on its own, so every single write should trigger
        // a rotation - after a few writes there must be more than one file.
        for (var i = 0; i < 5; i++)
            logger.Log($"line number {i} with some padding to exceed the size limit");

        Assert.True(LogFiles(_tempDir).Length > 1, "Expected more than one log file after exceeding the size limit repeatedly.");
    }

    [Fact]
    public void Log_UnderMaxFileSize_KeepsAppendingToSameFile()
    {
        var logger = new FileDebugLogger(_tempDir, maxFileSizeBytes: 1_000_000, maxAge: TimeSpan.FromDays(7));

        for (var i = 0; i < 5; i++)
            logger.Log($"line {i}");

        Assert.Single(LogFiles(_tempDir));
    }

    [Fact]
    public void Constructor_DeletesFilesOlderThanMaxAge()
    {
        Directory.CreateDirectory(_tempDir);
        var oldFile = Path.Combine(_tempDir, "bztorrentclient-old.log");
        File.WriteAllText(oldFile, "stale");
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow - TimeSpan.FromDays(30));

        _ = new FileDebugLogger(_tempDir, maxFileSizeBytes: 1024, maxAge: TimeSpan.FromDays(7));

        Assert.False(File.Exists(oldFile));
    }

    [Fact]
    public void Constructor_KeepsFilesNewerThanMaxAge()
    {
        Directory.CreateDirectory(_tempDir);
        var recentFile = Path.Combine(_tempDir, "bztorrentclient-recent.log");
        File.WriteAllText(recentFile, "fresh");
        File.SetLastWriteTimeUtc(recentFile, DateTime.UtcNow - TimeSpan.FromDays(1));

        _ = new FileDebugLogger(_tempDir, maxFileSizeBytes: 1024, maxAge: TimeSpan.FromDays(7));

        Assert.True(File.Exists(recentFile));
    }

    [Fact]
    public void Constructor_DoesNotDeleteUnrelatedFiles()
    {
        Directory.CreateDirectory(_tempDir);
        var unrelatedFile = Path.Combine(_tempDir, "not-a-log-file.txt");
        File.WriteAllText(unrelatedFile, "keep me");
        File.SetLastWriteTimeUtc(unrelatedFile, DateTime.UtcNow - TimeSpan.FromDays(30));

        _ = new FileDebugLogger(_tempDir, maxFileSizeBytes: 1024, maxAge: TimeSpan.FromDays(7));

        Assert.True(File.Exists(unrelatedFile));
    }
}

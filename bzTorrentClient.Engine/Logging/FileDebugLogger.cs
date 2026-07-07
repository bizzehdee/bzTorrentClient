namespace bzTorrentClient.Engine.Logging;

/// <summary>
/// Appends timestamped lines to a log file in <paramref name="directory"/>, starting a
/// fresh file once the current one reaches <paramref name="maxFileSizeBytes"/> and deleting
/// any of this logger's own files older than <paramref name="maxAge"/> (checked on
/// construction and on every rotation, not on a background timer - this is debug logging,
/// not something that needs to prune the instant a file ages out).
/// </summary>
public sealed class FileDebugLogger : IDebugLogger
{
    private const string FilePrefix = "bztorrentclient-";
    private const string FileExtension = ".log";

    private readonly string _directory;
    private readonly long _maxFileSizeBytes;
    private readonly TimeSpan _maxAge;
    private readonly Func<DateTime> _utcNow;
    private readonly object _lock = new();

    private string _currentFilePath;

    public FileDebugLogger(string directory, long maxFileSizeBytes, TimeSpan maxAge, Func<DateTime>? utcNow = null)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Log directory must not be empty.", nameof(directory));
        if (maxFileSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxFileSizeBytes), "Max file size must be positive.");

        _directory = directory;
        _maxFileSizeBytes = maxFileSizeBytes;
        _maxAge = maxAge;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);

        Directory.CreateDirectory(_directory);
        PruneOldFiles();
        _currentFilePath = NewFilePath();
    }

    public void Log(string message)
    {
        var line = $"{_utcNow():O} {message}{Environment.NewLine}";

        lock (_lock)
        {
            File.AppendAllText(_currentFilePath, line);

            if (new FileInfo(_currentFilePath).Length >= _maxFileSizeBytes)
            {
                PruneOldFiles();
                _currentFilePath = NewFilePath();
            }
        }
    }

    private string NewFilePath() =>
        Path.Combine(_directory, $"{FilePrefix}{_utcNow():yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}{FileExtension}");

    private void PruneOldFiles()
    {
        var cutoff = _utcNow() - _maxAge;

        foreach (var file in Directory.EnumerateFiles(_directory, $"{FilePrefix}*{FileExtension}"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
            catch (IOException)
            {
                // In use or already gone - leave it for the next prune.
            }
        }
    }
}

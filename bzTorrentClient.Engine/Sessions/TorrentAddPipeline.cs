using bzTorrent.Data;

namespace bzTorrentClient.Engine.Sessions;

/// <summary>
/// The three user-facing ways to add a torrent. Magnet/info-hash adds land with a stub
/// <see cref="IMetadata"/> (hash only, no pieces) — full metadata is fetched lazily over
/// DHT/BEP-9 once the session is started (see <c>NetworkedSessionManager</c>), matching
/// "start while paused" leaving a torrent legitimately without piece data until it runs.
/// </summary>
public sealed class TorrentAddPipeline
{
    private readonly ISessionManager _sessionManager;

    public TorrentAddPipeline(ISessionManager sessionManager)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
    }

    public async Task<TorrentSession> AddFromFileAsync(
        string torrentFilePath,
        string? downloadDirectory,
        bool startImmediately,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(torrentFilePath))
            throw new ArgumentException("Torrent file path must not be empty.", nameof(torrentFilePath));

        if (!File.Exists(torrentFilePath))
            throw new FileNotFoundException("Torrent file not found.", torrentFilePath);

        var bytes = await File.ReadAllBytesAsync(torrentFilePath, cancellationToken);
        var source = new TorrentAddSource.TorrentFile(bytes);
        return await _sessionManager.AddAsync(source, downloadDirectory, startImmediately, cancellationToken);
    }

    public Task<TorrentSession> AddFromMagnetAsync(
        string magnetUri,
        string? downloadDirectory,
        bool startImmediately,
        CancellationToken cancellationToken = default)
    {
        if (!MagnetLink.IsMagnetLink(magnetUri))
            throw new ArgumentException("Not a valid magnet URI.", nameof(magnetUri));

        var source = new TorrentAddSource.Magnet(magnetUri);
        return _sessionManager.AddAsync(source, downloadDirectory, startImmediately, cancellationToken);
    }

    public Task<TorrentSession> AddFromInfoHashAsync(
        string infoHashHex,
        string? downloadDirectory,
        bool startImmediately,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidInfoHash(infoHashHex))
            throw new ArgumentException("Info-hash must be exactly 40 hex characters.", nameof(infoHashHex));

        var source = TorrentAddSource.Magnet.FromInfoHash(infoHashHex);
        return _sessionManager.AddAsync(source, downloadDirectory, startImmediately, cancellationToken);
    }

    private static bool IsValidInfoHash(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length == 40 && value.All(Uri.IsHexDigit);
}

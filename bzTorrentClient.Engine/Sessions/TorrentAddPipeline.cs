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
        AddTorrentState state = AddTorrentState.Paused,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(torrentFilePath))
            throw new ArgumentException("Torrent file path must not be empty.", nameof(torrentFilePath));

        if (!File.Exists(torrentFilePath))
            throw new FileNotFoundException("Torrent file not found.", torrentFilePath);

        var bytes = await File.ReadAllBytesAsync(torrentFilePath, cancellationToken);
        var source = new TorrentAddSource.TorrentFile(bytes);
        return await AddAsync(source, downloadDirectory, state, cancellationToken);
    }

    public Task<TorrentSession> AddFromMagnetAsync(
        string magnetUri,
        string? downloadDirectory,
        AddTorrentState state = AddTorrentState.Paused,
        CancellationToken cancellationToken = default)
    {
        if (!MagnetLink.IsMagnetLink(magnetUri))
            throw new ArgumentException("Not a valid magnet URI.", nameof(magnetUri));

        var source = new TorrentAddSource.Magnet(magnetUri);
        return AddAsync(source, downloadDirectory, state, cancellationToken);
    }

    public Task<TorrentSession> AddFromInfoHashAsync(
        string infoHashHex,
        string? downloadDirectory,
        AddTorrentState state = AddTorrentState.Paused,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidInfoHash(infoHashHex))
            throw new ArgumentException("Info-hash must be exactly 40 hex characters.", nameof(infoHashHex));

        var source = TorrentAddSource.Magnet.FromInfoHash(infoHashHex);
        return AddAsync(source, downloadDirectory, state, cancellationToken);
    }

    private async Task<TorrentSession> AddAsync(
        TorrentAddSource source,
        string? downloadDirectory,
        AddTorrentState state,
        CancellationToken cancellationToken)
    {
        var session = await _sessionManager.AddAsync(source, downloadDirectory, startImmediately: state == AddTorrentState.Started, cancellationToken);

        // AddAsync always lands a new session Paused unless started immediately - Stopped
        // needs an explicit extra step since it isn't a state AddAsync itself knows about.
        if (state == AddTorrentState.Stopped)
            await _sessionManager.StopAsync(session.Id, cancellationToken);

        return session;
    }

    private static bool IsValidInfoHash(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length == 40 && value.All(Uri.IsHexDigit);
}

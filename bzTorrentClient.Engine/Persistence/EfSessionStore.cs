using Microsoft.EntityFrameworkCore;
using bzTorrentClient.Engine.Sessions;

namespace bzTorrentClient.Engine.Persistence;

public sealed class EfSessionStore : ISessionStore
{
    private readonly DbContextOptions<BzTorrentClientDbContext> _options;

    public EfSessionStore(DbContextOptions<BzTorrentClientDbContext> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<IReadOnlyList<TorrentSession>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = new BzTorrentClientDbContext(_options);
        var entities = await db.TorrentSessions.AsNoTracking().ToListAsync(cancellationToken);

        var sessions = new List<TorrentSession>(entities.Count);
        foreach (var entity in entities)
        {
            var source = ToSource(entity);
            var metadata = source.ResolveMetadata();
            var pieceCompletion = UnpackBits(entity.PieceCompletion, entity.PieceCount);

            sessions.Add(new TorrentSession(
                entity.Id,
                source,
                metadata,
                entity.DownloadDirectory,
                entity.State,
                entity.AddedAtUtc,
                pieceCompletion));
        }

        return sessions;
    }

    public async Task SaveAsync(TorrentSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        await using var db = new BzTorrentClientDbContext(_options);
        var entity = await db.TorrentSessions.FirstOrDefaultAsync(e => e.Id == session.Id, cancellationToken);

        if (entity is null)
        {
            entity = new TorrentSessionEntity { Id = session.Id };
            db.TorrentSessions.Add(entity);
        }

        entity.InfoHash = session.Metadata.HashString;
        entity.Name = session.Metadata.Name ?? string.Empty;
        entity.DownloadDirectory = session.DownloadDirectory;
        entity.State = session.State;
        entity.PieceCount = session.PieceCompletion.Length;
        entity.PieceCompletion = PackBits(session.PieceCompletion);
        entity.AddedAtUtc = session.AddedAtUtc;

        switch (session.Source)
        {
            case TorrentAddSource.TorrentFile file:
                entity.TorrentFileBytes = file.Bytes;
                entity.MagnetUri = null;
                break;
            case TorrentAddSource.Magnet magnet:
                entity.MagnetUri = magnet.Uri;
                entity.TorrentFileBytes = null;
                break;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var db = new BzTorrentClientDbContext(_options);
        var entity = await db.TorrentSessions.FirstOrDefaultAsync(e => e.Id == sessionId, cancellationToken);
        if (entity is null)
            return;

        db.TorrentSessions.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static TorrentAddSource ToSource(TorrentSessionEntity entity)
    {
        if (entity.TorrentFileBytes is { Length: > 0 })
            return new TorrentAddSource.TorrentFile(entity.TorrentFileBytes);

        if (!string.IsNullOrEmpty(entity.MagnetUri))
            return new TorrentAddSource.Magnet(entity.MagnetUri);

        throw new InvalidOperationException(
            $"Torrent session {entity.Id} has no recoverable source (neither torrent bytes nor a magnet URI).");
    }

    private static byte[] PackBits(bool[] bits)
    {
        var bytes = new byte[(bits.Length + 7) / 8];
        for (var i = 0; i < bits.Length; i++)
        {
            if (bits[i])
                bytes[i / 8] |= (byte)(0x80 >> (i % 8));
        }

        return bytes;
    }

    private static bool[] UnpackBits(byte[] bytes, int bitCount)
    {
        var bits = new bool[bitCount];
        for (var i = 0; i < bitCount; i++)
        {
            bits[i] = (bytes[i / 8] & (0x80 >> (i % 8))) != 0;
        }

        return bits;
    }
}

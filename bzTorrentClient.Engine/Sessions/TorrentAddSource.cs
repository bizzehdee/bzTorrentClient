using bzTorrent.Data;

namespace bzTorrentClient.Engine.Sessions;

/// <summary>
/// How a torrent was added, and thus how its <see cref="IMetadata"/> can be
/// re-resolved after a restart without needing bzTorrent to serialize
/// <see cref="IMetadata"/> itself.
/// </summary>
public abstract class TorrentAddSource
{
    private TorrentAddSource()
    {
    }

    public IMetadata ResolveMetadata() => this switch
    {
        TorrentFile file => Metadata.FromBuffer(file.Bytes),
        Magnet magnet => MagnetLink.ResolveToMetadata(magnet.Uri),
        _ => throw new InvalidOperationException("Unknown torrent add source.")
    };

    public sealed class TorrentFile : TorrentAddSource
    {
        public byte[] Bytes { get; }

        public TorrentFile(byte[] bytes)
        {
            Bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
        }
    }

    /// <summary>
    /// Covers both magnet links and raw info-hash adds — a raw info-hash is
    /// just a magnet URI with only <c>xt=urn:btih:...</c> and no trackers,
    /// which is exactly what a trackerless DHT-only fetch needs.
    /// </summary>
    public sealed class Magnet : TorrentAddSource
    {
        public string Uri { get; }

        public Magnet(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri))
                throw new ArgumentException("Magnet URI must not be empty.", nameof(uri));

            Uri = uri;
        }

        public static Magnet FromInfoHash(string infoHashHex)
        {
            if (string.IsNullOrWhiteSpace(infoHashHex))
                throw new ArgumentException("Info-hash must not be empty.", nameof(infoHashHex));

            return new Magnet($"magnet:?xt=urn:btih:{infoHashHex}");
        }
    }
}

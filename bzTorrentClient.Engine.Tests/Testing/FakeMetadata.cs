using bzTorrent.Data;

namespace bzTorrentClient.Engine.Tests.Testing;

/// <summary>Minimal in-memory <see cref="IMetadata"/> with controllable pieces/files, for tests that don't need real bencoded data.</summary>
internal sealed class FakeMetadata : IMetadata
{
    private readonly IReadOnlyList<MetadataFileInfo> _files;

    public FakeMetadata(
        int pieceCount,
        string hashHex = "0123456789abcdef0123456789abcdef01234567",
        long pieceSize = 16384,
        IReadOnlyList<byte[]>? pieceHashes = null,
        IReadOnlyList<MetadataFileInfo>? files = null)
    {
        PieceHashes = pieceHashes?.ToList<byte[]>() ?? Enumerable.Range(0, pieceCount).Select(_ => new byte[20]).ToList<byte[]>();
        PieceSize = pieceSize;
        Hash = InfoHash.FromHex(hashHex);
        _files = files ?? Array.Empty<MetadataFileInfo>();
    }

    public InfoHash Hash { get; }
    public string HashString => Hash.Hex;
    public string Name { get; set; } = "fake-torrent";
    public string Announce => string.Empty;
    public ICollection<string> AnnounceList { get; } = new List<string>();
    public string Comment => string.Empty;
    public string CreatedBy => string.Empty;
    public DateTime CreationDate => DateTime.UnixEpoch;
    public ICollection<byte[]> PieceHashes { get; }
    public long PieceSize { get; }
    public bool Private { get; init; }
    public ICollection<string> WebSeeds { get; } = new List<string>();
    public bool IsMultiFile => _files.Count > 1;

    public IReadOnlyCollection<string> GetFiles() => _files.Select(f => f.Filename).ToList();
    public IReadOnlyCollection<MetadataFileInfo> GetFileInfos() => _files.ToList();
    public bool Load(MagnetLink magnetLink) => false;
    public bool Load(Stream stream) => false;
    public void Save(Stream stream)
    {
    }

    public void SaveToFile(string filename)
    {
    }
}

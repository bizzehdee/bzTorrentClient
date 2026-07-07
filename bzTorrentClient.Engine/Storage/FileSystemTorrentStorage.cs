using bzTorrent.Data;

namespace bzTorrentClient.Engine.Storage;

/// <summary>Maps piece/block offsets onto a torrent's file(s) on disk, following the same layout math as bzTorrent's own Demo client.</summary>
public sealed class FileSystemTorrentStorage : ITorrentStorage
{
    private readonly IMetadata _metadata;
    private readonly string _downloadDirectory;
    private readonly long _totalLength;

    public FileSystemTorrentStorage(IMetadata metadata, string downloadDirectory)
    {
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));

        if (string.IsNullOrWhiteSpace(downloadDirectory))
            throw new ArgumentException("Download directory must not be empty.", nameof(downloadDirectory));

        _downloadDirectory = downloadDirectory;
        _totalLength = _metadata.GetFileInfos().Sum(f => f.FileSize);
    }

    public long GetPieceLength(int pieceIndex)
    {
        if (pieceIndex == _metadata.PieceHashes.Count - 1)
        {
            var remainder = _totalLength % _metadata.PieceSize;
            return remainder == 0 ? _metadata.PieceSize : remainder;
        }

        return _metadata.PieceSize;
    }

    public void EnsureAllocated()
    {
        foreach (var file in _metadata.GetFileInfos())
        {
            var fullPath = Path.GetFullPath(file.Filename, _downloadDirectory);
            var directory = Path.GetDirectoryName(fullPath);

            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            if (!File.Exists(fullPath))
            {
                using var stream = File.Create(fullPath);
                stream.SetLength(file.FileSize);
            }
        }
    }

    /// <summary>
    /// Deletes every file this torrent owns, following the same path layout
    /// <see cref="EnsureAllocated"/> creates them with. Any subdirectory left empty
    /// afterward (e.g. a multi-file torrent's own folder) is removed too, but only up to
    /// - never including - <paramref name="downloadDirectory"/> itself, and only while
    /// each directory is actually empty, so files unrelated to this torrent are left alone.
    /// </summary>
    public static void DeleteFiles(IMetadata metadata, string downloadDirectory)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (string.IsNullOrWhiteSpace(downloadDirectory))
            throw new ArgumentException("Download directory must not be empty.", nameof(downloadDirectory));

        var root = Path.GetFullPath(downloadDirectory);

        foreach (var file in metadata.GetFileInfos())
        {
            var fullPath = Path.GetFullPath(file.Filename, root);
            if (File.Exists(fullPath))
                File.Delete(fullPath);

            var directory = Path.GetDirectoryName(fullPath);
            while (!string.IsNullOrEmpty(directory)
                && !string.Equals(directory, root, StringComparison.Ordinal)
                && Directory.Exists(directory)
                && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
                directory = Path.GetDirectoryName(directory);
            }
        }
    }

    public void WriteBlock(int pieceIndex, int blockOffset, byte[] data)
    {
        var absoluteStart = (long)pieceIndex * _metadata.PieceSize + blockOffset;
        var absoluteEnd = absoluteStart + data.Length;

        foreach (var file in _metadata.GetFileInfos())
        {
            var fileEnd = file.FileStartByte + file.FileSize;
            if (absoluteEnd <= file.FileStartByte || absoluteStart >= fileEnd)
                continue;

            var fileOffset = Math.Max(absoluteStart - file.FileStartByte, 0);
            var dataOffset = (int)Math.Max(file.FileStartByte - absoluteStart, 0);
            var writeLength = (int)Math.Min(data.Length - dataOffset, file.FileSize - fileOffset);

            if (writeLength <= 0)
                continue;

            var fullPath = Path.GetFullPath(file.Filename, _downloadDirectory);
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            stream.Seek(fileOffset, SeekOrigin.Begin);
            stream.Write(data, dataOffset, writeLength);
        }
    }

    public byte[] ReadPiece(int pieceIndex)
    {
        var length = GetPieceLength(pieceIndex);
        var buffer = new byte[length];
        var absoluteStart = (long)pieceIndex * _metadata.PieceSize;
        var absoluteEnd = absoluteStart + length;

        foreach (var file in _metadata.GetFileInfos())
        {
            var fileEnd = file.FileStartByte + file.FileSize;
            if (absoluteEnd <= file.FileStartByte || absoluteStart >= fileEnd)
                continue;

            var fileOffset = Math.Max(absoluteStart - file.FileStartByte, 0);
            var bufferOffset = (int)Math.Max(file.FileStartByte - absoluteStart, 0);
            var readLength = (int)Math.Min(length - bufferOffset, file.FileSize - fileOffset);

            if (readLength <= 0)
                continue;

            var fullPath = Path.GetFullPath(file.Filename, _downloadDirectory);
            if (!File.Exists(fullPath))
                continue;

            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(fileOffset, SeekOrigin.Begin);
            var totalRead = 0;
            while (totalRead < readLength)
            {
                var read = stream.Read(buffer, bufferOffset + totalRead, readLength - totalRead);
                if (read == 0)
                    break;
                totalRead += read;
            }
        }

        return buffer;
    }
}

using System.Net;
using bzTorrentClient.Engine.Networking;

namespace bzTorrentClient.Engine.Tests.Networking;

internal sealed class FakePeerConnectionManager : IPeerConnectionManager
{
    public List<IPEndPoint> Candidates { get; } = new();
    public bool Started { get; private set; }
    public bool Paused { get; private set; }
    public bool Stopped { get; private set; }
    public bool Disposed { get; private set; }

    public int ActiveConnectionCount => Candidates.Count;

    public IReadOnlyCollection<IPEndPoint> ConnectedEndpoints => Candidates;

    public int PexPeersFound { get; set; }
    public long BytesDownloaded { get; set; }
    public long BytesUploaded { get; set; }

    public void AddPeerCandidate(IPEndPoint endpoint) => Candidates.Add(endpoint);
    public void Start() => Started = true;
    public void Pause() => Paused = true;
    public void Stop() => Stopped = true;
    public void Dispose() => Disposed = true;
}

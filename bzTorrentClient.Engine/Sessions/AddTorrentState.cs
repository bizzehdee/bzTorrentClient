namespace bzTorrentClient.Engine.Sessions;

/// <summary>The state a newly added torrent should land in.</summary>
public enum AddTorrentState
{
    Paused,
    Started,
    Stopped,
}

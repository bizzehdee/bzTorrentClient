namespace bzTorrentClient.Engine.Sessions;

public enum TorrentState
{
    Paused,
    Active,
    Stopped,
    Checking,
    Error,

    /// <summary>Fully verified, but not currently running - e.g. verified while Paused/Stopped. Distinct from <see cref="Seeding"/>, which is the running equivalent.</summary>
    Completed,

    /// <summary>Fully verified and currently running - accepting connections and serving pieces. What a torrent that finishes while <see cref="Active"/> transitions to.</summary>
    Seeding
}

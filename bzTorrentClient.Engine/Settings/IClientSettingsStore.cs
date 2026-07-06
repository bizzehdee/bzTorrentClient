namespace bzTorrentClient.Engine.Settings;

public interface IClientSettingsStore
{
    IClientSettings Load();

    void Save(IClientSettings settings);
}

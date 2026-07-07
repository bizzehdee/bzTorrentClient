using System.Net;
using bzTorrentClient.Avalonia.Shell;
using bzTorrentClient.Avalonia.Tests.Testing;
using bzTorrentClient.Engine.Networking;
using bzTorrentClient.Engine.Sessions;
using bzTorrentClient.Engine.Settings;

namespace bzTorrentClient.Avalonia.Tests.Shell;

/// <summary>A session manager whose slow ResumeAsync phase is controllable, so tests can prove the UI doesn't wait on it.</summary>
file sealed class FakeTwoPhaseSessionManager : FakeSessionManager, ITwoPhaseSessionInitializer
{
    private readonly TaskCompletionSource _resumeGate = new();

    public bool LoadCalled { get; private set; }
    public bool ResumeCalled { get; private set; }

    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        LoadCalled = true;
        return Task.CompletedTask;
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        ResumeCalled = true;
        await _resumeGate.Task;
    }

    public void ReleaseResume() => _resumeGate.TrySetResult();
}

file sealed class FakeClientSettingsStore : IClientSettingsStore
{
    public IClientSettings Load() => new ClientSettings("/downloads");

    public void Save(IClientSettings settings)
    {
    }
}

public class MainWindowViewModelTests
{
    private static TorrentAddSource.Magnet Source(string hashHex = "0123456789abcdef0123456789abcdef01234567") =>
        TorrentAddSource.Magnet.FromInfoHash(hashHex);

    [Fact]
    public async Task InitializeAsync_ShowsListBeforeSlowResumePhaseCompletes()
    {
        // Regression test: the torrent list used to stay empty until the whole slow
        // startup chain (tracker-list refresh, per-torrent disk verification,
        // auto-resuming) finished, which could take several seconds. LoadAsync (a DB
        // read only) must be enough to populate and show the list; ResumeAsync's slower
        // work must not block that.
        var sessionManager = new FakeTwoPhaseSessionManager();
        await sessionManager.AddAsync(Source(), "/downloads", false);

        var settings = new ClientSettings("/downloads");
        var viewModel = new MainWindowViewModel(sessionManager, new TorrentAddPipeline(sessionManager), settings, new FakeClientSettingsStore());

        var initializeTask = viewModel.InitializeAsync();

        // Give InitializeAsync a chance to reach the (still-blocked) ResumeAsync call.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!sessionManager.ResumeCalled && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.True(sessionManager.LoadCalled);
        Assert.True(sessionManager.ResumeCalled);
        Assert.Single(viewModel.TorrentList.Torrents);

        sessionManager.ReleaseResume();
        await initializeTask;
    }
}

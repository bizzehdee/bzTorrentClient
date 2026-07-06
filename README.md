# bzTorrentClient

A desktop BitTorrent client for Windows and Linux, built with
[Avalonia](https://avaloniaui.net/) on top of the
[bzTorrent](https://github.com/bizzehdee/bzTorrent) protocol library, included
here as a git submodule.

See [`PLAN.md`](PLAN.md) for the full feature scope and [`TASK.md`](TASK.md)
for what's been built so far.

## What it does

- Manages multiple torrents at once, each with several peer connections.
- Adds torrents by `.torrent` file, magnet link, or info-hash.
- Discovers peers via trackers, DHT, PEX (BEP-11), and LAN (LPD).
- Lets you choose a download directory per torrent.
- Starts a torrent paused, then pauses / stops / resumes it at will.
- Remembers your torrents and their progress between runs.

## Projects

| Project | Purpose |
|---|---|
| `bzTorrent` (submodule) | The underlying BitTorrent protocol library. |
| `bzTorrentClient.Engine` | Torrent session engine: piece selection, disk I/O, peer connection management, persistence. No UI dependency. |
| `bzTorrentClient.Engine.Tests` | xUnit + Moq tests for the engine. |
| `bzTorrentClient.Avalonia` | The desktop application (Avalonia + MVVM). |
| `bzTorrentClient.Avalonia.Tests` | Avalonia.Headless.XUnit tests for views/view models. |

## Getting the code

This repo uses `bzTorrent` as a submodule — clone with `--recurse-submodules`,
or run `git submodule update --init` after a plain clone.

## Building and running

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet build bzTorrentClient.Avalonia
dotnet run --project bzTorrentClient.Avalonia
```

Runs on both Windows and Linux — Avalonia handles the platform-specific UI
plumbing.

## Running tests

```
dotnet test bzTorrentClient.Engine.Tests
dotnet test bzTorrentClient.Avalonia.Tests
```

## License

Licensed under the [GNU General Public License v3.0](LICENSE).

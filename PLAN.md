# bzTorrentClient — PLAN.md

## Goal

A cross-platform (Windows, Linux) desktop BitTorrent client built on the
`bzTorrent` library, with an Avalonia UI. `bzTorrent` provides protocol
plumbing only (tracker, DHT, peer-wire, metadata, encryption) — it has no
concept of a "torrent session", piece scheduling, disk I/O, or persistence.
This project supplies that missing engine layer plus the UI on top of it.

## Explicit requirements (from user)

- Standard BitTorrent client feature set.
- Multiple torrents active at once.
- Multiple peer connections per torrent.
- User-selectable download directory (per torrent).
- Torrent lifecycle: add **start paused**, **pause** an active torrent,
  **stop**, **start**.
- Add a torrent by: `.torrent` file, magnet link, or raw info-hash
  ("torrent id" — hex/base32 SHA1, trackerless DHT-only fetch, no external ID
  service).
- UI: Avalonia, running on Windows and Linux.

## Confirmed decisions

- **Location**: new projects added to this repo/solution (`bzTorrent.sln`),
  not a separate repo.
- **Pause vs Stop are functionally distinct**:
  - **Paused**: torrent stays "loaded" — metadata, piece bitfield, and known
    peer list stay cached in memory; no data is transferred; no active peer
    sockets. Resuming from Paused is a fast in-memory restart (reconnect to
    already-known peers, no fresh tracker/DHT announce required).
  - **Stopped**: full teardown — all peer connections closed, tracker/DHT
    session released, torrent removed from the engine's active scheduling
    loop. Resuming from Stopped re-announces to trackers/DHT from scratch,
    as if freshly added (though piece bitfield/progress on disk is retained).
  - "Start while paused" on add means: register the torrent, verify/load any
    existing on-disk data, land in the **Paused** state — do not connect to
    peers or announce until the user presses Start.
- **Session persistence**: full resume across app restarts. On relaunch,
  all torrents reappear with their prior state (Paused/Active/Stopped),
  progress, and piece bitmap, and continue without re-hashing every piece
  (bitfield is trusted; a manual "Force re-check" is a Phase 2 nicety, not
  MVP).
- **MVP scope**: rarest-first piece selection, all files in a torrent
  downloaded (no per-file selection), no upload/download speed limits, seed
  indefinitely after completion. **Deferred to Phase 2** (see below).
- **CI**: GitHub Actions build+test pipeline, matrix over `ubuntu-latest`
  and `windows-latest`. Implemented as
  `.github/workflows/bztorrentclient-ci.yml`, scoped to changes under
  `bzTorrentClient/`.
- **UI testing**: `Avalonia.Headless.XUnit` (not Playwright — Playwright
  cannot drive a native desktop app).
- **Session persistence**: EF Core + SQLite, per the global DB stack
  preference, behind a repository/abstraction layer (`ISessionStore`) so
  the provider can be swapped later if ever needed. This is heavier than
  the data strictly requires (a single-user embedded state blob), but the
  user chose consistency with the standing stack preference over a
  bespoke JSON store.
- **Default listen port**: 6881, with a 6881–6889 fallback range if in
  use; configurable in Settings.

## Explicitly deferred to Phase 2 (not in MVP scope)

Flagging these now so they're tracked, not silently dropped:

- Per-file selection / priority within a multi-file torrent.
- Sequential (as opposed to rarest-first) download mode.
- Global and per-torrent upload/download speed limits.
- Seed ratio / seed time limits, auto-stop-seeding.
- Queueing (max simultaneously *active* torrents, with the rest queued).
- Manual "force re-check" / re-hash of an existing torrent's files.
- Removing a torrent with the option to also delete its downloaded files.
- Import/export of client settings.

A request for any of these during MVP implementation is a scope change per
the standing project-setup rules — it goes into this PLAN.md's Phase 2
section and a corresponding TASK.md entry, not straight into the current
task.

## Resolved items (formerly open)

All open items from the initial scoping pass have been decided (see
"Confirmed decisions" above): Avalonia.Headless.XUnit for UI tests, EF Core
+ SQLite behind `ISessionStore` for persistence, 6881 as the default
listen port.

**Authentication**: not applicable — single-user local desktop app, no
JWT/OpenIddict needed. Noted so it's not mistaken for an oversight.

## Architecture

`bzTorrentClient` is its own repository, separate from the `bzTorrent`
library's repository. The library is included here as a **git submodule**
at `bzTorrent/`, so the client always builds against the library's source
directly (no NuGet package):

```
bzTorrentClient/                       (this repo)
├── bzTorrentClient.sln                (references bzTorrent/bzTorrent/bzTorrent.csproj + the 4 projects below)
├── bzTorrent/                         (git submodule — the bzTorrent library repo)
├── bzTorrentClient.Engine/            (net10.0 class library)
├── bzTorrentClient.Engine.Tests/      (xUnit + Moq)
├── bzTorrentClient.Avalonia/          (Avalonia desktop app, net10.0)
└── bzTorrentClient.Avalonia.Tests/    (Avalonia.Headless.XUnit)
```

**Build direction is one-way**: `bzTorrentClient.sln` references
`bzTorrent/bzTorrent/bzTorrent.csproj` from the submodule (so the client
always builds against the library), but the library's own `bzTorrent.sln`
(inside the submodule) does not know the client exists — building/testing/
packing the library never pulls in the client. This keeps the library's
release pipeline unaffected by the client's presence, while the client's
CI naturally exercises the library it depends on. Bumping to a newer
library version is a normal submodule update (`git submodule update
--remote`), not a repo restructuring.

`bzTorrentClient.Engine` depends on `bzTorrent` only — no Avalonia
reference, so it's independently testable and reusable (e.g. by a future
CLI or headless daemon, though that's not in scope now — just a natural
consequence of not entangling engine and UI, not a speculative feature).

### Engine (`bzTorrentClient.Engine`)

Core abstractions (interfaces first, per repo convention of segregated,
composable interfaces):

- **`TorrentSession`** — one added torrent: `IMetadata`, current
  `TorrentState` (`Paused` / `Active` / `Stopped` / `Checking` / `Error` /
  `Completed`), progress, piece bitfield, download directory, peer list.
- **`ISessionManager`** — owns the collection of `TorrentSession`s;
  add/remove/start/pause/stop; enforces the global max-connections budget
  across all torrents.
- **`IPieceManager`** (per torrent) — rarest-first piece/block selection
  from peer bitfields; verifies a completed piece's SHA1 against
  `metadata.PieceHashes` before marking it done.
- **`ITorrentStorage`** — maps piece/block offsets to the torrent's file(s)
  (via `metadata.GetFileInfos()`), reads for verification, writes incoming
  blocks; one implementation backed by the local filesystem.
- **`IPeerConnectionManager`** (per torrent) — owns up to N
  `IPeerWireClient` connections (bzTorrent), performs basic choke/unchoke,
  issues block requests from `IPieceManager`, hands off received blocks to
  `ITorrentStorage`.
- **`IPeerSource`** — aggregates HTTP/UDP tracker announces
  (`ITrackerClient`), DHT (`DHTClient`), and LAN discovery
  (`ILocalPeerDiscovery`) into a single peer feed per torrent.
- **Add pipeline** — three entry points converging on a `TorrentSession`:
  - From `.torrent` file → `Metadata.FromFile`.
  - From magnet link → `MagnetLink.ResolveToMetadata`.
  - From raw info-hash → construct a minimal `IMetadata` with only the
    hash, then resolve full metadata trackerlessly over DHT via the
    `UTMetadata` extension once a peer is found (BEP-9).
- **`ISessionStore`** — persists/reloads the full session list (state,
  progress, bitfield, download dir) across app restarts. Backed by EF Core
  + SQLite; the interface exists so the provider is swappable per the
  global DB stack convention, even though only SQLite is realistically
  needed for a single-user desktop app.
- **`IClientSettings`** — default download directory, global max
  connections, max connections per torrent, listen port.

### UI (`bzTorrentClient.Avalonia`)

MVVM, organized by feature (vertical slices), not by technical layer:

```
Features/
├── TorrentList/     (main grid: name, status, progress, speed, peers)
├── AddTorrent/       (file picker / magnet paste / info-hash paste, download dir, "start paused" toggle)
├── TorrentDetails/    (peers / files / trackers tabs for the selected torrent)
└── Settings/          (download dir default, connection limits, listen port)
Shell/                 (MainWindow, navigation)
```

The UI project depends only on `bzTorrentClient.Engine`'s interfaces
(`ISessionManager`, `TorrentSession`, `IClientSettings`) — no direct
`bzTorrent` reference, keeping protocol details out of the view layer.

## Testing strategy

- `bzTorrentClient.Engine.Tests`: xUnit + Moq, per repo memory —
  **no real network in unit tests**; fake `ISocket`/`ITrackerClient`/DHT
  responders (matching the existing `bzTorrent.Tests` convention). Target
  90% coverage excluding DI/composition-root wiring.
- `bzTorrentClient.Avalonia.Tests`: `Avalonia.Headless.XUnit` for
  view/view-model smoke tests.
- Integration tests: exercise the full add → verify → download pipeline
  against loopback fake peers (reuse the pattern already used for
  `PeerWireListener`/`PeerWireClient` in `bzTorrent.Tests`), not real
  trackers/bootstrap nodes.

## CI

New GitHub Actions workflow (`.github/workflows/bztorrentclient-ci.yml`),
separate from the library's existing `release.yml`: matrix build+test on
`ubuntu-latest` and `windows-latest`, building `bzTorrentClient.sln`
(which includes the library) and running both test projects. Triggered on
changes under either `bzTorrentClient/**` or `bzTorrent/**`, so a library
change that breaks the client is caught even though it doesn't touch
`bzTorrent.sln`'s own release pipeline.

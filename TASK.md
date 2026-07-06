# bzTorrentClient — TASK.md

Scope, ordering, and dependencies as defined in `PLAN.md`. Implementation on
any task only begins when explicitly requested by ID or name.

## Byte-size unit formatting (user-requested)

- `ByteFormat.Bytes` used to step up a unit as soon as the value hit 1024
  (e.g. 1200 bytes → "1.2 KB") — technically correct but leaves a lot of
  values as an awkward near-1.0 fraction of the next unit. Changed the
  step-up threshold to 10× the next unit (10240, not 1024): a value only
  moves to the next unit once it would read as ten or more of it, so e.g.
  9000 bytes stays "9000 B" but 38690 bytes becomes "37.8 KB". Made
  `ByteFormat` public (was `internal`) so it's directly testable.
- Tests: `ByteFormatTests` (below/at/above the threshold, multi-unit
  step-up, top-unit ceiling, `Rate`'s `/s` suffix).

## Configurable global download/upload speed limits (user-requested)

- New `IRateLimiter` (`TokenBucketRateLimiter`) — a classic token bucket,
  bytes refill continuously up to a one-second burst cap. Reads its limit
  from a `Func<long>` on every call rather than capturing it once, so a
  live Settings-dialog change takes effect immediately without rebuilding
  anything. Zero or less means unlimited. The bucket lazily starts full
  (not empty) so a freshly started/idle limiter can serve an immediate
  burst instead of stalling the very first request.
- `IClientSettings` gained `GlobalDownloadLimitBytesPerSecond` /
  `GlobalUploadLimitBytesPerSecond` (default 0 = unlimited), persisted by
  `JsonClientSettingsStore`. Unlike the other numeric settings, 0 here is a
  meaningful value ("unlimited"), not a "use the default instead" sentinel
  — load logic only clamps genuinely invalid negatives, it doesn't
  substitute a non-zero default the way `GlobalMaxConnections` etc. do.
- `NetworkedSessionManager` constructs exactly one download and one upload
  `TokenBucketRateLimiter` (backed by the shared `IClientSettings`
  instance) and passes both to every `PeerConnectionManager` it creates —
  the limit is a true global cap across all torrents, not per-torrent.
- `PeerConnectionManager` enforces the caps at two points:
  - **Download**: before calling `TryGetNextRequest`, not after — that
    call marks whatever block it returns as requested, so checking the
    limiter afterward and declining would strand that block (never
    re-offered, silent stall). Budget is checked against
    `TypicalBlockSize` (16 KiB, the standard BEP-3 block size) since the
    real length isn't known until after the call.
  - **Upload**: in the `Request` handler, before reading/serving the
    block. Safe to check after the fact here (no persistent state) — over
    budget just drops the request; the peer will re-request or move on.
- Settings dialog: `DOWNLOAD LIMIT (KB/S, 0 = UNLIMITED)` /
  `UPLOAD LIMIT (KB/S, 0 = UNLIMITED)` fields, stored as KB/s in the UI and
  converted to/from bytes/second when loading/saving.
- Tests: `TokenBucketRateLimiterTests` (unit-level: unlimited, budget
  enforcement, refill over time, live limit changes);
  `PeerConnectionManagerIntegrationTests.PeerConnectionManager_DownloadLimiter_PacesRequests`
  (real loopback TCP transfer, proves throttling actually slows a
  multi-block download, not just the limiter in isolation);
  `ClientSettingsTests`/`JsonClientSettingsStoreTests` (defaults, 0
  round-trips as 0); `SettingsViewModelTests` (KB/s ⇄ bytes/sec
  conversion, negative-value validation).

## Auto-resume, tracker back-off, and metadata retry (user-requested)

- **Auto-resume on restart**: `NetworkedSessionManager.InitializeAsync` now
  resumes every persisted session whose `State == Active` by calling
  `StartAsync` on it after loading — previously `InitializeAsync` only
  loaded session records into memory, so an Active torrent sat inert until
  the user clicked Start again. One session failing to resume (bad tracker,
  disk error, ...) is caught and turns that session into `Error` rather
  than blocking the rest from starting.
- **Tracker back-off (3 failures) and periodic re-announce**: already fully
  implemented in `AggregatingPeerSource.PollTrackerAsync` —
  `MaxConsecutiveTrackerFailures = 3` gives up on a tracker after 3
  consecutive failed announces, and a successful announce schedules the
  next one after `max(tracker's WaitTime, 30s)`, resetting the failure
  counter. No changes needed; verified by reading the existing code.
- **Magnet metadata fetch was one-shot**: investigated as part of a
  follow-up report ("peers connect but no metadata/file list ever
  appears") — `NetworkedSessionManager` tried BEP-9 metadata fetch exactly
  once per Start; if it failed (too few peers known that early), the
  torrent was stuck at "(fetching metadata…)" forever even as its peer
  count kept climbing from ongoing tracker/DHT/PEX activity. Fixed by
  turning `RunDeferredMetadataFetchAsync` into a retry loop
  (`_metadataRetryDelay`, default 30s) that keeps trying until it succeeds
  or the torrent is stopped/removed. The stub (0-piece) connection manager
  now starts after the *first* failed attempt (not never), so peers keep
  connecting/exchanging PEX between retries instead of the swarm staying
  frozen. `BuildPieceAndConnectionManagers` now disposes the previous
  connection manager before replacing it, since it may genuinely be
  running (with live connections) by the time a later retry succeeds —
  previously this was safe only because the stub was never started before
  being replaced.
  - **Known follow-up, not yet fixed**: peer candidates already delivered
    to the stub connection manager aren't replayed to the rebuilt one
    after a successful late fetch (`AggregatingPeerSource` dedupes
    `PeerFound` at the source, so a new subscriber never sees
    already-reported endpoints) — piece downloading has to wait for fresh
    peer discoveries rather than immediately using the 50+ peers already
    known. Not fixed this pass; flagged for a later task.
  - **Also noticed, not yet fixed**: `PeerConnectionManager` never
    registers `UTMetadata` as a servable extension, and
    `ExtendedProtocolExtensions.OnHandshake` never advertises
    `metadata_size` — so this client can never *serve* metadata to other
    peers once it has it, even though it can now reliably *fetch* it. Only
    matters for helping other magnet-only leechers in the swarm; doesn't
    affect this client's own downloads.
- Tests: `NetworkedSessionManagerTests.InitializeAsync_ResumesSessionsThatWereActiveOnLastShutdown`,
  `InitializeAsync_OneSessionFailingToResume_StillResumesTheOthers`,
  `RunDeferredMetadataFetchAsync_KeepsRetryingAfterAFailedAttempt`.

## Peers connect but nothing downloads/no metadata shown (user-reported)

- Root cause: `RarestFirstPieceManager` clones `session.PieceCompletion` at
  construction (so it can resume from prior progress) but from then on
  tracked piece completion purely in its own private `_completed` array —
  nothing ever wrote a finished, verified piece back to
  `TorrentSession.PieceCompletion`. Since the UI's progress bar, "downloaded"
  size, and `Completed` state all derive from `session.ProgressFraction`
  (which reads `PieceCompletion`), they stayed stuck at 0% forever — even
  while bytes were genuinely being received and written to disk. Restarting
  a torrent would also silently re-download everything already fetched,
  since the cloned completion state was never persisted back either.
- Fixed by adding `IPieceManager.PieceCompleted` (an `event Action<int>?`,
  raised by `RarestFirstPieceManager.OnBlockReceived` once a piece verifies)
  and wiring it in `NetworkedSessionManager.BuildPieceAndConnectionManagers`:
  `pieceManager.PieceCompleted += session.MarkPieceVerified;`. Raised outside
  the piece manager's internal lock to avoid running the session callback
  under it.
- This was investigated after a user report of "significant numbers of
  peers connecting, but nothing being downloaded or any metadata being
  transferred" — peer connections and the wire protocol itself checked out
  (handshake/bitfield/request/piece paths were all already correct), so the
  missing link was specifically this completion feedback loop, not the
  networking layer.
- Tests: `RarestFirstPieceManagerTests.OnBlockReceived_CorrectHash_RaisesPieceCompleted`,
  `OnBlockReceived_WrongHash_DoesNotRaisePieceCompleted`;
  `NetworkedSessionManagerTests.OnBlockReceived_CompletesPiece_UpdatesSessionProgress`
  (builds a real single-piece .torrent via `Metadata.CreateFromPath` to
  exercise the full wiring end-to-end, not just the piece manager in
  isolation).

## Per-torrent and global network stats (user-requested)

- **Per torrent** (`TorrentListView` row): size (verified-progress bytes /
  total), download/upload speed, alongside the existing state/progress/peer
  count. Speed is computed client-side in `TorrentRowViewModel` by diffing
  `TorrentNetworkStats.BytesDownloaded/Uploaded` between successive 1-second
  UI refreshes — no rate-tracking added to the engine itself. "Downloaded"
  bytes shown use verified progress (`ProgressFraction * totalBytes`); the
  raw `BytesDownloaded` counter (which also counts bytes from pieces that
  later failed hash verification) is used only for the speed calculation.
- **Global footer** (`Features/StatusFooter`): DHT node count + peers found,
  PEX peers found, LAN (LPD) peers found, tracker peers found, and total
  download/upload speed, summed across every session via a new
  `ITorrentRuntimeInfoProvider.GetNetworkStats(sessionId)` /
  `TorrentNetworkStats` record.
- Engine additions backing this: `IPeerSource` gained
  `TrackerPeersFound`/`DhtPeersFound`/`LanPeersFound`/`DhtNodeCount` (raw,
  not deduped — reflects discovery-channel activity, not unique peers);
  `IDhtPeerFinder` gained `NodeCount` (forwards `DHTClient.NodeCount`);
  `IPeerConnectionManager` gained `PexPeersFound`/`BytesDownloaded`/
  `BytesUploaded`, incremented at the `Piece`/`Request`/PEX-`Added` handlers
  already present in `PeerConnectionManager` from the PEX work.

## Start button not re-enabled after Stop (user-reported)

- Root cause: `NetworkedSessionManager.StartAsync` awaited the whole magnet
  metadata fetch (up to `metadataFetchTimeout`, default 90s) inline before
  returning. The UI's Start command's `IsRunning` (and thus the button's
  enabled state, via `AsyncRelayCommand`'s built-in re-entrancy guard) stayed
  true for that entire window — so clicking Stop on the same torrent couldn't
  re-enable Start, since Start was a different, still-in-flight command.
  Fixed by detaching the metadata fetch into `RunDeferredMetadataFetchAsync`:
  `StartAsync` now returns as soon as the peer source/connection manager (for
  torrents with known metadata) are up; for magnet torrents the connection
  manager starts once the background fetch resolves (success or timeout),
  guarded by a runtime-identity check so a Stop/Remove that races the fetch
  doesn't resurrect a torn-down runtime.
- Secondary, related bug: all rows shared one `AsyncRelayCommand` instance
  per action (Start/Pause/Stop/Remove) on `TorrentListViewModel`, parameterized
  via `CommandParameter`. Since `IsRunning` lives on the command, one row's
  slow operation disabled that button for *every* row. Fixed by moving
  Start/Pause/Stop/Remove commands (and a per-row `ErrorMessage`) onto
  `TorrentRowViewModel` itself, each row getting its own command instances;
  `TorrentListView.axaml` bindings simplified accordingly (no more
  `$parent[UserControl]` DataContext lookups).
- Tests: `NetworkedSessionManagerTests.StartAsync_MagnetOnlySession_ReturnsWellBeforeMetadataFetchTimeout`,
  `StopAsync_WhileMetadataFetchStillPending_DoesNotResurrectTheRuntime`;
  `TorrentListViewModelTests.RowStartCommand_DoesNotBlockOnASlowStart`.

## Post-050-054 fixes (user-reported)

- Rail icons (Add/Settings) were invisible: `IconPlus`/`IconClose`/`IconSettings`
  were open-line geometries, but Avalonia's `PathIcon` only fills `Data` via
  `Foreground` — it doesn't stroke. Redrawn as closed filled shapes in
  `Styles/Icons.axaml`.
- `MetadataFetcher.TryFetchFromPeer`'s `finally { client.Disconnect(); }` was
  unguarded (unlike `PeerConnectionManager`'s), so a peer whose connection
  already dropped internally could throw on cleanup (NRE or a raw
  `SocketException`, e.g. ENOTCONN/"transport endpoint is not connected") —
  faulting the whole metadata-fetch worker and surfacing as a `StartAsync`
  failure in the UI several seconds to minutes later. Extracted the existing
  double-disconnect guard into a shared `PeerWireSafety.SafeDisconnect`,
  used by both `PeerConnectionManager` and `MetadataFetcher` now.
- "No peers/files/piece-map after Start" for a magnet add is expected until
  a real peer actually responds (DHT bootstrap + BEP-9 fetch both need real
  reachable peers/bootstrap nodes) — not a bug found so far, but worth
  revisiting if it turns out to persist with known-good, well-seeded
  magnets and open UDP egress.
- **Pressing Stop crashed the whole app.** Root cause was in `bzTorrent`
  itself: `LocalPeerDiscovery.Close()` closes the reader socket while a
  `BeginReceiveFrom` may still be pending; the pending callback's
  `EndReceiveFrom` then throws (socket closed), and that callback runs on a
  thread-pool thread outside `Close()`/`Dispose()`'s own try/catch — an
  unhandled exception on a thread-pool thread terminates the whole process,
  not just this component. Every torrent's `AggregatingPeerSource.Stop()`
  disposes its `LanPeerFinder`, so this fired on every Stop. Fixed by
  wrapping the receive callback body in try/catch (`ObjectDisposedException`/
  `SocketException` = normal shutdown) and moving `isReceiving = false` into
  a `finally` (it was also only reachable on the success path before, so any
  transient receive error — not just shutdown — silently wedged LPD forever).
  Regression test added: `OpenThenClose_WhileReceivePending_DoesNotCrashProcess`
  in `bzTorrent.Tests/LocalPeerDiscoveryTests.cs`. **Follow-up**: that first
  fix only covered the async callback's `EndReceiveFrom` — the regression
  test still crashed the test host, because the *arming* call
  (`BeginReceiveFrom` itself, on `Process()`'s own dedicated thread) can
  also throw synchronously (`ObjectDisposedException`) if `Close()` disposes
  the socket in the gap between the loop's `!_killSwitch` check and the
  call. Wrapped that call in its own try/catch too (return on either
  exception type — `_killSwitch` will be observed on the next loop
  iteration regardless). Verified by running the regression test and the
  full `bzTorrent.Tests` suite 5x in a row with no crash.
- **Added PEX (BEP-11)** to `PeerConnectionManager`: registers
  `ExtendedProtocolExtensions` + `UTPeerExchange` per connection (skipped for
  private torrents, matching DHT/LPD), feeds peers we learn about into the
  existing `AddPeerCandidate` dedup/candidate queue, and reciprocates by
  broadcasting our own currently-connected endpoints to each peer every 30s
  (simplified vs. a full PEX implementation — no "dropped" tracking, a peer
  may get re-announced to someone who already has it — harmless since
  candidates are deduped on the receiving end). Not yet implemented: LT
  tracker-exchange, `FastExtensions` — still tracker + DHT + LAN + PEX only.

## Setup

- [x] TASK-001: Resolve open plan items (Playwright vs Avalonia.Headless,
      JSON vs EF Core session store, default listen port) with the user
      before any engine/UI code is written. Resolved: Avalonia.Headless.XUnit,
      EF Core + SQLite, port 6881.
- [x] TASK-002: Scaffold `bzTorrentClient.Engine` (net10.0 class library)
      and `bzTorrentClient.Engine.Tests` (xUnit + Moq). Depends on:
      TASK-001.
- [x] TASK-003: Scaffold `bzTorrentClient.Avalonia` (Avalonia desktop app,
      net10.0) and `bzTorrentClient.Avalonia.Tests`
      (Avalonia.Headless.XUnit). Depends on: TASK-001.
- [x] TASK-006: Create `bzTorrentClient/bzTorrentClient.sln` referencing
      `bzTorrent.csproj` plus the four client projects, and keep the
      client out of `bzTorrent.sln` entirely — the library's build/test/
      release pipeline must not build the client, but the client's build
      must always build against the library. Depends on: TASK-002,
      TASK-003.
- [x] TASK-004: Add `bzTorrentClient/README.md` describing the client for
      end users (install/run on Windows and Linux). Depends on: TASK-002,
      TASK-003.
- [x] TASK-005: GitHub Actions workflow: build+test matrix
      (`ubuntu-latest`, `windows-latest`) for the three new test-bearing
      projects. Depends on: TASK-002, TASK-003. Implemented as
      `.github/workflows/bztorrentclient-ci.yml`.

## Engine — core session model

- [x] TASK-010: `TorrentSession` model + `TorrentState` enum (`Paused`,
      `Active`, `Stopped`, `Checking`, `Error`, `Completed`) and the
      state-transition rules from `PLAN.md` (start-paused on add, pause =
      soft halt, stop = hard halt). Depends on: TASK-002. Also introduced
      `TorrentAddSource` (`TorrentFile` bytes / `Magnet` URI, the latter
      also covering raw info-hash adds as a trackerless magnet) since
      TASK-012's persistence needs a way to re-resolve `IMetadata` after a
      restart — bzTorrent has no `IMetadata` serializer.
- [x] TASK-011: `IClientSettings` (default download dir, global max
      connections, max connections per torrent, listen port) with a
      concrete settings implementation. Depends on: TASK-002.
- [x] TASK-012: `ISessionStore` — persist/reload session list (state,
      progress, bitfield, download dir) across restarts, backed by EF
      Core + SQLite (`BzTorrentClientDbContext`, `EfSessionStore`) behind
      the `ISessionStore` repository abstraction. Depends on: TASK-010,
      TASK-011.
- [x] TASK-013: `ISessionManager` — add/remove torrents, start/pause/stop,
      dedupes by info-hash, loads persisted sessions on startup
      (`InitializeAsync`), and exposes a global connection-budget
      reservation (`TryReserveConnections`/`ReleaseConnections`) for the
      future peer-connection manager (TASK-041/042) to consume — actual
      per-connection accounting isn't wired up until those tasks exist.
      Depends on: TASK-010, TASK-012.

## Engine — add-torrent pipeline

- [x] TASK-020: Add by `.torrent` file — `TorrentAddPipeline.AddFromFileAsync`
      reads the file and calls `ISessionManager.AddAsync` with a
      `TorrentAddSource.TorrentFile`. Depends on: TASK-013.
- [x] TASK-021: Add by magnet link — `TorrentAddPipeline.AddFromMagnetAsync`,
      validates via `MagnetLink.IsMagnetLink` and adds a
      `TorrentAddSource.Magnet`. Depends on: TASK-013.
- [x] TASK-022: Add by raw info-hash — `TorrentAddPipeline.AddFromInfoHashAsync`
      validates the 40-hex-char hash and adds it as a trackerless
      `TorrentAddSource.Magnet` (`TorrentAddSource.Magnet.FromInfoHash`).
      The actual DHT peer discovery + `UTMetadata` (BEP-9) fetch of full
      metadata happens lazily in `NetworkedSessionManager.StartAsync` (see
      TASK-042) rather than at add time — a magnet/hash torrent legitimately
      has no piece data until it's started, matching "start while paused".
      Depends on: TASK-013, TASK-040.

## Engine — transfer pipeline

- [x] TASK-030: `ITorrentStorage` / `FileSystemTorrentStorage` — maps
      piece/block offsets to a torrent's file(s) via `metadata.GetFileInfos()`;
      reads for verification, writes incoming blocks, handles pieces
      spanning multiple files. Depends on: TASK-010.
- [x] TASK-031: `IPieceManager` / `RarestFirstPieceManager` — rarest-first
      piece/block selection from peer bitfields, tracks in-flight vs.
      received blocks separately (avoids re-requesting an already-requested
      block), SHA1 verification of completed pieces against
      `metadata.PieceHashes`, resets a piece's progress on a hash mismatch
      so it gets re-requested. Depends on: TASK-030.
- [x] TASK-040: `IPeerSource` / `AggregatingPeerSource` — aggregates
      `HTTPTrackerClient`/`UDPTrackerClient` announces (per-tracker polling
      loop with backoff), DHT, and LAN discovery (BEP-14) into one peer
      feed per torrent, deduped by endpoint. DHT/LAN are skipped for
      private torrents (BEP-27). One `DHTClient`/`LocalPeerDiscovery`
      instance per active torrent rather than shared process-wide, since
      `DHTClient.PeerFound` doesn't tag which info-hash a result is
      for — sharing one DHT node across concurrent torrent searches would
      make results ambiguous. `IDhtPeerFinder`/`ILanPeerFinder` seams let
      tests fake these out entirely (no real sockets in unit tests).
      Depends on: TASK-010.
- [x] TASK-041: `IPeerConnectionManager` / `PeerConnectionManager` — owns up
      to `maxConnectionsPerTorrent` outbound `IPeerWireClient` connections,
      reserves/releases the global connection budget from TASK-013 per
      connection, sends our own bitfield and `Have` broadcasts to
      connected peers, unchokes anyone who's interested (no tit-for-tat
      choking algorithm — out of MVP scope), requests blocks via
      `IPieceManager`, and serves `Request`s for pieces we already have.
      **No inbound listener** — MVP is outbound-only, so we only ever seed
      to peers we ourselves connected to for downloading, not fresh
      incoming connections; noted as a known gap, not silently dropped.
      Depends on: TASK-031, TASK-040.
- [x] TASK-042: `NetworkedSessionManager` — decorates the plain
      `SessionManager` (TASK-013) to create/own each torrent's
      `IPeerSource` + `IPeerConnectionManager` runtime. `StartAsync`
      creates the runtime on first use, kicks off a best-effort BEP-9
      metadata fetch for magnet/hash sessions (via `MetadataFetcher`,
      rebuilding the piece/connection managers once real piece data
      arrives, and promoting the session's `TorrentAddSource` to the
      fetched `.torrent` bytes so a restart doesn't need to re-fetch).
      `PauseAsync` disconnects peer connections but leaves the peer source
      running (soft halt — matches "no fresh announce needed to resume").
      `StopAsync`/`RemoveAsync` fully dispose both (hard halt). Depends
      on: TASK-013, TASK-041.

**Incidental fixes in the sibling `bzTorrent` library**, found and fixed
while building TASK-041's integration test (a real peer connection
disconnected immediately after every handshake, with no matching bug in
bzTorrentClient's own code):
- `BitfieldHandler.Handle` compared `Payload.Length < packet.CommandLength`
  directly, but `PeerWirePacket.Parse` always produces `CommandLength ==
  Payload.Length + 1` (the message-ID byte) for anything that reaches a
  handler — so the check was always true and closed the connection on
  *every* incoming bitfield message, for any client using this library.
  Removed the check (`PeerWirePacket.Parse` already guarantees a complete
  payload before a handler ever runs).
- `PeerWireClient.SendBitField` allocated `bitField.Length / 8` bytes with
  no rounding, throwing `IndexOutOfRangeException` for any piece count not
  an exact multiple of 8 (i.e. almost every real torrent). Fixed to
  `(bitField.Length + 7) / 8`.
- Both had regression tests added in `bzTorrent.Tests`
  (`Protocol/Handlers/MessageHandlerTests.cs`, `PeerWireClientTests.cs`,
  `PeerWireClient/PeerWireClientIntegrationTests.cs`); the previous
  `BitfieldHandlerTests`/integration test encoded the same wrong
  `CommandLength == Payload.Length` assumption as the bug and were
  corrected rather than just left passing. Full `bzTorrent.Tests` suite
  (194 tests) still passes.

## UI (Avalonia)

- [x] TASK-050: Shell — `Shell/MainWindow` (moved out of the generic
      `Views/`/`ViewModels/` template split into a `Shell/` slice, and
      `Features/<name>/` per feature, per PLAN.md's vertical-slice
      architecture) hosts `TorrentListView` (top) and `TorrentDetailsView`
      (bottom, showing whatever's selected in the list), plus toolbar
      buttons that raise `AddTorrentRequested`/`SettingsRequested` events
      for the view's code-behind to open as dialogs — keeps `Window`/
      `StorageProvider` APIs out of the view models. `App.axaml.cs` is the
      composition root: builds the SQLite `EfSessionStore`, `ClientSettings`
      (loaded via a new `JsonClientSettingsStore` — see below),
      `SessionManager` wrapped in `NetworkedSessionManager`, and
      `TorrentAddPipeline`, all under `%AppData%/bzTorrentClient/`.
      Depends on: TASK-003.
- [x] TASK-051: `Features/TorrentList` — `TorrentListViewModel` polls
      `ISessionManager.Sessions` on a 1-second `DispatcherTimer` (the
      engine has no change-notification events yet, and adding them was
      judged a bigger engine change than this UI pass warrants) and
      reconciles into an `ObservableCollection<TorrentRowViewModel>`
      showing name/state/progress/peer-count, with per-row Start/Pause/
      Stop/Remove buttons bound to commands on the row's own
      `TorrentRowViewModel`. **Speed is not shown** — the engine doesn't
      track transfer rates (out of scope for tasks 30-42); shown as a gap,
      not faked. Peer count needed a small new `ITorrentRuntimeInfoProvider`
      interface (implemented by `NetworkedSessionManager`, exposing
      `GetActiveConnectionCount`/`GetConnectedPeers` per session) since
      `ISessionManager` itself intentionally has no networking-specific
      members. Depends on: TASK-013, TASK-050.
- [x] TASK-052: `Features/AddTorrent` — `AddTorrentWindow` dialog with a
      mode switch (file / magnet / info-hash) via radio buttons, a file
      picker and download-directory picker (via Avalonia's
      `IStorageProvider`, in the view's code-behind), and an "add paused"
      checkbox (default on, matching "start while paused"). Calls straight
      through to `TorrentAddPipeline`. Depends on: TASK-020, TASK-021,
      TASK-022, TASK-050.
- [x] TASK-053: `Features/TorrentDetails` — Peers/Files/Trackers tabs for
      the selected torrent. Peers needed `IPeerConnectionManager` to track
      connected endpoints (`ConnectedEndpoints`, keyed by peer ID alongside
      the existing `_activeClients`), surfaced through the same
      `ITorrentRuntimeInfoProvider.GetConnectedPeers`. Files/Trackers read
      straight from `Metadata.GetFileInfos()`/`AnnounceList`. Depends on:
      TASK-042, TASK-050.
- [x] TASK-054: `Features/Settings` — edits `IClientSettings` with
      validation (non-empty download dir, positive connection limits,
      1-65535 port) before saving. Settings persistence wasn't in TASK-011's
      original scope (that was just the in-memory model), but a Settings
      screen that resets every launch would be a broken feature, so added
      `IClientSettingsStore`/`JsonClientSettingsStore` (plain JSON file,
      same rationale as the DB-vs-JSON discussion in PLAN.md — this is
      config, not queryable state) alongside it. Depends on: TASK-011,
      TASK-050.

`bzTorrentClient.Avalonia.csproj` also needed `<ImplicitUsings>enable</ImplicitUsings>`
added (the Avalonia project template doesn't enable it by default, unlike
`bzTorrentClient.Engine`), and the stray `<Folder Include="Models\" />`
template leftover was removed since no `Models/` folder is used.

Manually verified: `dotnet run` on the Avalonia project starts without
exceptions and the composition root creates `sessions.db` under
`%AppData%/bzTorrentClient/` as expected.

Fixed the `bzTorrentClient.Avalonia.Tests` "could not find app host
executable" issue flagged after TASK-003: the scaffold had `Avalonia.
Headless.XUnit` 12.0.5 (which pulls in xUnit v3) alongside a direct
`xunit` v2 package reference — once real test files existed, this surfaced
as `FactAttribute` existing in both `xunit.core` (v2) and `xunit.v3.core`.
Swapped the v2 `xunit` package for `xunit.v3` 3.2.2 (keeping
`xunit.runner.visualstudio`, `Avalonia.Headless.XUnit`, `coverlet.
collector`). Added 23 tests covering the view models introduced in
TASK-050-054 (`TorrentListViewModel`, `TorrentDetailsViewModel`,
`SettingsViewModel`, `AddTorrentViewModel`) — plain xUnit against a
`FakeSessionManager`, no Avalonia rendering needed since these are pure
`ObservableObject`/`CommunityToolkit.Mvvm` classes. View-level
`Avalonia.Headless` rendering tests are still follow-up work, not done here.

## Phase 2 (deferred — not scheduled, tracked for later)

- [ ] TASK-100: Per-file selection/priority within a multi-file torrent.
- [ ] TASK-101: Sequential download mode toggle.
- [ ] TASK-102: Global and per-torrent upload/download speed limits.
- [ ] TASK-103: Seed ratio / seed time limits, auto-stop-seeding.
- [ ] TASK-104: Queueing — cap simultaneously active torrents, queue rest.
- [ ] TASK-105: Manual force re-check / re-hash of an existing torrent.
- [ ] TASK-106: Remove torrent with optional delete-files-on-disk.
- [ ] TASK-107: Import/export client settings.

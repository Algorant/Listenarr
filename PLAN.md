# Deluge support + Transmission completion fix plan

This fork is being used to add Deluge download-client support while also fixing a separate Transmission completed-download import bug discovered during homelab testing.

The goals are:

1. Keep qBittorrent behavior unchanged unless an upstream maintainer explicitly asks for a later refactor.
2. Ship the Transmission fix as a small, upstreamable bugfix.
3. Ship Deluge support as a separate upstreamable feature.
4. Use a local integration branch/image for homelab testing both changes together.
5. Keep private tracker testing minimal and manual.

## Current fork state

- `origin/canary` tracks the fork's current canary branch.
- `origin/feature/deluge-download-client` already exists and contains Deluge work.
- That Deluge branch is based on older upstream canary around `v1.0.7`.
- Current canary is newer (`v1.0.11` era at the time this plan was written).
- The Deluge work itself is concentrated in one top commit:
  - `ae974002 Add Deluge download client support`
- Because the branch base is old, diffing it against current canary shows many unrelated upstream changes. Do not upstream or merge that branch as-is.

## Branch strategy

Use three working branches plus the existing archived Deluge branch.

```text
origin/canary
  ├── fix/transmission-completed-seeding
  ├── feature/deluge-download-client-v2
  └── integration/deluge-plus-transmission

origin/feature/deluge-download-client   # existing old branch; preserve as reference
```

### 1. `fix/transmission-completed-seeding`

Purpose: small, focused bugfix.

Scope:

- Fix Transmission completed/seeding torrents staying as internal `Downloading`.
- Do not add Deluge.
- Do not alter qBittorrent behavior.
- Avoid broad shared status-map changes unless absolutely required.

Observed bug from homelab test:

- Listenarr manual search -> Prowlarr/MAM -> seedbox Transmission worked.
- Transmission completed the torrent and began seeding.
- Listenarr queue showed the item as completed externally, but the DB download record stayed `Downloading` with `importAttempts=0`.
- No processing/import job was queued.
- Manual import copied the completed file successfully, proving paths and permissions were correct.

Likely cause:

- `AdapterUtils.MapDownloadProgress` maps `seeding` to `DownloadStatus.Downloading`.
- qBittorrent compensates with an explicit completion override.
- Transmission computes an `isComplete` value but currently does not call `dl.Completed()` in the same way.

Low-risk fix:

- In `TransmissionAdapter.FetchDownloadsAsync`, after computing Transmission progress/status:
  - consider a torrent complete when `percent >= 1.0` or `leftUntilDone == 0`, and it is in a completed-side state such as seeding/seed-wait/stopped.
  - call `dl.Completed()` once the completion condition is met.
- Leave `AdapterUtils` and qBittorrent unchanged for this branch.

Suggested tests:

- Add/adjust a Transmission adapter test covering:
  - status `SEED` / `seeding`
  - `percentDone = 1.0`
  - `leftUntilDone = 0`
  - expected DB status transitions to `Completed`.
- Include a non-complete downloading case to ensure it remains `Downloading`.

Upstream PR framing:

> Transmission completed torrents remain Downloading and never import

### 2. `feature/deluge-download-client-v2`

Purpose: Deluge support as a separate feature.

Scope:

- Reapply the useful Deluge work from `origin/feature/deluge-download-client` onto current canary.
- Prefer cherry-picking `ae974002` onto a fresh current branch, then resolve conflicts intentionally.
- Add or update tests for Deluge state mapping, add, remove, queue polling, and import item resolution where practical.
- Do not change qBittorrent behavior.
- Do not depend on MAM for initial tests.

Important review of existing Deluge branch:

- Existing Deluge work touches:
  - frontend download-client type/options
  - download client configuration model
  - service registration
  - `listenarr.infrastructure/Adapters/DelugeAdapter.cs`
  - README notes
- Before trusting it, review the existing adapter for:
  - matching downloads by external torrent hash, not Listenarr client ID.
  - complete/seeding/paused-complete status handling.
  - correct import item source files and content path.
  - category/label filtering.
  - seed-limit/removal semantics.
  - authenticated Deluge Web JSON-RPC session handling.

Deluge state expectations:

- Deluge/libtorrent states include variants like:
  - `Downloading Metadata`
  - `Downloading`
  - `Checking`
  - `Seeding`
  - `Paused`
  - `Queued`
  - `Error`
- Deluge maps libtorrent `finished` and `seeding` to `Seeding`.
- A Deluge torrent should be import-eligible when the payload is complete and visible to Listenarr, even if the torrent is still seeding.

Recommended Deluge completion rules:

- `Seeding` + progress 100 -> completed/import-eligible.
- `Paused` + progress 100 -> completed/import-eligible.
- `Queued` + progress 100 -> completed/import-eligible or completed-pending-seed.
- `Downloading` or `Downloading Metadata` -> downloading.
- `Checking` -> checking/downloading, not importable until complete and stable.
- `Error` / missing files -> failed.

Upstream PR framing:

> Add Deluge download client support

### 3. `integration/deluge-plus-transmission`

Purpose: private testing branch/image.

Scope:

- Combine:
  - `fix/transmission-completed-seeding`
  - `feature/deluge-download-client-v2`
- Add temporary high-signal diagnostic logging if useful.
- Build and deploy a custom Docker image to the homelab.
- This branch is not intended to be opened upstream directly.

Temporary integration-only logging should be removed or downgraded before upstream PRs.

## Shared status/lifecycle design

Do not force qBittorrent into a new abstraction in the first pass.

A useful additive abstraction for new/fixed clients is a normalized torrent lifecycle result, for example:

```csharp
public sealed record TorrentLifecycleState(
    string RawState,
    bool IsDownloading,
    bool IsChecking,
    bool IsComplete,
    bool IsSeeding,
    bool IsPaused,
    bool IsFailed,
    bool IsImportEligible,
    bool CanBeRemoved,
    bool CanMoveFiles
);
```

Rules:

- Import eligibility should be based on payload completion, file visibility, and the stability window.
- Import eligibility should not require the torrent to stop seeding.
- Seeding state should affect cleanup/removal policy, not whether copying/importing can happen.
- qBittorrent can continue using its existing code until maintainers choose to migrate it.

This keeps the feature additive and lowers regression risk.

## Homelab testing constraints

The real homelab test uses MyAnonamouse through local Prowlarr and seedbox download clients.

Constraints:

- MAM access is strict about IP/session behavior.
- Prowlarr is already configured to interface with MAM.
- The actual torrenting must happen on the Ultra.cc seedbox, not from home.
- Keep testing manual and minimal.
- Do not enable RSS/automatic searches for MAM during testing.
- Do not point Listenarr at the real audiobook library until the test root workflow is proven.

Current safe test paths:

- Listenarr test root in container:
  - `/data/media/test/listenarr_audiobooks`
- Host test root:
  - `/srv/data/BIGNAS/Media/test/listenarr_audiobooks`
- Seedbox rclone mount on host/container:
  - `/mnt/seedbox`
- Transmission test path:
  - seedbox: `/home/incertophile/files/listenarr`
  - local: `/mnt/seedbox/files/listenarr`

## Testing workflow

### Fast local tests first

Before using MAM or the seedbox, test state mapping and adapter behavior locally.

For Transmission:

- Mock or fixture RPC torrent with status `SEED`, `percentDone=1.0`, `leftUntilDone=0`.
- Verify FetchDownloads marks the Listenarr download `Completed`.

For Deluge:

- Mock JSON-RPC `web.update_ui` responses for:
  - `Seeding`, progress 100
  - `Paused`, progress 100
  - `Queued`, progress 100
  - `Downloading`, progress less than 100
  - `Checking`
  - `Error`
- Verify the normalized status and import eligibility.

### Build Docker image

Example local tag:

```bash
docker build -t listenarr:homelab-deluge-test .
```

If Dockhand cannot access a local image, push a fork image to GHCR instead:

```bash
docker build -t ghcr.io/Algorant/listenarr:homelab-deluge-test .
docker push ghcr.io/Algorant/listenarr:homelab-deluge-test
```

Then update the homelab `stacks/arrs/compose.yml` Listenarr image temporarily to that tag.

### Transmission end-to-end test

Goal: prove the bugfix automatically imports completed seeding Transmission downloads.

Steps:

1. Deploy the custom image.
2. Keep Listenarr pointed only at the isolated test root.
3. Use one existing or new manual test audiobook.
4. Search manually in Listenarr.
5. Select one MAM result manually.
6. Confirm Transmission receives and completes it on the seedbox.
7. Confirm Listenarr transitions the DB download record to `Completed` and queues an import job.
8. Confirm the file lands under the isolated test root automatically.
9. Confirm the torrent/source file remains on the seedbox when completed-download handling is set to keep/none.

If automatic import still fails, collect:

- Listenarr logs around `DownloadMonitorService`, `TransmissionAdapter`, and import processing.
- `/api/v1/downloads`
- `/api/v1/download/queue`
- `/api/v1/download/processing/activity`
- source and destination paths.

### Deluge end-to-end test

Goal: prove Deluge can replace Transmission as the preferred seedbox torrent client.

Pre-MAM checks:

1. Add Deluge download client in Listenarr.
2. Test Deluge connection to the Ultra.cc daemon/Web API path.
3. Verify queue polling shows existing Deluge torrents without touching unrelated categories.
4. Verify configured category/label isolates Listenarr traffic.

MAM test:

1. Keep MAM usage to one manual release.
2. Search manually in Listenarr through Prowlarr.
3. Select one release manually.
4. Confirm Deluge receives it on the seedbox.
5. Confirm Deluge state reaches `Seeding` or another complete-side state.
6. Confirm Listenarr imports automatically into the isolated test root.
7. Confirm seeding continues or cleanup behavior matches settings.

## Homelab cleanup after verification

After each live test:

1. Verify imported file exists in the isolated test root.
2. Verify Listenarr library status and file count.
3. Verify the source torrent remains or is removed according to the configured completed-download handling policy.
4. If a stale Listenarr DB download record remains but the file was manually imported:
   - remove only the Listenarr DB queue/download record with force/database-only cleanup;
   - do not delete seedbox torrent files unless intentionally cleaning up.
5. Remove or pause test torrents in the seedbox client only after deciding whether continued seeding is required.
6. Keep real audiobook library unconfigured until the workflow is reliable.
7. Revert the homelab compose image tag from the temporary test image back to upstream canary unless continuing testing.
8. Remove temporary debug logging before opening upstream PRs.

## Upstream cleanup plan

Prepare two PRs, not one:

1. Transmission bugfix PR
   - minimal code diff
   - focused tests
   - no Deluge changes
   - no qBittorrent changes

2. Deluge support PR
   - rebased onto current upstream canary after the Transmission PR is merged or kept independent if possible
   - Deluge adapter, UI option, registration, docs/tests
   - no qBittorrent changes

The integration branch remains private/local and should not be upstreamed as-is.

## Open decisions

- Whether to introduce a small `TorrentLifecycleState` helper in the Deluge PR or keep mapping local to `DelugeAdapter`.
- Whether the Transmission fix should use a shared helper or only mirror the existing qBittorrent completion override.
- Whether homelab should deploy local Docker images directly or pushed GHCR images for Dockhand compatibility.
- How much Deluge seed-limit cleanup behavior to implement in the first PR versus follow-up.

## Deluge lifecycle audit from homelab integration test

This section records issues discovered after deploying the private integration image `ghcr.io/algorant/listenarr:deluge-transmission-canary` into the homelab `arrs` stack.

### Proven good in the deployed integration image

- Listenarr can create a Deluge download-client config via API.
- Deluge Web JSON-RPC authentication works through the local `deluge-web:8112` bridge to the Ultra.cc daemon.
- Connection test returns `Deluge: connected to Web UI and daemon`.
- Queue polling reaches the Deluge daemon and reports zero `listenarr`-labeled items when none exist.
- Remote path mapping works for the intended isolated path:
  - remote: `/home/incertophile/files/listenarr/`
  - local: `/mnt/seedbox/files/listenarr/`
- Deluge Label plugin is enabled on the Ultra.cc daemon.

### Homelab failure that triggered this audit

A controlled manual UI grab was attempted with `ultra-transmission` disabled and `ultra-deluge` enabled.

Listenarr failed before adding the torrent to Deluge:

```text
No suitable download client found for torrent. Please configure and enable a torrent client (qBittorrent or Transmission) in Settings.
```

Relevant log evidence:

```text
Looking for torrent client. Found 1 enabled download clients: Ultra.cc Deluge (deluge)
No torrent client (qBittorrent or Transmission) found among enabled clients
```

This means Deluge config/test support exists, but at least one send/grab selection path still only considers qBittorrent and Transmission.

### Required patch checklist before the next image rebuild

Patch these together before rebuilding/recontainerizing. Do not rebuild for only the first item; otherwise the next live test is likely to fail farther downstream.

#### 1. Include Deluge in torrent client auto-selection

Update all torrent-client selection paths to include `deluge`.

Known locations from the old Deluge branch audit:

- `listenarr.application/Downloads/DownloadService.cs`
  - `GetAppropriateDownloadClient(bool isTorrent)`
  - user-facing `neededClients` error text
- `listenarr.application/Search/AutomaticSearchService.cs`
  - its local appropriate-client selection helper
- `listenarr.api/Controllers/LibraryController.cs`
  - legacy/internal appropriate-client selection helper if still present/used

Recommended behavior:

- If one enabled torrent client exists and it is Deluge, select it.
- Prefer existing behavior for qBittorrent/Transmission unless intentionally changed.
- Suggested upstream-safe preference order:

```text
qBittorrent -> Transmission -> Deluge
```

For homelab Deluge testing, disable Transmission so Deluge is selected.

Update messages from:

```text
qBittorrent or Transmission
```

to:

```text
qBittorrent, Transmission, or Deluge
```

#### 2. Create Deluge labels before setting them

The Ultra.cc daemon currently has the Deluge Label plugin enabled, but the `listenarr` label did not exist during audit.

Current observed labels included:

```text
btn, local-sonarr, mam, movieclub, mtv, radarr, radarr-test, readarr, sonarr, sonarr-test, stephen, ufc
```

Potential failure mode if unpatched:

1. Deluge torrent is added successfully.
2. `label.set_torrent` fails because `listenarr` label does not exist.
3. The adapter logs/debug-suppresses the label failure.
4. Queue filtering by configured category/label hides the torrent.
5. Listenarr never tracks/imports it.

Patch `DelugeAdapter.TrySetLabelAsync` or equivalent to:

1. Call `label.get_labels`.
2. If the configured label/category is missing, call `label.add`.
3. Then call `label.set_torrent`.
4. Treat label setup failure as at least a warning; consider failing add when a category is configured but cannot be applied, because category filtering depends on it.

#### 3. Match Deluge downloads by external torrent id/hash, not client config id

`DelugeAdapter.FetchDownloadsAsync` in the old branch appeared to do lookup roughly by `d.DownloadClientId`.

That is wrong for active downloads:

- `Download.DownloadClientId` is the Listenarr client config id, e.g. `ultra-deluge`.
- Deluge queue item ids are torrent hashes.

Patch matching to use, in order:

1. `download.GetExternalId()`
2. metadata `ClientDownloadId`
3. metadata `TorrentHash`
4. exact title match as a fallback only if unambiguous

Avoid fuzzy title matching. Previous qBittorrent/Transmission work intentionally avoided fuzzy matching because it can import the wrong files.

#### 4. Fix Deluge import item lookup

`DelugeAdapter.GetImportItemAsync(DownloadClientConfiguration client, Download download, QueueItem queueItem, ...)` should retrieve the current Deluge item using the external torrent hash/id, not `download.DownloadClientId`.

Expected behavior:

- Build candidate ids from `download.GetExternalId()`, `ClientDownloadId`, and `TorrentHash`.
- Search current Deluge items by `DownloadId`/hash.
- Return translated queue item with accurate source files/content path.
- Only fall back to the provided queue item if the current item cannot be found.

Without this, the download may reach `Completed` and queue an import job but then fail during post-processing because source file resolution cannot find the correct Deluge item.

#### 5. Treat Deluge as a torrent/hash client in metadata paths

Where code special-cases torrent hash clients as qBittorrent/Transmission, include Deluge.

Known locations from grep/audit:

- `DownloadService.cs`
  - after `clientGateway.AddAsync`, when setting `TorrentHash` metadata
  - `RemoveFromClientAsync` torrent-id resolution
- `DownloadQueueService.cs`
  - `PersistDiscoveredClientIdentifiersAsync` should set `TorrentHash` for Deluge too
- `MovedDownloadProcessor.cs`
  - cross-client torrent fallback set should include `deluge` if that code path remains relevant

Helper suggestion:

```csharp
private static bool IsTorrentHashClient(string? type) =>
    string.Equals(type, "qbittorrent", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(type, "transmission", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(type, "deluge", StringComparison.OrdinalIgnoreCase);
```

Use a helper rather than repeating three-way checks.

#### 6. Populate Deluge source files from `web.update_ui` file metadata

The old branch `DelugeAdapter` requests `files` but appears to rely primarily on `ContentPath` scanning.

Scanning may work when paths map perfectly, but explicit source files are safer and reduce ambiguity, especially for multi-file torrents.

Deluge `web.update_ui` file entries look like:

```json
{
  "index": 0,
  "path": "Torrent Folder/Book.m4b",
  "offset": 0,
  "size": 123456
}
```

Patch item construction to populate source files as:

```text
save_path + file.path
```

Important details:

- If the torrent is a single file and file path equals the torrent name, source file should be `/save_path/name.ext`.
- If the torrent is a folder, source files should be `/save_path/folder/file.ext`.
- Preserve `ContentPath` for display/scanning fallback, but prefer explicit `SourceFiles` for import.
- Remote path mapping should translate these source files from `/home/incertophile/...` to `/mnt/seedbox/...` in `DownloadClientGateway`.

#### 7. Verify Deluge completion/status mapping

Current Deluge state mapping was reviewed as plausible but still needs tests.

Expected mapping:

- `Seeding` + progress `100` => completed/import-eligible
- `Finished` + progress `100` => completed/import-eligible, if emitted
- `Paused` + progress `100` => completed/import-eligible
- `Queued` + progress `100` => likely completed/import-eligible or completed-pending-seed
- `Downloading` or `Downloading Metadata` => downloading
- `Checking` => checking/downloading, not importable until complete/stable
- `Error` => failed

Add tests around `MapStatus`/`FetchDownloadsAsync` so Deluge does not repeat the Transmission seeding bug.

#### 8. Validate Deluge removal/cleanup behavior but keep homelab cleanup safe

For the first Deluge PR, full seed-limit cleanup behavior can be minimal, but these should not be broken:

- Manual queue remove should pass the Deluge torrent hash, not the Listenarr UUID.
- Completed Download Handling `none` should leave torrent/source files in Deluge.
- Any `remove` / `remove_and_delete` behavior should call `core.remove_torrent(hash, deleteFiles)` with the correct hash.

Homelab should continue using:

```text
removeCompletedDownloads=none
```

until import behavior is proven.

#### 9. Add focused tests before building another GHCR image

Minimum tests recommended before the next expensive image cycle:

- Auto-selection chooses Deluge when it is the only enabled torrent client.
- Auto-selection still chooses qBittorrent/Transmission according to intended preference when multiple clients are enabled.
- Deluge label creation is attempted when category exists in settings and label is missing.
- Deluge add stores returned torrent hash/id as `ClientDownloadId` and `TorrentHash`.
- Deluge `FetchDownloadsAsync` matches DB download by stored external id/hash and marks `Seeding`/100% complete.
- Deluge import item lookup finds the current item by hash and returns usable source files.
- Remote path translation turns Deluge source files under `/home/incertophile/files/listenarr` into `/mnt/seedbox/files/listenarr`.
- Queue/remove path uses Deluge hash rather than Listenarr UUID.

### Homelab retest plan after patch/rebuild

1. Deploy rebuilt integration image.
2. Keep real Audiobooks library unmounted/unconfigured.
3. Keep isolated Listenarr root only:
   - `/data/media/test/listenarr_audiobooks`
4. Keep `ultra-deluge` enabled.
5. Keep `ultra-transmission` disabled to force Deluge.
6. Confirm Deluge client test still returns `Deluge: connected to Web UI and daemon`.
7. Confirm or create Deluge label `listenarr` before grabbing.
8. Do one manual Listenarr/Prowlarr/MAM grab only after explicit user confirmation.
9. Verify Deluge receives the torrent under:
   - `/home/incertophile/files/listenarr`
   - label/category `listenarr`
10. Verify Listenarr queue/download DB tracks the torrent by hash.
11. Verify completed/seeding Deluge item queues import automatically.
12. Verify copied file appears under isolated root.
13. Verify source file/torrent remains in Deluge because cleanup is `none`.

### Do not regress already-proven Transmission behavior

The current integration image successfully fixed Transmission completed/seeding import in homelab testing:

- `Speaker for the Dead` completed on seedbox Transmission.
- Listenarr detected `seeding` with `left=0` as complete.
- Listenarr queued post-processing automatically.
- Listenarr copied the file into the isolated test root without manual import.
- Seedbox source torrent/file remained in place.

Keep this behavior covered while patching Deluge.

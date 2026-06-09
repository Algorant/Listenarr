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

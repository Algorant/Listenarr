# Deluge seedbox integration patch plan

This integration branch combines the already-working Transmission completed/seeding fix with the Deluge work needed for the next homelab seedbox image.

## Goals

1. Keep the proven Transmission fix intact.
2. Make Deluge usable as the only enabled torrent client for manual and automatic grabs.
3. Track Deluge downloads by torrent hash/external id, not the Listenarr client config id.
4. Ensure Deluge category/label filtering cannot hide newly added torrents because the label was missing.
5. Provide explicit Deluge source files for import so remote path mapping and multi-file torrents are reliable.
6. Keep cleanup conservative for the seedbox test; `removeCompletedDownloads=none` should leave Deluge torrents/files in place.

## Implementation checklist

- [x] Add shared torrent-client helpers for preference and hash-id client detection.
- [x] Include Deluge in torrent auto-selection paths while preserving qBittorrent -> Transmission -> Deluge preference.
- [x] Store/persist `TorrentHash` metadata for Deluge wherever qBittorrent/Transmission are treated as torrent-hash clients.
- [x] Use Deluge torrent hash for remove/deferred cleanup paths.
- [x] Create missing Deluge labels before calling `label.set_torrent`.
- [x] Populate Deluge `QueueItem.SourceFiles` from `web.update_ui` `files` metadata.
- [x] Improve Deluge import lookup by trying external id, `ClientDownloadId`, and `TorrentHash` before exact-title fallback.
- [x] Add focused regression tests for selection, metadata, label creation, source files/import lookup, and existing Transmission behavior.
- [x] Run full backend tests and frontend build through mise before building a container.

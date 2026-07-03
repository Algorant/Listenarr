/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Deluge
{
    internal sealed class DelugeImportItemResolver(DelugeRpcClient rpcClient, ILogger logger)
    {
        public async Task<DownloadClientItem> GetImportItemAsync(DownloadClientConfiguration client, DownloadClientItem item, CancellationToken ct = default)
        {
            var result = item.Clone();
            var torrent = await TryGetTorrentAsync(client, item.DownloadId, ct);
            if (torrent == null)
            {
                return result;
            }

            var savePath = DelugeResponseMapper.GetString(torrent.Value, "save_path");
            var name = DelugeResponseMapper.GetString(torrent.Value, "name");
            var contentPath = DelugeResponseMapper.BuildOutputPath(savePath, name);
            if (!string.IsNullOrWhiteSpace(contentPath))
            {
                result.OutputPath = contentPath;
            }

            return result;
        }

        public async Task<QueueItem> GetImportItemAsync(DownloadClientConfiguration client, Download download, QueueItem queueItem, CancellationToken ct = default)
        {
            var result = queueItem.Clone();
            var externalId = download.GetExternalId();
            if (string.IsNullOrWhiteSpace(externalId))
            {
                externalId = queueItem.Id;
            }

            var torrent = await TryGetTorrentAsync(client, externalId, ct);
            if (torrent == null)
            {
                return result;
            }

            var savePath = DelugeResponseMapper.GetString(torrent.Value, "save_path");
            var name = DelugeResponseMapper.GetString(torrent.Value, "name");
            var contentPath = DelugeResponseMapper.BuildOutputPath(savePath, name);
            if (!string.IsNullOrWhiteSpace(contentPath))
            {
                result.ContentPath = contentPath;
                result.RemotePath = savePath;
            }

            result.SourceFiles = DelugeResponseMapper.BuildSourceFiles(savePath, torrent.Value);
            result.Id = externalId;
            result.Title = string.IsNullOrWhiteSpace(name) ? result.Title : name;

            logger.LogDebug("Resolved Deluge import item for {TorrentId}: {ContentPath}", externalId, contentPath);
            return result;
        }

        private async Task<JsonElement?> TryGetTorrentAsync(DownloadClientConfiguration client, string? torrentId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(torrentId))
            {
                return null;
            }

            try
            {
                await rpcClient.AuthenticateAsync(client, ct);
                await rpcClient.EnsureDaemonConnectedAsync(client, ct);
                var response = await rpcClient.InvokeAsync(client, "web.update_ui", [DelugeResponseMapper.StatusKeys, new Dictionary<string, object>()], ct);
                if (!response.TryGetProperty("torrents", out var torrents) || torrents.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                foreach (var torrent in torrents.EnumerateObject())
                {
                    if (string.Equals(torrent.Name, torrentId, StringComparison.OrdinalIgnoreCase))
                    {
                        return torrent.Value.Clone();
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Error resolving import item for Deluge torrent {TorrentId}", torrentId);
            }

            return null;
        }
    }
}

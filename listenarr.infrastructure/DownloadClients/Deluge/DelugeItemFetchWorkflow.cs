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
    internal sealed class DelugeItemFetchWorkflow(DelugeRpcClient rpcClient, ILogger logger)
    {
        public async Task<List<DownloadClientItem>> GetItemsAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            var items = new List<DownloadClientItem>();
            if (client == null) return items;

            var configuredCategory = DownloadClientCategoryFilter.GetConfiguredCategory(client);
            try
            {
                await rpcClient.AuthenticateAsync(client, ct);
                await rpcClient.EnsureDaemonConnectedAsync(client, ct);
                var response = await rpcClient.InvokeAsync(client, "web.update_ui", [DelugeResponseMapper.StatusKeys, new Dictionary<string, object>()], ct);
                if (!response.TryGetProperty("torrents", out var torrents) || torrents.ValueKind != JsonValueKind.Object)
                {
                    return items;
                }

                foreach (var torrent in torrents.EnumerateObject())
                {
                    var label = DelugeResponseMapper.GetString(torrent.Value, "label");
                    if (!DownloadClientCategoryFilter.Matches(configuredCategory, label))
                    {
                        continue;
                    }

                    items.Add(DelugeResponseMapper.MapDownloadClientItem(client, torrent.Name, torrent.Value));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Failed to retrieve Deluge items for client {ClientName}", LogRedaction.SanitizeText(client.Name ?? client.Id));
            }

            return items;
        }
    }
}

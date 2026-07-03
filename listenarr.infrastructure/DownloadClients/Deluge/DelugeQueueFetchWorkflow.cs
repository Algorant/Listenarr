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
    internal sealed class DelugeQueueFetchWorkflow(DelugeRpcClient rpcClient, ILogger logger)
    {
        public async Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, List<string> ids, CancellationToken ct = default)
        {
            var items = new List<QueueItem>();
            if (client == null) return items;

            var isMonitorPoll = ids.Count > 0;
            var configuredCategory = DownloadClientCategoryFilter.GetConfiguredCategory(client);

            try
            {
                await rpcClient.AuthenticateAsync(client, ct);
                await rpcClient.EnsureDaemonConnectedAsync(client, ct);
                var response = await rpcClient.InvokeAsync(client, "web.update_ui", [DelugeResponseMapper.StatusKeys, new Dictionary<string, object>()], ct);
                if (!response.TryGetProperty("torrents", out var torrents) || torrents.ValueKind != JsonValueKind.Object)
                {
                    var message = $"Deluge returned an invalid queue response for client {LogRedaction.SanitizeText(client.Name ?? client.Id)}.";
                    logger.LogWarning("Deluge returned an invalid queue response for client {ClientName}", LogRedaction.SanitizeText(client.Name ?? client.Id));
                    if (isMonitorPoll)
                    {
                        throw new DownloadClientAdapterPollingException(message);
                    }
                    return items;
                }

                foreach (var torrent in torrents.EnumerateObject())
                {
                    try
                    {
                        var label = DelugeResponseMapper.GetString(torrent.Value, "label");
                        if (!DownloadClientCategoryFilter.Matches(configuredCategory, label))
                        {
                            continue;
                        }

                        items.Add(DelugeResponseMapper.MapQueueItem(client, torrent.Name, torrent.Value));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        logger.LogDebug(ex, "Failed to map Deluge torrent entry (non-fatal)");
                    }
                }
            }
            catch (DownloadClientAdapterPollingException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Failed to retrieve Deluge queue for client {ClientName}", LogRedaction.SanitizeText(client.Name ?? client.Id));
                if (isMonitorPoll)
                {
                    throw new DownloadClientAdapterPollingException("Error polling Deluge queue.", ex);
                }
            }

            return FilterByIds(items, ids);
        }

        private static List<QueueItem> FilterByIds(List<QueueItem> items, List<string> ids)
        {
            if (ids.Count == 0)
            {
                return items;
            }

            var idSet = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return [.. items.Where(item => !string.IsNullOrWhiteSpace(item.Id) && idSet.Contains(item.Id))];
        }
    }
}

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
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.DownloadClients.Deluge
{
    internal static class DelugeResponseMapper
    {
        public static readonly string[] StatusKeys =
        [
            "name", "total_size", "total_done", "progress", "download_payload_rate", "eta", "state",
            "save_path", "label", "ratio", "num_seeds", "num_peers", "time_added", "files", "message"
        ];

        public static QueueItem MapQueueItem(DownloadClientConfiguration client, string torrentId, JsonElement torrent)
        {
            var title = GetString(torrent, "name");
            var total = GetInt64(torrent, "total_size");
            var done = GetInt64(torrent, "total_done");
            var savePath = GetString(torrent, "save_path");
            var label = GetString(torrent, "label");
            var progress = GetDouble(torrent, "progress");
            var eta = GetInt32(torrent, "eta");
            var added = GetDouble(torrent, "time_added");
            var contentPath = BuildOutputPath(savePath, title);

            return new QueueItem
            {
                Id = torrentId,
                Title = title,
                Quality = string.IsNullOrWhiteSpace(label) ? "Unknown" : label,
                Status = MapQueueStatus(GetString(torrent, "state"), total, done),
                Progress = progress,
                Size = total,
                Downloaded = Math.Max(0, done),
                DownloadSpeed = GetDouble(torrent, "download_payload_rate"),
                Eta = eta >= 0 ? eta : null,
                DownloadClient = client.Name ?? client.Id ?? "Deluge",
                DownloadClientId = client.Id ?? string.Empty,
                DownloadClientType = DownloadClientTypes.Deluge,
                AddedAt = FromUnix(added),
                ErrorMessage = GetString(torrent, "message"),
                Seeders = GetInt32(torrent, "num_seeds"),
                Leechers = GetInt32(torrent, "num_peers"),
                Ratio = GetDouble(torrent, "ratio"),
                CanPause = true,
                CanRemove = true,
                RemotePath = savePath,
                ContentPath = contentPath,
                SourceFiles = BuildSourceFiles(savePath, torrent)
            };
        }

        public static DownloadClientItem MapDownloadClientItem(DownloadClientConfiguration client, string torrentId, JsonElement torrent)
        {
            var title = GetString(torrent, "name");
            var total = GetInt64(torrent, "total_size");
            var done = GetInt64(torrent, "total_done");
            var progress = GetDouble(torrent, "progress");
            var eta = GetInt32(torrent, "eta");
            var savePath = GetString(torrent, "save_path");
            var label = GetString(torrent, "label");
            var removeCompletedDownloads = !string.Equals(client.RemoveCompletedDownloads, "none", StringComparison.OrdinalIgnoreCase);

            return new DownloadClientItem
            {
                DownloadId = torrentId,
                Title = title,
                Category = label,
                TotalSize = total,
                RemainingSize = Math.Max(0, total - done),
                RemainingTime = eta >= 0 ? TimeSpan.FromSeconds(eta) : null,
                OutputPath = BuildOutputPath(savePath, title),
                Status = MapDownloadItemStatus(GetString(torrent, "state"), total, done),
                Message = GetString(torrent, "message"),
                Progress = progress,
                DownloadSpeed = GetDouble(torrent, "download_payload_rate"),
                SeedRatio = GetDouble(torrent, "ratio"),
                Seeders = GetInt32(torrent, "num_seeds"),
                Leechers = GetInt32(torrent, "num_peers"),
                AddedAt = FromUnix(GetDouble(torrent, "time_added")),
                CanBeRemoved = true,
                CanMoveFiles = false,
                DownloadClientInfo = DownloadClientItemClientInfo.FromClient(
                    client.Id,
                    client.Name,
                    DownloadClientTypes.Deluge,
                    DownloadProtocol.Torrent,
                    removeCompletedDownloads,
                    hasPostImportCategory: false)
            };
        }

        public static string MapQueueStatus(string state, long totalSize, long totalDone)
            => MapDownloadItemStatus(state, totalSize, totalDone).ToString().ToLowerInvariant();

        public static DownloadItemStatus MapDownloadItemStatus(string state, long totalSize, long totalDone)
        {
            var normalized = (state ?? string.Empty).Trim().ToLowerInvariant();
            var reliablyComplete = totalSize > 0 && totalDone >= totalSize;

            return normalized switch
            {
                "seeding" or "finished" => DownloadItemStatus.Completed,
                "paused" when reliablyComplete => DownloadItemStatus.Completed,
                "paused" => DownloadItemStatus.Paused,
                "queued" when reliablyComplete => DownloadItemStatus.Completed,
                "queued" => DownloadItemStatus.Queued,
                "downloading" or "downloading metadata" => DownloadItemStatus.Downloading,
                "error" => DownloadItemStatus.Failed,
                "checking" => DownloadItemStatus.Checking,
                _ => DownloadItemStatus.Unknown
            };
        }

        public static string BuildOutputPath(string savePath, string name)
        {
            savePath = savePath.TrimEnd('/', '\\');
            if (string.IsNullOrWhiteSpace(savePath)) return string.Empty;
            return string.IsNullOrWhiteSpace(name) ? savePath : FileUtils.CombineWithOptionalBase(savePath, name);
        }

        public static List<string>? BuildSourceFiles(string savePath, JsonElement torrent)
        {
            if (!torrent.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var sourceFiles = new List<string>();
            foreach (var file in files.EnumerateArray())
            {
                var path = GetString(file, "path");
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                sourceFiles.Add(FileUtils.CombineWithOptionalBase(savePath, path));
            }

            return sourceFiles.Count > 0 ? sourceFiles : null;
        }

        public static string GetString(JsonElement element, string key)
            => element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

        private static int GetInt32(JsonElement element, string key)
            => element.TryGetProperty(key, out var value) && value.TryGetInt32(out var result) ? result : 0;

        private static long GetInt64(JsonElement element, string key)
            => element.TryGetProperty(key, out var value) && value.TryGetInt64(out var result) ? result : 0;

        private static double GetDouble(JsonElement element, string key)
            => element.TryGetProperty(key, out var value) && value.TryGetDouble(out var result) ? result : 0d;

        private static DateTime FromUnix(double seconds)
            => seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)seconds).UtcDateTime : DateTime.UtcNow;
    }
}

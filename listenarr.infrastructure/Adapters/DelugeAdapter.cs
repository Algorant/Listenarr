/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Listenarr.Application.Downloads;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Security;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Torrents;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Adapters
{
    /// <summary>
    /// Deluge Web JSON-RPC adapter, modelled after the Servarr Deluge integration.
    /// </summary>
    public class DelugeAdapter : IDownloadClientAdapter
    {
        public string ClientId => "deluge";
        public string ClientType => "deluge";
        public DownloadProtocol Protocol => DownloadProtocol.Torrent;

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ITorrentFileDownloader _torrentFileDownloader;
        private readonly ILogger<DelugeAdapter> _logger;

        private static readonly string[] StatusKeys =
        [
            "name", "total_size", "total_done", "progress", "download_payload_rate", "eta", "state",
            "save_path", "label", "ratio", "num_seeds", "num_peers", "time_added", "files", "message",
            "is_finished", "is_auto_managed", "stop_at_ratio", "stop_ratio"
        ];

        public DelugeAdapter(IHttpClientFactory httpClientFactory, ITorrentFileDownloader torrentFileDownloader, ILogger<DelugeAdapter> logger)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _torrentFileDownloader = torrentFileDownloader ?? throw new ArgumentNullException(nameof(torrentFileDownloader));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<(bool Success, string Message)> TestConnectionAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            try
            {
                using var http = _httpClientFactory.CreateClient(ClientType);
                await AuthenticateAsync(http, client, ct);
                await EnsureDaemonConnectedAsync(http, client, ct);
                var connected = await RpcAsync(http, client, "web.connected", [], ct);
                if (connected.ValueKind == JsonValueKind.True)
                    return (true, "Deluge: connected to Web UI and daemon");
                return (false, "Deluge: unexpected JSON-RPC response");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogDebug(ex, "Deluge authentication failed for client {ClientId}", LogRedaction.SanitizeText(client?.Id ?? client?.Name ?? client?.Type));
                return (false, "Deluge: authentication failed (check Web UI password)");
            }
            catch (TaskCanceledException)
            {
                return (false, "Deluge: connection timed out");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Deluge test failed for client {ClientId}", LogRedaction.SanitizeText(client?.Id ?? client?.Name ?? client?.Type));
                return (false, $"Deluge: connection failed ({ex.Message})");
            }
        }

        public async Task<string?> AddAsync(DownloadClientConfiguration client, SearchResult result, CancellationToken ct = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (result == null) throw new ArgumentNullException(nameof(result));

            using var http = _httpClientFactory.CreateClient(ClientType);
            await AuthenticateAsync(http, client, ct);
            await EnsureDaemonConnectedAsync(http, client, ct);

            var options = BuildTorrentOptions(client);
            string? id = null;
            var magnet = DownloadClientUriBuilder.NormalizeMagnetLink(result.MagnetLink);
            var torrentUrl = NormalizeTorrentUrl(result.TorrentUrl);
            var bytes = result.TorrentFileContent;

            if ((bytes == null || bytes.Length == 0) && !string.IsNullOrWhiteSpace(torrentUrl) && !torrentUrl.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                var downloaded = await _torrentFileDownloader.DownloadAsync(torrentUrl, ct);
                if (downloaded.HasBytes) bytes = downloaded.TorrentBytes;
                else if (downloaded.HasMagnet) magnet = DownloadClientUriBuilder.NormalizeMagnetLink(downloaded.MagnetUri);
            }

            if (bytes != null && bytes.Length > 0)
            {
                var filename = SanitizeTorrentFileName(result.Title) + ".torrent";
                var res = await RpcAsync(http, client, "core.add_torrent_file", [filename, Convert.ToBase64String(bytes), options], ct);
                id = res.ValueKind == JsonValueKind.String ? res.GetString() : null;
            }
            else
            {
                var uri = !string.IsNullOrWhiteSpace(magnet) ? magnet : torrentUrl;
                if (string.IsNullOrWhiteSpace(uri)) throw new ArgumentException("No magnet link, torrent URL, or cached torrent file provided", nameof(result));

                if (uri.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                {
                    var res = await RpcAsync(http, client, "core.add_torrent_magnet", [uri, options], ct);
                    id = res.ValueKind == JsonValueKind.String ? res.GetString() : TryExtractHashFromMagnet(uri);
                }
                else
                {
                    var tempPath = await RpcAsync(http, client, "web.download_torrent_from_url", [uri], ct);
                    var path = tempPath.GetString();
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        var res = await RpcAsync(http, client, "web.add_torrents", [new[] { new { path, options } }], ct);
                        id = TryExtractAddedId(res);
                    }
                }
            }

            var category = GetSetting(client, "category");
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(category))
            {
                var labelSet = await TrySetLabelAsync(http, client, id, category, ct);
                if (!labelSet)
                {
                    _logger.LogWarning(
                        "Deluge torrent {TorrentId} was added but configured category/label {Label} could not be applied. " +
                        "Tracked downloads are still matched by hash, but the general queue view may hide this torrent until the label exists.",
                        LogRedaction.SanitizeText(id),
                        LogRedaction.SanitizeText(category));
                }
            }

            return id;
        }

        public async Task<bool> RemoveAsync(DownloadClientConfiguration client, string id, bool deleteFiles = false, CancellationToken ct = default)
        {
            using var http = _httpClientFactory.CreateClient(ClientType);
            await AuthenticateAsync(http, client, ct);
            await EnsureDaemonConnectedAsync(http, client, ct);
            var res = await RpcAsync(http, client, "core.remove_torrent", [id, deleteFiles], ct);
            return res.ValueKind == JsonValueKind.True || res.ValueKind == JsonValueKind.Null || res.ValueKind == JsonValueKind.Undefined;
        }

        public async Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, CancellationToken ct = default)
            => (await GetTorrentSnapshotsAsync(client, applyCategoryFilter: true, ct)).Select(s => s.ToQueueItem()).ToList();

        public async Task<List<DownloadClientItem>> GetItemsAsync(DownloadClientConfiguration client, CancellationToken ct = default)
            => (await GetTorrentSnapshotsAsync(client, applyCategoryFilter: true, ct)).Select(s => s.Item).ToList();

        public async Task<List<(string Id, string Name)>> GetRecentHistoryAsync(DownloadClientConfiguration client, int limit = 100, CancellationToken ct = default)
            => (await GetItemsAsync(client, ct)).Where(i => i.Status == DownloadItemStatus.Completed).Take(limit).Select(i => (i.DownloadId, i.Title)).ToList();

        public async Task<DownloadClientItem> GetImportItemAsync(DownloadClientConfiguration client, DownloadClientItem item, DownloadClientItem? previousAttempt = null, CancellationToken ct = default)
        {
            var current = (await GetTorrentSnapshotsAsync(client, applyCategoryFilter: false, ct))
                .FirstOrDefault(i => string.Equals(i.Item.DownloadId, item.DownloadId, StringComparison.OrdinalIgnoreCase));
            return current?.Item ?? item;
        }

        public async Task<QueueItem> GetImportItemAsync(DownloadClientConfiguration client, Download download, QueueItem queueItem, QueueItem? previousAttempt = null, CancellationToken ct = default)
        {
            var snapshots = await GetTorrentSnapshotsAsync(client, applyCategoryFilter: false, ct);
            var current = FindSnapshotForDownload(download, queueItem, snapshots);
            return current?.ToQueueItem() ?? queueItem;
        }

        public async Task<List<Download>> FetchDownloadsAsync(DownloadClientConfiguration client, List<Download> downloads, CancellationToken cancellationToken = default)
        {
            var snapshots = await GetTorrentSnapshotsAsync(client, applyCategoryFilter: false, cancellationToken);
            foreach (var d in downloads)
            {
                var snapshot = FindSnapshotForDownload(d, null, snapshots);
                if (snapshot == null)
                {
                    continue;
                }

                var item = snapshot.Item;
                d.Progress = (decimal)item.Progress;
                d.TotalSize = item.TotalSize;
                d.DownloadedSize = Math.Max(0, item.TotalSize - item.RemainingSize);
                d.DownloadPath = item.OutputPath;
                d.Metadata ??= new Dictionary<string, object>();
                d.Metadata["CanBeRemoved"] = item.CanBeRemoved;
                d.Metadata["CanMoveFiles"] = item.CanMoveFiles;

                if (d.Status is DownloadStatus.Moved or DownloadStatus.Processing or DownloadStatus.ImportPending)
                    continue;
                if (item.Status == DownloadItemStatus.Completed) d.Completed();
                else if (item.Status == DownloadItemStatus.Failed) d.Status = DownloadStatus.Failed;
                else if (item.Status == DownloadItemStatus.Paused) d.Status = DownloadStatus.Paused;
                else d.Status = DownloadStatus.Downloading;
            }
            return downloads;
        }

        private async Task<List<DelugeTorrentSnapshot>> GetTorrentSnapshotsAsync(
            DownloadClientConfiguration client,
            bool applyCategoryFilter,
            CancellationToken ct)
        {
            using var http = _httpClientFactory.CreateClient(ClientType);
            await AuthenticateAsync(http, client, ct);
            await EnsureDaemonConnectedAsync(http, client, ct);
            var res = await RpcAsync(http, client, "web.update_ui", [StatusKeys, new Dictionary<string, object>()], ct);
            return ParseTorrentSnapshots(client, res, applyCategoryFilter);
        }

        private static List<DelugeTorrentSnapshot> ParseTorrentSnapshots(
            DownloadClientConfiguration client,
            JsonElement res,
            bool applyCategoryFilter)
        {
            var list = new List<DelugeTorrentSnapshot>();
            var configuredCategory = DownloadClientCategoryFilter.GetConfiguredCategory(client);
            if (!res.TryGetProperty("torrents", out var torrents) || torrents.ValueKind != JsonValueKind.Object) return list;

            foreach (var torrent in torrents.EnumerateObject())
            {
                var t = torrent.Value;
                var label = GetString(t, "label");
                if (applyCategoryFilter && !DownloadClientCategoryFilter.Matches(configuredCategory, label)) continue;

                var total = GetLong(t, "total_size");
                var done = GetLong(t, "total_done");
                var status = MapStatus(GetString(t, "state"), GetDouble(t, "progress"));
                var removeCompletedDownloads = !string.IsNullOrWhiteSpace(client.RemoveCompletedDownloads) &&
                    !string.Equals(client.RemoveCompletedDownloads, "none", StringComparison.OrdinalIgnoreCase);

                var item = new DownloadClientItem
                {
                    DownloadId = torrent.Name,
                    Title = GetString(t, "name"),
                    Category = label,
                    TotalSize = total,
                    RemainingSize = Math.Max(0, total - done),
                    RemainingTime = BuildRemainingTime(t),
                    OutputPath = BuildOutputPath(t),
                    Status = status,
                    Message = GetString(t, "message"),
                    Progress = GetDouble(t, "progress"),
                    DownloadSpeed = GetDouble(t, "download_payload_rate"),
                    SeedRatio = GetDouble(t, "ratio"),
                    Seeders = (int)GetLong(t, "num_seeds"),
                    Leechers = (int)GetLong(t, "num_peers"),
                    AddedAt = FromUnix(GetDouble(t, "time_added")),
                    CanBeRemoved = removeCompletedDownloads && status == DownloadItemStatus.Completed,
                    CanMoveFiles = false,
                    DownloadClientInfo = DownloadClientItemClientInfo.FromClient(client.Id, client.Name, client.Type, DownloadProtocol.Torrent, removeCompletedDownloads, false)
                };

                list.Add(new DelugeTorrentSnapshot(item, BuildSourceFiles(t)));
            }

            return list;
        }

        private static DelugeTorrentSnapshot? FindSnapshotForDownload(
            Download download,
            QueueItem? queueItem,
            IEnumerable<DelugeTorrentSnapshot> snapshots)
        {
            var snapshotList = snapshots.ToList();
            var candidates = GetDownloadIdCandidates(download, queueItem).ToList();

            foreach (var candidate in candidates)
            {
                var match = snapshotList.FirstOrDefault(s => string.Equals(s.Item.DownloadId, candidate, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    return match;
                }
            }

            var titles = new[] { download.Title, queueItem?.Title }
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (titles.Count == 0)
            {
                return null;
            }

            var exactTitleMatches = snapshotList
                .Where(s => titles.Any(t => string.Equals(s.Item.Title?.Trim(), t, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            return exactTitleMatches.Count == 1 ? exactTitleMatches[0] : null;
        }

        private static IEnumerable<string> GetDownloadIdCandidates(Download download, QueueItem? queueItem)
        {
            if (download == null)
            {
                yield break;
            }

            var values = new[]
            {
                download.GetExternalId(),
                download.GetMetadataString("ClientDownloadId"),
                download.GetMetadataString("TorrentHash"),
                queueItem?.Id
            };

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value) || string.Equals(value, download.Id, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (seen.Add(value))
                {
                    yield return value;
                }
            }
        }

        private static string BuildBaseUrl(DownloadClientConfiguration client)
        {
            var scheme = client.UseSSL ? "https" : "http";
            var host = client.Host.Trim().TrimEnd('/');
            if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                host = new Uri(host).Authority;
            var urlBase = GetSetting(client, "urlBase") ?? GetSetting(client, "UrlBase") ?? string.Empty;
            urlBase = urlBase.Trim('/');
            return string.IsNullOrWhiteSpace(urlBase) ? $"{scheme}://{host}:{client.Port}/json" : $"{scheme}://{host}:{client.Port}/{urlBase}/json";
        }

        private async Task AuthenticateAsync(HttpClient http, DownloadClientConfiguration client, CancellationToken ct)
        {
            var res = await RpcAsync(http, client, "auth.login", [client.Password ?? string.Empty], ct);
            if (res.ValueKind != JsonValueKind.True) throw new UnauthorizedAccessException("Failed to authenticate with Deluge Web UI");
        }

        private async Task EnsureDaemonConnectedAsync(HttpClient http, DownloadClientConfiguration client, CancellationToken ct)
        {
            var connected = await RpcAsync(http, client, "web.connected", [], ct);
            if (connected.ValueKind == JsonValueKind.True) return;

            var hosts = await RpcAsync(http, client, "web.get_hosts", [], ct);
            if (hosts.ValueKind != JsonValueKind.Array || hosts.GetArrayLength() == 0)
                throw new InvalidOperationException("Deluge Web is not connected to a daemon and no daemon hosts are configured");

            string? hostId = null;
            foreach (var host in hosts.EnumerateArray())
            {
                if (host.ValueKind == JsonValueKind.Array && host.GetArrayLength() > 0)
                {
                    hostId = host[0].GetString();
                    if (!string.IsNullOrWhiteSpace(hostId)) break;
                }
            }

            if (string.IsNullOrWhiteSpace(hostId))
                throw new InvalidOperationException("Deluge Web returned no daemon host id");

            await RpcAsync(http, client, "web.connect", [hostId], ct);
        }

        private async Task<JsonElement> RpcAsync(HttpClient http, DownloadClientConfiguration client, string method, object[] parameters, CancellationToken ct)
        {
            var payload = JsonSerializer.Serialize(new { method, @params = parameters, id = 1 });
            using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(payload));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await http.PostAsync(BuildBaseUrl(client), content, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden) throw new UnauthorizedAccessException("Deluge rejected the request");
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                throw new InvalidOperationException($"Deluge JSON-RPC error calling {method}: {error}");
            if (doc.RootElement.TryGetProperty("result", out var result)) return result.Clone();
            return default;
        }

        private static Dictionary<string, object> BuildTorrentOptions(DownloadClientConfiguration client)
        {
            var options = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(client.DownloadPath)) options["download_location"] = client.DownloadPath;
            var addPaused = GetSetting(client, "addPaused") ?? GetSetting(client, "AddPaused");
            if (bool.TryParse(addPaused, out var paused)) options["add_paused"] = paused;
            return options;
        }

        private async Task<bool> TrySetLabelAsync(HttpClient http, DownloadClientConfiguration client, string id, string label, CancellationToken ct)
        {
            try
            {
                await EnsureLabelExistsAsync(http, client, label, ct);
                await RpcAsync(http, client, "label.set_torrent", [id, label], ct);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(
                    ex,
                    "Unable to set Deluge label/category {Label} on torrent {TorrentId}. Is the Label plugin enabled?",
                    LogRedaction.SanitizeText(label),
                    LogRedaction.SanitizeText(id));
                return false;
            }
        }

        private async Task EnsureLabelExistsAsync(HttpClient http, DownloadClientConfiguration client, string label, CancellationToken ct)
        {
            var labels = await RpcAsync(http, client, "label.get_labels", [], ct);
            if (ContainsLabel(labels, label))
            {
                return;
            }

            await RpcAsync(http, client, "label.add", [label], ct);
        }

        private static bool ContainsLabel(JsonElement labels, string label)
        {
            if (labels.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            return labels.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null)
                .Any(existing => string.Equals(existing, label, StringComparison.OrdinalIgnoreCase));
        }

        private static DownloadItemStatus MapStatus(string state, double progress)
        {
            var normalizedState = (state ?? string.Empty).Trim().ToLowerInvariant();
            var payloadComplete = progress >= 100.0;

            return normalizedState switch
            {
                "seeding" or "finished" => DownloadItemStatus.Completed,
                "paused" when payloadComplete => DownloadItemStatus.Completed,
                "paused" => DownloadItemStatus.Paused,
                "queued" when payloadComplete => DownloadItemStatus.Completed,
                "queued" => DownloadItemStatus.Queued,
                "downloading" or "downloading metadata" => DownloadItemStatus.Downloading,
                "error" => DownloadItemStatus.Failed,
                "checking" => DownloadItemStatus.Checking,
                _ => payloadComplete ? DownloadItemStatus.Completed : DownloadItemStatus.Unknown
            };
        }

        private static string BuildOutputPath(JsonElement t)
        {
            var savePath = GetString(t, "save_path");
            var name = GetString(t, "name");
            if (string.IsNullOrWhiteSpace(savePath)) return string.Empty;
            return string.IsNullOrWhiteSpace(name) ? savePath : CombineDelugePath(savePath, name);
        }

        private static List<string> BuildSourceFiles(JsonElement t)
        {
            var savePath = GetString(t, "save_path");
            if (string.IsNullOrWhiteSpace(savePath) || !t.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return files.EnumerateArray()
                .Select(file => GetString(file, "path"))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => CombineDelugePath(savePath, path))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string CombineDelugePath(string? basePath, string? candidatePath)
        {
            var candidate = (candidatePath ?? string.Empty).Trim().Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }

            if (candidate.StartsWith("/", StringComparison.Ordinal) || candidate.Contains(":/", StringComparison.Ordinal))
            {
                return candidate;
            }

            var normalizedBase = (basePath ?? string.Empty).TrimEnd('/', '\\');
            if (string.IsNullOrWhiteSpace(normalizedBase))
            {
                return candidate.TrimStart('/', '\\');
            }

            return normalizedBase + "/" + candidate.TrimStart('/', '\\');
        }

        private static TimeSpan? BuildRemainingTime(JsonElement t)
        {
            var eta = GetLong(t, "eta");
            if (eta < 0)
            {
                return null;
            }

            try
            {
                return TimeSpan.FromSeconds(eta);
            }
            catch (OverflowException)
            {
                return TimeSpan.MaxValue;
            }
        }

        private static string ToQueueStatus(DownloadItemStatus status) => status.ToString().ToLowerInvariant();
        private static string? GetSetting(DownloadClientConfiguration c, string key) => c.Settings != null && c.Settings.TryGetValue(key, out var v) ? v?.ToString() : null;
        private static string GetString(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
        private static long GetLong(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.TryGetInt64(out var l) ? l : 0;
        private static double GetDouble(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.TryGetDouble(out var d) ? d : 0;
        private static DateTime FromUnix(double seconds) => seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)seconds).UtcDateTime : DateTime.UtcNow;
        private static string? NormalizeTorrentUrl(string? url) => string.IsNullOrWhiteSpace(url) ? null : url.Trim();
        private static string SanitizeTorrentFileName(string? title) => string.Join("_", (title ?? "listenarr").Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        private static string? TryExtractHashFromMagnet(string magnet) { var m = System.Text.RegularExpressions.Regex.Match(magnet, @"btih:([A-Fa-f0-9]{40})"); return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null; }
        private static string? TryExtractAddedId(JsonElement res) => res.ValueKind == JsonValueKind.Array && res.GetArrayLength() > 0 ? res[0].GetString() : null;

        private sealed record DelugeTorrentSnapshot(DownloadClientItem Item, List<string> SourceFiles)
        {
            public QueueItem ToQueueItem() => new()
            {
                Id = Item.DownloadId,
                Title = Item.Title,
                Status = ToQueueStatus(Item.Status),
                Progress = Item.Progress,
                Size = Item.TotalSize,
                Downloaded = Math.Max(0, Item.TotalSize - Item.RemainingSize),
                DownloadSpeed = Item.DownloadSpeed,
                Eta = Item.RemainingTime.HasValue ? (int)Math.Min(int.MaxValue, Item.RemainingTime.Value.TotalSeconds) : null,
                DownloadClient = Item.DownloadClientInfo.Name,
                DownloadClientId = Item.DownloadClientInfo.Id,
                DownloadClientType = Item.DownloadClientInfo.Type,
                AddedAt = Item.AddedAt,
                ErrorMessage = Item.Message,
                Seeders = Item.Seeders,
                Leechers = Item.Leechers,
                Ratio = Item.SeedRatio,
                RemotePath = Item.OutputPath,
                ContentPath = Item.OutputPath,
                SourceFiles = SourceFiles.Count > 0 ? new List<string>(SourceFiles) : null,
                CanRemove = Item.CanBeRemoved
            };
        }
    }
}

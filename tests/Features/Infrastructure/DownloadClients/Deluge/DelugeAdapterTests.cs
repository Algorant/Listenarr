/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using System.Net;
using System.Text;
using System.Text.Json;
using Listenarr.Infrastructure.DownloadClients.Deluge;
using Listenarr.Infrastructure.Torrents;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.DownloadClients.Deluge
{
    [Trait("Name", "DelugeAdapterTests")]
    [Trait("Category", "DownloadClientAdapter")]
    [Trait("Third-Party", "Deluge")]
    public class DelugeAdapterTests
    {
        [Theory]
        [InlineData("Seeding", 100.0, DownloadItemStatus.Completed, "completed")]
        [InlineData("Paused", 100.0, DownloadItemStatus.Completed, "completed")]
        [InlineData("Queued", 100.0, DownloadItemStatus.Completed, "completed")]
        [InlineData("Downloading", 42.5, DownloadItemStatus.Downloading, "downloading")]
        [InlineData("Downloading Metadata", 0.0, DownloadItemStatus.Downloading, "downloading")]
        [InlineData("Checking", 100.0, DownloadItemStatus.Checking, "checking")]
        [InlineData("Error", 0.0, DownloadItemStatus.Failed, "failed")]
        public async Task GetItemsAndQueue_MapDelugeStatesToNormalizedStatuses(string state, double progress, DownloadItemStatus itemStatus, string queueStatus)
        {
            var adapter = CreateAdapter(BuildUpdateUiResponse(state, progress, "listenarr"));
            var client = CreateClient(category: "listenarr");

            var items = await adapter.GetItemsAsync(client, CancellationToken.None);
            var queue = await adapter.GetQueueAsync(client, CancellationToken.None);

            Assert.Single(items);
            Assert.Equal(itemStatus, items[0].Status);
            Assert.Equal("ABCDEF1234567890", items[0].DownloadId);
            Assert.Equal("Book.m4b", items[0].Title);
            Assert.Equal("/downloads/Book.m4b", items[0].OutputPath);
            Assert.Single(queue);
            Assert.Equal(queueStatus, queue[0].Status);
            Assert.Equal("/downloads", queue[0].RemotePath);
            Assert.Equal("/downloads/Book.m4b", queue[0].ContentPath);
        }

        [Fact]
        public async Task GetItemsAndQueue_DoNotCompleteUnknownStateFromProgressAlone()
        {
            var adapter = CreateAdapter(BuildUpdateUiResponse("Mystery", 100.0, "listenarr", totalSize: 0, totalDone: 0));
            var client = CreateClient(category: "listenarr");

            var items = await adapter.GetItemsAsync(client, CancellationToken.None);
            var queue = await adapter.GetQueueAsync(client, CancellationToken.None);

            Assert.Single(items);
            Assert.Equal(DownloadItemStatus.Unknown, items[0].Status);
            Assert.Single(queue);
            Assert.Equal("unknown", queue[0].Status);
        }

        [Fact]
        public async Task GetItemsAndQueue_DoNotCompleteUnknownStateFromBytesAlone()
        {
            var adapter = CreateAdapter(BuildUpdateUiResponse("Mystery", 100.0, "listenarr", totalSize: 100, totalDone: 100));
            var client = CreateClient(category: "listenarr");

            var items = await adapter.GetItemsAsync(client, CancellationToken.None);
            var queue = await adapter.GetQueueAsync(client, CancellationToken.None);

            Assert.Single(items);
            Assert.Equal(DownloadItemStatus.Unknown, items[0].Status);
            Assert.Single(queue);
            Assert.Equal("unknown", queue[0].Status);
        }

        [Theory]
        [InlineData("Seeding")]
        [InlineData("Finished")]
        public async Task GetItemsAndQueue_ExplicitTerminalStatesComplete(string state)
        {
            var adapter = CreateAdapter(BuildUpdateUiResponse(state, 100.0, "listenarr", totalSize: 0, totalDone: 0));
            var client = CreateClient(category: "listenarr");

            var items = await adapter.GetItemsAsync(client, CancellationToken.None);
            var queue = await adapter.GetQueueAsync(client, CancellationToken.None);

            Assert.Single(items);
            Assert.Equal(DownloadItemStatus.Completed, items[0].Status);
            Assert.Single(queue);
            Assert.Equal("completed", queue[0].Status);
        }

        [Fact]
        public async Task GetItemsAndQueue_PausedOrQueuedNeedReliableBytesToComplete()
        {
            var incomplete = CreateAdapter(BuildUpdateUiResponse("Paused", 100.0, "listenarr", totalSize: 0, totalDone: 0));
            var complete = CreateAdapter(BuildUpdateUiResponse("Paused", 100.0, "listenarr", totalSize: 100, totalDone: 100));
            var client = CreateClient(category: "listenarr");

            var incompleteItems = await incomplete.GetItemsAsync(client, CancellationToken.None);
            var completeItems = await complete.GetItemsAsync(client, CancellationToken.None);

            Assert.Single(incompleteItems);
            Assert.Equal(DownloadItemStatus.Paused, incompleteItems[0].Status);
            Assert.Single(completeItems);
            Assert.Equal(DownloadItemStatus.Completed, completeItems[0].Status);
        }

        [Fact]
        public async Task GetQueueAsync_FullSnapshotIncludesConfiguredCategoryOnly()
        {
            var adapter = CreateAdapter("""
            {
              "id":1,
              "result":{
                "torrents":{
                  "HASH1":{
                    "name":"Book One","total_size":100,"total_done":100,"progress":100.0,
                    "download_payload_rate":0,"eta":0,"state":"Seeding","save_path":"/downloads",
                    "label":"listenarr","ratio":1.0,"num_seeds":1,"num_peers":0,"time_added":1700000000,"message":""
                  },
                  "HASH2":{
                    "name":"Movie One","total_size":100,"total_done":100,"progress":100.0,
                    "download_payload_rate":0,"eta":0,"state":"Seeding","save_path":"/downloads",
                    "label":"movies","ratio":1.0,"num_seeds":1,"num_peers":0,"time_added":1700000000,"message":""
                  }
                }
              },
              "error":null
            }
            """);
            var client = CreateClient(category: "listenarr");

            var items = await adapter.GetQueueAsync(client, CancellationToken.None);

            Assert.Single(items);
            Assert.Equal("HASH1", items[0].Id);
            Assert.Equal("listenarr", items[0].Quality);
        }

        [Fact]
        public async Task GetQueueAsync_WithIdsTargetsOnlyRequestedTorrentIds()
        {
            var adapter = CreateAdapter("""
            {
              "id":1,
              "result":{
                "torrents":{
                  "HASH1":{
                    "name":"Book One","total_size":100,"total_done":20,"progress":20.0,
                    "download_payload_rate":0,"eta":0,"state":"Downloading","save_path":"/downloads",
                    "label":"listenarr","ratio":0,"num_seeds":1,"num_peers":0,"time_added":1700000000,"message":""
                  },
                  "HASH2":{
                    "name":"Book Two","total_size":100,"total_done":100,"progress":100.0,
                    "download_payload_rate":0,"eta":0,"state":"Seeding","save_path":"/downloads",
                    "label":"listenarr","ratio":1.0,"num_seeds":1,"num_peers":0,"time_added":1700000000,"message":""
                  }
                }
              },
              "error":null
            }
            """);
            var client = CreateClient(category: "listenarr");

            var items = await adapter.GetQueueAsync(client, ["HASH2"], CancellationToken.None);

            Assert.Single(items);
            Assert.Equal("HASH2", items[0].Id);
            Assert.Equal("completed", items[0].Status);
        }

        [Fact]
        public async Task TestConnectionAsync_AuthenticatesAndRequiresDaemonConnection()
        {
            var adapter = CreateAdapter(BuildUpdateUiResponse("Seeding", 100.0, "listenarr"));
            var client = CreateClient(category: null);

            var (success, message) = await adapter.TestConnectionAsync(client, CancellationToken.None);

            Assert.True(success);
            Assert.Contains("daemon", message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task AddAsync_WhenMagnetProvided_ReturnsDelugeTorrentIdAndSetsLabel()
        {
            var calls = new List<string>();
            var adapter = CreateAdapter(BuildUpdateUiResponse("Seeding", 100.0, "listenarr"), calls);
            var client = CreateClient(category: "listenarr");
            var submission = PreparedSubmissionTestFactory.Torrent(new SearchResult
            {
                Title = "Book",
                MagnetLink = "magnet:?xt=urn:btih:ABCDEF1234567890ABCDEF1234567890ABCDEF12"
            });

            var result = await adapter.AddAsync(client, submission, CancellationToken.None);

            Assert.Equal("ABCDEF1234567890ABCDEF1234567890ABCDEF12", result.ExternalId);
            Assert.Contains("core.add_torrent_magnet", calls);
            Assert.Contains("label.set_torrent", calls);
        }

        [Fact]
        public async Task GetImportItemAsync_MatchesQueueItemByExternalClientIdAndSourceFiles()
        {
            var adapter = CreateAdapter(BuildUpdateUiResponse("Seeding", 100.0, "listenarr", includeFiles: true));
            var client = CreateClient(category: "listenarr");
            var download = new Download { Id = "listenarr-download-1", DownloadClientId = client.Id };
            download.SetExternalId("ABCDEF1234567890");
            var fallback = new QueueItem { Id = "listenarr-download-1", Title = "Fallback" };

            var item = await adapter.GetImportItemAsync(client, download, fallback, null, CancellationToken.None);

            Assert.Equal("ABCDEF1234567890", item.Id);
            Assert.Equal("Book.m4b", item.Title);
            Assert.Equal("/downloads/Book.m4b", item.ContentPath);
            Assert.Equal("/downloads", item.RemotePath);
            Assert.Equal(["/downloads/Book.m4b"], item.SourceFiles);
        }

        [Fact]
        public async Task GetImportItemAsync_UsesExternalTorrentIdNotListenarrDownloadId()
        {
            var adapter = CreateAdapter(BuildUpdateUiResponse("Seeding", 100.0, "listenarr"));
            var client = CreateClient(category: "listenarr");
            var download = new Download { Id = "listenarr-download-1", DownloadClientId = client.Id };
            download.SetExternalId("ABCDEF1234567890");
            var fallback = new QueueItem { Id = "listenarr-download-1", Title = "Fallback" };

            var item = await adapter.GetImportItemAsync(client, download, fallback, null, CancellationToken.None);

            Assert.Equal("ABCDEF1234567890", item.Id);
            Assert.NotEqual(download.Id, item.Id);
        }

        private static DelugeAdapter CreateAdapter(string updateUiResponse, List<string>? calls = null)
        {
            var handler = new DelegatingHandlerMock(async (request, ct) =>
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                using var document = JsonDocument.Parse(body);
                var method = document.RootElement.GetProperty("method").GetString() ?? string.Empty;
                calls?.Add(method);
                var responseBody = method switch
                {
                    "auth.login" => """
                    { "id":1, "result":true, "error":null }
                    """,
                    "web.connected" => """
                    { "id":1, "result":true, "error":null }
                    """,
                    "web.update_ui" => updateUiResponse,
                    "core.add_torrent_magnet" => """
                    { "id":1, "result":"ABCDEF1234567890ABCDEF1234567890ABCDEF12", "error":null }
                    """,
                    "label.set_torrent" => """
                    { "id":1, "result":null, "error":null }
                    """,
                    _ => """
                    { "id":1, "result":null, "error":null }
                    """
                };

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
                };
            });
            var httpFactory = new Mock<IHttpClientFactory>();
            httpFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler));

            return new DelugeAdapter(httpFactory.Object, Mock.Of<ITorrentFileDownloader>(), NullLogger<DelugeAdapter>.Instance);
        }

        private static DownloadClientConfiguration CreateClient(string? category)
        {
            var client = new DownloadClientConfiguration
            {
                Id = "deluge-1",
                Name = "Deluge",
                Type = "deluge",
                Host = "localhost",
                Port = 8112,
                Password = "deluge"
            };

            if (!string.IsNullOrWhiteSpace(category))
            {
                client.Settings = new Dictionary<string, object>
                {
                    ["category"] = category
                };
            }

            return client;
        }

        private static string BuildUpdateUiResponse(
            string state,
            double progress,
            string label,
            bool includeFiles = false,
            long totalSize = 100,
            long? totalDone = null)
            => $$"""
            {
              "id":1,
              "result":{
                "torrents":{
                  "ABCDEF1234567890":{
                    "name":"Book.m4b",
                    "total_size":{{totalSize}},
                    "total_done":{{totalDone ?? (long)Math.Round(progress)}},
                    "progress":{{progress.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
                    "download_payload_rate":25,
                    "eta":60,
                    "state":"{{state}}",
                    "save_path":"/downloads",
                    "label":"{{label}}",
                    "ratio":1.0,
                    "num_seeds":1,
                    "num_peers":0,
                    "time_added":1700000000,
                    "message":""{{(includeFiles ? ",\n                    \"files\":[{\"path\":\"Book.m4b\",\"size\":100}]" : string.Empty)}}
                  }
                }
              },
              "error":null
            }
            """;
    }
}

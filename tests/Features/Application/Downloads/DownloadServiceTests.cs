using Listenarr.Application.Downloads;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Domain.Models;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Application.Downloads
{
    public class DownloadServiceTests : BaseTests
    {
        private DownloadClientConfiguration _client = new DownloadClientConfigurationBuilder().Build();
        private Audiobook _audiobook = new AudiobookBuilder().Build();
        private Download _download = new DownloadBuilder().Build();
        private MetadataServiceMock metadataServiceMock = new MetadataServiceMock();

        public override async Task InitializeAsync()
        {
            _services.AddSingleton<IMetadataService>(metadataServiceMock);
            Init();

            await InitData();
        }

        private async Task InitData()
        {
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(Path.GetTempPath())
                .WithMetadataProcessing()
                .WithMoveFileOnCompleted()
                .Build());

            _client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfiguration
            {
                Id = "qb-1",
                Name = "local qbit",
                Type = "qbittorrent",
                Host = "localhost",
                Port = 8080,
                IsEnabled = true
            });

            _audiobook = await CreateAudiobook();

            _download.DownloadClientId = _client.Id;
            _download.AudiobookId = _audiobook.Id;
            await _downloadRepository.AddAsync(_download);
        }

        [Fact]
        public async Task SendToDownloadClientAsync_StoresMagnetHashFallback_WhenClientReturnsNoId()
        {
            var gatewayMock = new Mock<IDownloadClientGateway>();
            gatewayMock
                .Setup(g => g.AddAsync(_client, It.IsAny<SearchResult>(), It.IsAny<System.Threading.CancellationToken>()))
                .ReturnsAsync((string?)null);
            _services.AddSingleton(gatewayMock.Object);

            var queueServiceMock = new Mock<IDownloadQueueService>();
            queueServiceMock.Setup(q => q.GetQueueAsync()).ReturnsAsync(new List<QueueItem>
            {
                new QueueItem
                {
                    Id = "tracked-1",
                    Title = "Dune - Frank Herbert [M4B]",
                    Status = "completed",
                    DownloadClient = "local qbit",
                    DownloadClientId = "qb-1",
                    DownloadClientType = "qbittorrent",
                    AddedAt = DateTime.UtcNow.AddHours(-2)
                }
            });
            _services.AddSingleton(gatewayMock.Object);

            Init();
            await InitData();

            var downloadService = _provider.GetRequiredService<DownloadService>();

            var searchResult = new SearchResult
            {
                Title = "Artemis",
                Artist = "Andy Weir",
                DownloadType = "Torrent",
                MagnetLink = "magnet:?xt=urn:btih:ABCDEF1234567890ABCDEF1234567890ABCDEF12&dn=Artemis",
                Size = 123456789
            };

            var downloadId = await downloadService.SendToDownloadClientAsync(searchResult, "qb-1");
            var persisted = await _downloadRepository.GetByIdAsync(downloadId);

            Assert.NotNull(persisted);
            Assert.Equal("ABCDEF1234567890ABCDEF1234567890ABCDEF12", persisted!.Metadata["ClientDownloadId"]?.ToString());
            Assert.Equal("ABCDEF1234567890ABCDEF1234567890ABCDEF12", persisted.Metadata["TorrentHash"]?.ToString());
        }

        [Fact]
        public async Task SendToDownloadClientAsync_AutoSelectsDeluge_WhenItIsOnlyEnabledTorrentClient()
        {
            var gatewayMock = new Mock<IDownloadClientGateway>();
            _services.AddSingleton(gatewayMock.Object);

            Init();
            await InitData();

            _client.IsEnabled = false;
            await _downloadClientConfigurationRepository.SaveAsync(_client);

            var delugeClient = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfiguration
            {
                Id = "deluge-1",
                Name = "Deluge",
                Type = "deluge",
                Host = "localhost",
                Port = 8112,
                IsEnabled = true
            });

            gatewayMock
                .Setup(g => g.AddAsync(
                    It.Is<DownloadClientConfiguration>(c => c.Id == delugeClient.Id),
                    It.IsAny<SearchResult>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync("DELUGEHASH1234567890");

            var downloadService = _provider.GetRequiredService<DownloadService>();

            var searchResult = new SearchResult
            {
                Title = "Artemis",
                Artist = "Andy Weir",
                DownloadType = "Torrent",
                MagnetLink = "magnet:?xt=urn:btih:DELUGEHASH1234567890&dn=Artemis",
                Size = 123456789
            };

            var downloadId = await downloadService.SendToDownloadClientAsync(searchResult);
            var persisted = await _downloadRepository.GetByIdAsync(downloadId);

            Assert.NotNull(persisted);
            Assert.Equal("deluge-1", persisted!.DownloadClientId);
            Assert.Equal("DELUGEHASH1234567890", persisted.Metadata["ClientDownloadId"]?.ToString());
            Assert.Equal("DELUGEHASH1234567890", persisted.Metadata["TorrentHash"]?.ToString());
            gatewayMock.Verify(
                g => g.AddAsync(
                    It.Is<DownloadClientConfiguration>(c => c.Id == delugeClient.Id),
                    It.Is<SearchResult>(r => r.DownloadType == "Torrent"),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task RemoveFromQueueAsync_UsesTorrentHashForDelugeRemoval()
        {
            var gatewayMock = new Mock<IDownloadClientGateway>();
            _services.AddSingleton(gatewayMock.Object);

            Init();
            await InitData();

            var delugeClient = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfiguration
            {
                Id = "deluge-1",
                Name = "Deluge",
                Type = "deluge",
                Host = "localhost",
                Port = 8112,
                IsEnabled = true
            });

            var download = new Download
            {
                Id = "download-deluge-remove",
                DownloadClientId = delugeClient.Id,
                Title = "Deluge Book",
                Status = DownloadStatus.Downloading,
                Metadata = new Dictionary<string, object>
                {
                    ["TorrentHash"] = "DELUGEHASH-REMOVE",
                    ["ClientDownloadId"] = "DELUGEHASH-REMOVE"
                }
            };
            await _downloadRepository.AddAsync(download);

            gatewayMock
                .Setup(g => g.RemoveAsync(
                    It.Is<DownloadClientConfiguration>(c => c.Id == delugeClient.Id),
                    "DELUGEHASH-REMOVE",
                    false,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var downloadService = _provider.GetRequiredService<DownloadService>();

            var removed = await downloadService.RemoveFromQueueAsync(download.Id, delugeClient.Id);

            Assert.True(removed);
            gatewayMock.Verify(
                g => g.RemoveAsync(
                    It.Is<DownloadClientConfiguration>(c => c.Id == delugeClient.Id),
                    "DELUGEHASH-REMOVE",
                    false,
                    It.IsAny<CancellationToken>()),
                Times.Once);
            Assert.Null(await _downloadRepository.GetByIdAsync(download.Id));
        }

        [Fact]
        public async Task SendToDownloadClientAsync_DerivesTorrent_WhenRequestSpoofsDdl()
        {
            var gatewayMock = new Mock<IDownloadClientGateway>();
            gatewayMock
                .Setup(g => g.AddAsync(_client, It.IsAny<SearchResult>(), It.IsAny<System.Threading.CancellationToken>()))
                .ReturnsAsync("client-download-123");
            _services.AddSingleton(gatewayMock.Object);

            Init();
            await InitData();

            var downloadService = _provider.GetRequiredService<DownloadService>();

            var searchResult = new SearchResult
            {
                Title = "Artemis",
                Artist = "Andy Weir",
                DownloadType = "DDL",
                MagnetLink = "magnet:?xt=urn:btih:ABCDEF1234567890ABCDEF1234567890ABCDEF12&dn=Artemis",
                Size = 123456789
            };

            var downloadId = await downloadService.SendToDownloadClientAsync(searchResult, "qb-1");
            var persisted = await _downloadRepository.GetByIdAsync(downloadId);

            Assert.NotNull(persisted);
            Assert.Equal("qb-1", persisted!.DownloadClientId);
            Assert.Equal("Torrent", persisted.Metadata["DownloadType"]?.ToString());
            gatewayMock.Verify(
                g => g.AddAsync(
                    _client,
                    It.Is<SearchResult>(r => r.DownloadType == "Torrent" && r.MagnetLink.Contains("magnet:?xt=urn:btih:", StringComparison.OrdinalIgnoreCase)),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task SendToDownloadClientAsync_DerivesTrustedInternetArchiveAsDdl()
        {
            var gatewayMock = new Mock<IDownloadClientGateway>();
            _services.AddSingleton(gatewayMock.Object);

            Init();
            await InitData();

            await _indexerRepository.AddAsync(new Indexer
            {
                Id = 42,
                Name = "Internet Archive",
                Url = "https://archive.org/advancedsearch.php",
                Type = "Torrent",
                Implementation = "InternetArchive",
                IsEnabled = true
            });

            var downloadService = _provider.GetRequiredService<DownloadService>();

            var searchResult = new SearchResult
            {
                Title = "Artemis",
                Artist = "Andy Weir",
                DownloadType = "Torrent",
                IndexerId = 42,
                TorrentUrl = "https://archive.org/download/artemis_book/artemis.m4b",
                Size = 123456789,
                Source = "Internet Archive"
            };

            var downloadId = await downloadService.SendToDownloadClientAsync(searchResult, "qb-1");
            var persisted = await _downloadRepository.GetByIdAsync(downloadId);

            Assert.NotNull(persisted);
            Assert.Equal("DDL", persisted!.DownloadClientId);
            Assert.Equal("DDL", persisted.Metadata["DownloadType"]?.ToString());
            gatewayMock.Verify(
                g => g.AddAsync(It.IsAny<DownloadClientConfiguration>(), It.IsAny<SearchResult>(), It.IsAny<System.Threading.CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task UpdateAsync_WhenDownloadDoesNotExist_DoesNotPersistOrNotify()
        {
            var missingDownload = new DownloadBuilder()
                .WithId("missing-download")
                .WithStatus(DownloadStatus.Queued)
                .Build();

            var downloadRepository = new Mock<IDownloadRepository>(MockBehavior.Strict);
            downloadRepository
                .Setup(r => r.GetByIdAsync(missingDownload.Id))
                .ReturnsAsync((Download?)null);

            var notificationService = new Mock<INotificationService>(MockBehavior.Strict);

            _services.AddSingleton(downloadRepository.Object);
            _services.AddSingleton(notificationService.Object);
            Init();

            var downloadService = _provider.GetRequiredService<DownloadService>();

            await downloadService.UpdateAsync(missingDownload);

            downloadRepository.Verify(r => r.GetByIdAsync(missingDownload.Id), Times.Once);
            downloadRepository.Verify(r => r.UpdateAsync(It.IsAny<Download>()), Times.Never);
            notificationService.VerifyNoOtherCalls();
        }
    }
}

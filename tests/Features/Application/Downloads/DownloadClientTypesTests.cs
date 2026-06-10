using Listenarr.Application.Downloads;
using Listenarr.Domain.Models;
using Xunit;

namespace Listenarr.Tests.Features.Application.Downloads
{
    public class DownloadClientTypesTests
    {
        [Fact]
        public void SelectPreferredTorrentClient_PreservesExistingPreferenceAndFallsBackToDeluge()
        {
            var deluge = new DownloadClientConfiguration { Id = "deluge-1", Type = "deluge" };
            var transmission = new DownloadClientConfiguration { Id = "transmission-1", Type = "transmission" };
            var qbittorrent = new DownloadClientConfiguration { Id = "qbittorrent-1", Type = "qbittorrent" };

            Assert.Equal("deluge-1", DownloadClientTypes.SelectPreferredTorrentClient([deluge])?.Id);
            Assert.Equal("transmission-1", DownloadClientTypes.SelectPreferredTorrentClient([deluge, transmission])?.Id);
            Assert.Equal("qbittorrent-1", DownloadClientTypes.SelectPreferredTorrentClient([deluge, transmission, qbittorrent])?.Id);
        }

        [Theory]
        [InlineData("qbittorrent")]
        [InlineData("transmission")]
        [InlineData("deluge")]
        public void IsTorrentHashClient_IncludesAllTorrentClients(string clientType)
        {
            Assert.True(DownloadClientTypes.IsTorrentHashClient(clientType));
        }
    }
}

/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Tests.Builders;
using Microsoft.Extensions.Logging.Abstractions;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Downloads.Submission
{
    [Trait("Name", "DownloadClientSelectorTests")]
    [Trait("Category", "DownloadClientSelection")]
    public class DownloadClientSelectorTests : BaseTests
    {
        [Fact]
        public async Task GetAppropriateDownloadClientAsync_SelectsEnabledDelugeWhenItIsTheOnlyTorrentClient()
        {
            var selectedClientId = await GetSubmissionSelectorResultAsync(
                CreateClient("deluge", "deluge", true));

            Assert.Equal("deluge", selectedClientId);
        }

        [Fact]
        public async Task GetAppropriateDownloadClientAsync_DoesNotSelectDisabledDeluge()
        {
            var selectedClientId = await GetSubmissionSelectorResultAsync(
                CreateClient("deluge", "deluge", false));

            Assert.Null(selectedClientId);
        }

        [Fact]
        public async Task GetAppropriateDownloadClientAsync_PreservesQbittorrentThenTransmissionPrecedenceOverDeluge()
        {
            var selectedClientId = await GetSubmissionSelectorResultAsync(
                CreateClient("deluge", "deluge", true),
                CreateClient("transmission", "transmission", true),
                CreateClient("qbittorrent", "qbittorrent", true));

            Assert.Equal("qbittorrent", selectedClientId);
        }

        [Fact]
        public async Task GetAppropriateDownloadClientAsync_PreservesTransmissionPrecedenceOverDeluge()
        {
            var selectedClientId = await GetSubmissionSelectorResultAsync(
                CreateClient("deluge", "deluge", true),
                CreateClient("transmission", "transmission", true));

            Assert.Equal("transmission", selectedClientId);
        }

        [Fact]
        public async Task AutomaticSearchSelector_SelectsEnabledDelugeWhenItIsTheOnlyTorrentClient()
        {
            var selectedClientId = await GetAutomaticSearchSelectorResultAsync(
                CreateClient("deluge", "deluge", true));

            Assert.Equal("deluge", selectedClientId);
        }

        [Fact]
        public async Task AutomaticSearchSelector_DoesNotSelectDisabledDeluge()
        {
            var selectedClientId = await GetAutomaticSearchSelectorResultAsync(
                CreateClient("deluge", "deluge", false));

            Assert.Equal(string.Empty, selectedClientId);
        }

        [Fact]
        public async Task AutomaticSearchSelector_PreservesTransmissionPrecedenceOverDeluge()
        {
            var selectedClientId = await GetAutomaticSearchSelectorResultAsync(
                CreateClient("deluge", "deluge", true),
                CreateClient("transmission", "transmission", true));

            Assert.Equal("transmission", selectedClientId);
        }

        [Fact]
        public async Task AutomaticSearchSelector_PreservesQbittorrentThenTransmissionPrecedenceOverDeluge()
        {
            var selectedClientId = await GetAutomaticSearchSelectorResultAsync(
                CreateClient("deluge", "deluge", true),
                CreateClient("transmission", "transmission", true),
                CreateClient("qbittorrent", "qbittorrent", true));

            Assert.Equal("qbittorrent", selectedClientId);
        }

        private static async Task<string?> GetSubmissionSelectorResultAsync(params DownloadClientConfiguration[] clients)
        {
            var configurationService = CreateConfigurationService(clients);
            var selector = new DownloadClientSelector(configurationService.Object, NullLogger<DownloadClientSelector>.Instance);

            return await selector.GetAppropriateDownloadClientAsync(isTorrent: true);
        }

        private static async Task<string> GetAutomaticSearchSelectorResultAsync(params DownloadClientConfiguration[] clients)
        {
            var configurationService = CreateConfigurationService(clients);
            var services = new ServiceCollection();
            services.AddScoped(_ => configurationService.Object);
            await using var provider = services.BuildServiceProvider();
            var selector = new AutomaticSearchDownloadClientSelector(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger.Instance);

            return await selector.GetAppropriateDownloadClientAsync(new SearchResult(), isTorrent: true);
        }

        private static Mock<IConfigurationService> CreateConfigurationService(IEnumerable<DownloadClientConfiguration> clients)
        {
            var configurationService = new Mock<IConfigurationService>();
            configurationService.Setup(service => service.GetDownloadClientConfigurationsAsync()).ReturnsAsync(clients.ToList());
            return configurationService;
        }

        private static DownloadClientConfiguration CreateClient(string id, string type, bool enabled)
        {
            var builder = new DownloadClientConfigurationBuilder()
                .WithId(id)
                .WithName(type)
                .WithType(type);

            return (enabled ? builder.Enabled() : builder.Disabled()).Build();
        }
    }
}

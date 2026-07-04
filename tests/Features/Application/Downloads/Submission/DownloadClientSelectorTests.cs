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

namespace Listenarr.Tests.Features.Application.Downloads.Submission
{
    [Trait("Area", "DownloadsSubmission")]
    [Trait("Name", "DownloadClientSelectorTests")]
    public class DownloadClientSelectorTests
    {
        [Fact]
        [Trait("Method", "GetAppropriateDownloadClientAsync")]
        [Trait("Scenario", "ManualSearchTorrentAutoSelectsOnlyDelugeClient")]
        public async Task GetAppropriateDownloadClientAsync_TorrentWithOnlyEnabledDeluge_ReturnsDelugeClientId()
        {
            var deluge = new DownloadClientConfigurationBuilder()
                .WithId("it-deluge-sandbox")
                .WithName("IT Deluge Sandbox")
                .WithType("deluge")
                .Enabled()
                .Build();

            var selector = CreateSelector([deluge]);

            var selectedClientId = await selector.GetAppropriateDownloadClientAsync(isTorrent: true);

            Assert.Equal("it-deluge-sandbox", selectedClientId);
        }

        [Fact]
        [Trait("Method", "GetAppropriateDownloadClientAsync")]
        [Trait("Scenario", "TorrentPreferenceOrderIncludesDelugeAfterExistingClients")]
        public async Task GetAppropriateDownloadClientAsync_TorrentPreservesExistingPreferenceOrderBeforeDeluge()
        {
            var deluge = new DownloadClientConfigurationBuilder()
                .WithId("deluge-client")
                .WithName("Deluge")
                .WithType("deluge")
                .Enabled()
                .Build();
            var transmission = new DownloadClientConfigurationBuilder()
                .WithId("transmission-client")
                .WithName("Transmission")
                .WithType("transmission")
                .Enabled()
                .Build();
            var qbittorrent = new DownloadClientConfigurationBuilder()
                .WithId("qbittorrent-client")
                .WithName("qBittorrent")
                .WithType("qbittorrent")
                .Enabled()
                .Build();

            var selector = CreateSelector([deluge, transmission, qbittorrent]);

            var selectedClientId = await selector.GetAppropriateDownloadClientAsync(isTorrent: true);

            Assert.Equal("qbittorrent-client", selectedClientId);
        }

        [Fact]
        [Trait("Method", "GetAppropriateDownloadClientAsync")]
        [Trait("Scenario", "TorrentAutoSelectionIgnoresDisabledDeluge")]
        public async Task GetAppropriateDownloadClientAsync_TorrentWithDisabledDeluge_ReturnsNull()
        {
            var deluge = new DownloadClientConfigurationBuilder()
                .WithId("disabled-deluge")
                .WithName("Disabled Deluge")
                .WithType("deluge")
                .Disabled()
                .Build();

            var selector = CreateSelector([deluge]);

            var selectedClientId = await selector.GetAppropriateDownloadClientAsync(isTorrent: true);

            Assert.Null(selectedClientId);
        }

        private static DownloadClientSelector CreateSelector(List<DownloadClientConfiguration> downloadClients)
        {
            var configurationService = new Mock<IConfigurationService>();
            configurationService
                .Setup(service => service.GetDownloadClientConfigurationsAsync())
                .ReturnsAsync(downloadClients);

            return new DownloadClientSelector(
                configurationService.Object,
                NullLogger<DownloadClientSelector>.Instance);
        }
    }
}

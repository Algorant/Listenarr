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

namespace Listenarr.Tests.Features.Application.Security.Redaction
{
    public class ApiResponseRedactorTests
    {
        [Fact]
        public void DownloadClientResponses_IncludeConfiguredDownloadPath()
        {
            var client = new DownloadClientConfiguration
            {
                DownloadPath = "C:/Users/Ivan/Downloads/ListenarrIT/deluge/downloads"
            };

            using var summary = JsonDocument.Parse(JsonSerializer.Serialize(ApiResponseRedactor.ToDownloadClientSummaryResponse(client)));
            using var detail = JsonDocument.Parse(JsonSerializer.Serialize(ApiResponseRedactor.ToDownloadClientDetailResponse(client)));

            Assert.Equal(client.DownloadPath, summary.RootElement.GetProperty("DownloadPath").GetString());
            Assert.Equal(client.DownloadPath, detail.RootElement.GetProperty("DownloadPath").GetString());
        }
    }
}

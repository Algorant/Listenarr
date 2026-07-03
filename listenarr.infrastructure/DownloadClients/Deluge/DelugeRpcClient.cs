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
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Deluge
{
    internal sealed class DelugeRpcClient(
        IHttpClientFactory httpClientFactory,
        string clientType,
        ILogger logger)
    {
        public async Task<JsonElement> InvokeAsync(DownloadClientConfiguration client, string method, object[] parameters, CancellationToken ct = default)
        {
            var http = httpClientFactory.CreateClient(clientType);
            var payload = JsonSerializer.Serialize(new { method, @params = parameters, id = 1 });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var url = BuildBaseUrl(client);

            logger.LogDebug("Deluge RPC request to {Url}: {Method}", LogRedaction.SanitizeUrl(url), method);
            using var response = await http.PostAsync(url, content, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new UnauthorizedAccessException("Deluge rejected the request");
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Deluge returned {StatusCode}: {Body}", response.StatusCode, LogRedaction.SanitizeText(body, 500));
                throw new HttpRequestException($"Deluge returned {response.StatusCode}: {body}", null, response.StatusCode);
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null && error.ValueKind != JsonValueKind.Undefined)
            {
                throw new InvalidOperationException($"Deluge JSON-RPC error calling {method}: {error}");
            }

            return doc.RootElement.TryGetProperty("result", out var result) ? result.Clone() : default;
        }

        public async Task AuthenticateAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            var result = await InvokeAsync(client, "auth.login", [client.Password ?? string.Empty], ct);
            if (result.ValueKind != JsonValueKind.True)
            {
                throw new UnauthorizedAccessException("Failed to authenticate with Deluge Web UI");
            }
        }

        public async Task EnsureDaemonConnectedAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            var connected = await InvokeAsync(client, "web.connected", [], ct);
            if (connected.ValueKind == JsonValueKind.True)
            {
                return;
            }

            var hosts = await InvokeAsync(client, "web.get_hosts", [], ct);
            if (hosts.ValueKind != JsonValueKind.Array || hosts.GetArrayLength() == 0)
            {
                throw new InvalidOperationException("Deluge Web is not connected to a daemon and no daemon hosts are configured");
            }

            var hostId = hosts.EnumerateArray()
                .Where(host => host.ValueKind == JsonValueKind.Array && host.GetArrayLength() > 0)
                .Select(host => host[0].GetString())
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

            if (string.IsNullOrWhiteSpace(hostId))
            {
                throw new InvalidOperationException("Deluge Web returned no daemon host id");
            }

            await InvokeAsync(client, "web.connect", [hostId], ct);
        }

        private static string BuildBaseUrl(DownloadClientConfiguration client)
        {
            var urlBase = DelugeSettings.Get(client, "urlBase") ?? DelugeSettings.Get(client, "UrlBase") ?? string.Empty;
            urlBase = urlBase.Trim('/');
            var rpcPath = string.IsNullOrWhiteSpace(urlBase) ? "/json" : $"/{urlBase}/json";
            return DownloadClientUriBuilder.BuildUri(client, rpcPath).ToString();
        }
    }
}

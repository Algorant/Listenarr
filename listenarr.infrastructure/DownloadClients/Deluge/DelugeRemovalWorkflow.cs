/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

namespace Listenarr.Infrastructure.DownloadClients.Deluge
{
    internal sealed class DelugeRemovalWorkflow(DelugeRpcClient rpcClient)
    {
        public async Task<bool> RemoveAsync(DownloadClientConfiguration client, string id, bool deleteFiles = false, CancellationToken ct = default)
        {
            await rpcClient.AuthenticateAsync(client, ct);
            await rpcClient.EnsureDaemonConnectedAsync(client, ct);
            var result = await rpcClient.InvokeAsync(client, "core.remove_torrent", [id, deleteFiles], ct);
            return result.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined;
        }
    }
}

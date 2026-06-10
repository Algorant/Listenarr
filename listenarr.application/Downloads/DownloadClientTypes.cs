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
using Listenarr.Domain.Models;

namespace Listenarr.Application.Downloads;

public static class DownloadClientTypes
{
    public const string Qbittorrent = "qbittorrent";
    public const string Transmission = "transmission";
    public const string Deluge = "deluge";
    public const string Sabnzbd = "sabnzbd";
    public const string Nzbget = "nzbget";

    public const string TorrentClientDisplayList = "qBittorrent, Transmission, or Deluge";
    public const string UsenetClientDisplayList = "SABnzbd or NZBGet";

    private static readonly string[] TorrentPreferenceOrder =
    [
        Qbittorrent,
        Transmission,
        Deluge
    ];

    private static readonly string[] UsenetPreferenceOrder =
    [
        Sabnzbd,
        Nzbget
    ];

    public static DownloadClientConfiguration? SelectPreferredTorrentClient(IEnumerable<DownloadClientConfiguration> clients)
        => SelectPreferredClient(clients, TorrentPreferenceOrder);

    public static DownloadClientConfiguration? SelectPreferredUsenetClient(IEnumerable<DownloadClientConfiguration> clients)
        => SelectPreferredClient(clients, UsenetPreferenceOrder);

    public static bool IsTorrentClient(string? type)
        => MatchesAny(type, TorrentPreferenceOrder);

    public static bool IsTorrentHashClient(string? type)
        => IsTorrentClient(type);

    private static DownloadClientConfiguration? SelectPreferredClient(
        IEnumerable<DownloadClientConfiguration> clients,
        IReadOnlyList<string> preferredTypes)
    {
        if (clients == null)
        {
            return null;
        }

        var clientList = clients.ToList();
        foreach (var preferredType in preferredTypes)
        {
            var client = clientList.FirstOrDefault(c => TypeEquals(c.Type, preferredType));
            if (client != null)
            {
                return client;
            }
        }

        return null;
    }

    private static bool MatchesAny(string? type, IEnumerable<string> candidates)
        => candidates.Any(candidate => TypeEquals(type, candidate));

    private static bool TypeEquals(string? type, string expected)
        => string.Equals(type, expected, StringComparison.OrdinalIgnoreCase);
}

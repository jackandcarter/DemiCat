using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace DemiCatPlugin;

internal static class MembershipCache
{
    private static readonly object SyncRoot = new();
    private static string? _displayName;
    private static bool _loading;

    internal static string? DisplayName
    {
        get
        {
            lock (SyncRoot)
            {
                return _displayName;
            }
        }
    }

    internal static void Reset()
    {
        lock (SyncRoot)
        {
            _displayName = null;
            _loading = false;
        }
    }

    internal static async Task EnsureLoaded(HttpClient httpClient, Config config)
    {
        if (TokenManager.Instance?.IsReady() != true)
        {
            return;
        }

        if (!ApiHelpers.ValidateApiBaseUrl(config))
        {
            return;
        }

        lock (SyncRoot)
        {
            if (_displayName != null || _loading)
            {
                return;
            }

            _loading = true;
        }

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{config.ApiBaseUrl.TrimEnd('/')}/api/users/me/profile");
            ApiHelpers.AddAuthHeader(request, TokenManager.Instance!);
            var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            if (doc.RootElement.TryGetProperty("displayName", out var displayNameElement))
            {
                var value = displayNameElement.GetString();
                lock (SyncRoot)
                {
                    _displayName = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                }
            }
        }
        catch
        {
            // ignore failures; a placeholder will be used instead
        }
        finally
        {
            lock (SyncRoot)
            {
                _loading = false;
            }
        }
    }

    internal static string GetCreatorLabel()
    {
        string? name;
        lock (SyncRoot)
        {
            name = _displayName;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            var player = PluginServices.Instance?.ClientState?.LocalPlayer;
            var characterName = player?.Name.TextValue ?? player?.Name.ToString();
            if (!string.IsNullOrWhiteSpace(characterName))
            {
                name = characterName;
            }
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = "You";
        }

        return $"Event created by {name}";
    }
}

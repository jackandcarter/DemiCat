using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace DemiCatPlugin;

internal static class MembershipCache
{
    private static readonly object SyncRoot = new();
    private static string? _displayName;
    private static string? _discordUserId;
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

    internal static string? DiscordUserId
    {
        get
        {
            lock (SyncRoot)
            {
                return _discordUserId;
            }
        }
    }

    internal static void Reset()
    {
        lock (SyncRoot)
        {
            _displayName = null;
            _discordUserId = null;
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
            void SetValue(ref string? field, string? value)
            {
                lock (SyncRoot)
                {
                    field = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                }
            }

            if (doc.RootElement.TryGetProperty("displayName", out var displayNameElement))
            {
                SetValue(ref _displayName, displayNameElement.GetString());
            }

            if (doc.RootElement.TryGetProperty("discordUserId", out var discordUserIdElement))
            {
                string? idValue = discordUserIdElement.ValueKind switch
                {
                    JsonValueKind.String => discordUserIdElement.GetString(),
                    JsonValueKind.Number => discordUserIdElement.GetRawText(),
                    _ => null
                };
                SetValue(ref _discordUserId, idValue);
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

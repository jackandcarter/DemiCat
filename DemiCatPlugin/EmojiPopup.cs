using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;

namespace DemiCatPlugin;

public class EmojiPopup
{
    private readonly Config _config;
    private readonly HttpClient _httpClient;
    private readonly List<UnicodeEmoji> _unicode = new();
    private readonly List<GuildEmoji> _guild = new();
    private bool _unicodeLoaded;
    private bool _guildLoaded;
    private Action<string>? _onSelected;

    private const string PopupId = "PickEmoji";

    public EmojiPopup(Config config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
    }

    public void Open(Action<string> onSelected)
    {
        _onSelected = onSelected;
        ImGui.OpenPopup(PopupId);
    }

    public void Draw()
    {
        if (!ImGui.BeginPopup(PopupId)) return;
        if (ImGui.BeginTabBar("emoji-tabs"))
        {
            if (ImGui.BeginTabItem("Unicode"))
            {
                DrawUnicode();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Guild"))
            {
                DrawGuild();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
        ImGui.EndPopup();
    }

    private void DrawUnicode()
    {
        if (!_unicodeLoaded) _ = FetchUnicode();
        foreach (var e in _unicode)
        {
            if (ImGui.Button($"{e.Emoji}##u{e.Emoji}"))
            {
                _onSelected?.Invoke(e.Emoji);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
        }
        ImGui.NewLine();
    }

    private void DrawGuild()
    {
        if (!_guildLoaded) _ = FetchGuild();
        foreach (var e in _guild)
        {
            if (ImGui.Button($":{e.Name}:##g{e.Id}"))
            {
                GuildEmojiInfos[e.Id] = (e.Name, e.IsAnimated);
                _onSelected?.Invoke($"custom:{e.Id}");
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
        }
        ImGui.NewLine();
    }

    private async Task FetchUnicode()
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config)) return;
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/emojis/unicode");
            ApiHelpers.AddAuthHeader(request, TokenManager.Instance!);
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return;
            var stream = await response.Content.ReadAsStreamAsync();
            var list = await JsonSerializer.DeserializeAsync<List<UnicodeEmoji>>(stream) ?? new();
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _unicode.Clear();
                _unicode.AddRange(list);
                _unicodeLoaded = true;
            });
        }
        catch
        {
            // ignore
        }
    }

    private async Task FetchGuild()
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config) || string.IsNullOrWhiteSpace(_config.GuildId)) return;
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/emojis/guilds/{_config.GuildId}");
            ApiHelpers.AddAuthHeader(request, TokenManager.Instance!);
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return;
            var stream = await response.Content.ReadAsStreamAsync();
            var list = await JsonSerializer.DeserializeAsync<List<GuildEmoji>>(stream) ?? new();
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _guild.Clear();
                _guild.AddRange(list);
                foreach (var g in list)
                    GuildEmojiInfos[g.Id] = (g.Name, g.IsAnimated);
                _guildLoaded = true;
            });
        }
        catch
        {
            // ignore
        }
    }

    public static string? LookupGuildName(string id) =>
        GuildEmojiInfos.TryGetValue(id, out var info) ? info.Name : null;

    public static bool IsGuildEmojiAnimated(string id) =>
        GuildEmojiInfos.TryGetValue(id, out var info) && info.IsAnimated;

    private static readonly Dictionary<string, (string Name, bool IsAnimated)> GuildEmojiInfos = new();

    public class UnicodeEmoji
    {
        public string Emoji { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string ImageUrl { get; set; } = string.Empty;
    }

    public class GuildEmoji
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool IsAnimated { get; set; }
        public string ImageUrl { get; set; } = string.Empty;
    }
}

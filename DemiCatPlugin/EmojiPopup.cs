using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Numerics;
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
    private string _search = string.Empty;

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

        ImGui.SetNextItemWidth(240);
        ImGui.InputText("Search", ref _search, 64);

        if (ImGui.BeginTabBar("emoji-tabs"))
        {
            if (ImGui.BeginTabItem("Emoji"))
            {
                DrawUnicodeGrid();
                ImGui.EndTabItem();
            }

            var guildEnabled = !string.IsNullOrWhiteSpace(_config.GuildId);
            if (!guildEnabled) ImGui.BeginDisabled();
            var guildTab = ImGui.BeginTabItem("Server");
            if (!guildEnabled && ImGui.IsItemHovered())
                ImGui.SetTooltip("Set GuildId in config to enable server emojis");
            if (guildTab)
            {
                DrawGuildGrid();
                ImGui.EndTabItem();
            }
            if (!guildEnabled) ImGui.EndDisabled();

            ImGui.EndTabBar();
        }

        ImGui.EndPopup();
    }

    private void DrawUnicodeGrid()
    {
        if (!_unicodeLoaded) _ = FetchUnicode();

        var items = string.IsNullOrWhiteSpace(_search)
            ? _unicode
            : _unicode.Where(u =>
                   u.Name.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
                   u.Emoji.Contains(_search, StringComparison.OrdinalIgnoreCase)
               ).ToList();

        DrawGrid(
            items.Count,
            i => items[i].ImageUrl,
            i => items[i].Name,
            i => items[i].Emoji,
            _ => false
        );
    }

    private void DrawGuildGrid()
    {
        if (!_guildLoaded) _ = FetchGuild();

        var items = string.IsNullOrWhiteSpace(_search)
            ? _guild
            : _guild.Where(g => g.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)).ToList();

        DrawGrid(
            items.Count,
            i => items[i].ImageUrl,
            i => items[i].Name,
            i => $"custom:{items[i].Id}",
            i => items[i].IsAnimated
        );
    }

    private void DrawGrid(
        int count,
        Func<int, string> getUrl,
        Func<int, string> getName,
        Func<int, string> getReturnValue,
        Func<int, bool> isDisabled,
        int columns = 8,
        float cellSize = 28f)
    {
        int col = 0;

        for (int i = 0; i < count; i++)
        {
            if (col == 0) ImGui.BeginGroup();

            var url = getUrl(i);
            var name = getName(i);
            var disabled = isDisabled(i);
            var index = i; // capture stable index for async callback

            if (disabled) ImGui.BeginDisabled();

            WebTextureCache.Get(url, tex =>
            {
                if (tex != null)
                {
                    ImGui.PushID(index);
                    var ret = getReturnValue(index);

                    if (ImGui.ImageButton(tex.GetWrapOrEmpty().Handle, new Vector2(cellSize, cellSize)))
                    {
                        if (!disabled)
                        {
                            const string markerPrefix = "custom:";
                            if (ret.StartsWith(markerPrefix, StringComparison.OrdinalIgnoreCase))
                            {
                                var id = ret.Substring(markerPrefix.Length);
                                EmojiAssets.SetGuildEmoji(id, name, false);
                            }
                            else
                            {
                                if (EmojiAssets.LookupUnicodeUrl(ret) == null)
                                    EmojiAssets.SetUnicodeEmoji(ret, url);
                            }

                            _onSelected?.Invoke(ret);
                            ImGui.CloseCurrentPopup();
                        }
                    }

                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(name);

                    ImGui.PopID();
                }
            });

            if (disabled) ImGui.EndDisabled();

            ImGui.SameLine();

            col++;
            if (col >= columns)
            {
                ImGui.NewLine();
                ImGui.EndGroup();
                col = 0;
            }
        }

        if (col != 0) { ImGui.NewLine(); ImGui.EndGroup(); }
    }

    // For tests to warm the cache without UI
    internal void PreloadGuildTextures()
    {
        foreach (var g in _guild)
            WebTextureCache.Get(g.ImageUrl, _ => { });
    }

    private async Task FetchUnicode()
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config)) return;

        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/emojis/unicode");
            ApiHelpers.AddAuthHeader(req, TokenManager.Instance!);
            var res = await _httpClient.SendAsync(req);
            if (!res.IsSuccessStatusCode) return;

            var stream = await res.Content.ReadAsStreamAsync();
            var list = await JsonSerializer.DeserializeAsync<List<UnicodeEmoji>>(stream) ?? new();

            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _unicode.Clear();
                _unicode.AddRange(list);

                foreach (var u in list) EmojiAssets.SetUnicodeEmoji(u.Emoji, u.ImageUrl);

                _unicodeLoaded = true;
            });
        }
        catch { }
    }

    private async Task FetchGuild()
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config) || string.IsNullOrWhiteSpace(_config.GuildId)) return;

        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/emojis/guilds/{_config.GuildId}");
            ApiHelpers.AddAuthHeader(req, TokenManager.Instance!);
            var res = await _httpClient.SendAsync(req);
            if (!res.IsSuccessStatusCode) return;

            var stream = await res.Content.ReadAsStreamAsync();
            var list = await JsonSerializer.DeserializeAsync<List<GuildEmoji>>(stream) ?? new();

            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _guild.Clear();
                _guild.AddRange(list);
                foreach (var g in list) EmojiAssets.SetGuildEmoji(g.Id, g.Name, g.IsAnimated);
                _guildLoaded = true;
            });
        }
        catch { }
    }

    // Static lookups used elsewhere
    public static string? LookupGuildName(string id) => EmojiAssets.LookupGuildName(id);
    public static bool IsGuildEmojiAnimated(string id) => EmojiAssets.IsGuildEmojiAnimated(id);

    public class UnicodeEmoji
    {
        public string Emoji { get; set; } = string.Empty;
        public string Name   { get; set; } = string.Empty;
        public string ImageUrl { get; set; } = string.Empty;
    }

    public class GuildEmoji
    {
        public string Id        { get; set; } = string.Empty;
        public string Name      { get; set; } = string.Empty;
        public bool   IsAnimated{ get; set; }
        public string ImageUrl  { get; set; } = string.Empty;
    }
}

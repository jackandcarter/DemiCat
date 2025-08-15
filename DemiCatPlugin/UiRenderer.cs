using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Numerics;
using System.Threading.Tasks;
using DiscordHelper;
using Dalamud.Bindings.ImGui;

namespace DemiCatPlugin;

public class UiRenderer : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Config _config;
    private readonly Dictionary<string, EventView> _embeds = new();
    private string _channelId;
    private EventView? _current;

    public UiRenderer(Config config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
        _channelId = config.EventChannelId;
    }

    public string ChannelId
    {
        get => _channelId;
        set => _channelId = value;
    }

    public void SetEmbeds(IEnumerable<EmbedDto> embeds)
    {
        var ids = new HashSet<string>();
        foreach (var dto in embeds)
        {
            ids.Add(dto.Id);
            if (_embeds.TryGetValue(dto.Id, out var view))
            {
                view.Update(dto);
            }
            else
            {
                _embeds[dto.Id] = new EventView(dto, _config, _httpClient, RefreshEmbeds);
            }
        }

        foreach (var key in _embeds.Keys.Where(k => !ids.Contains(k)).ToList())
        {
            _embeds[key].Dispose();
            _embeds.Remove(key);
        }
    }

    public async Task RefreshEmbeds()
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.HelperBaseUrl.TrimEnd('/')}/api/embeds");
            if (!string.IsNullOrEmpty(_config.AuthToken))
            {
                request.Headers.Add("X-Api-Key", _config.AuthToken);
            }
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }
            var stream = await response.Content.ReadAsStreamAsync();
            var embeds = await JsonSerializer.DeserializeAsync<List<EmbedDto>>(stream) ?? new List<EmbedDto>();
            SetEmbeds(embeds);
        }
        catch
        {
            // ignored
        }
    }

    public void Draw()
    {
        ImGui.BeginChild("##eventButtons", new Vector2(120, 0), true);
        _current?.DrawButtons();
        ImGui.EndChild();

        ImGui.SameLine();

        ImGui.BeginChild("##eventScroll", new Vector2(0, 0), true);

        var scrollY = ImGui.GetScrollY();
        _current = null;

        foreach (var view in _embeds.Values.Where(v => string.IsNullOrEmpty(_channelId) || v.ChannelId == _channelId))
        {
            var start = ImGui.GetCursorPosY();
            view.Draw();
            var end = ImGui.GetCursorPosY();
            if (_current == null && scrollY >= start && scrollY < end)
            {
                _current = view;
            }
        }

        ImGui.EndChild();
    }

    public void Dispose()
    {
        foreach (var view in _embeds.Values)
        {
            view.Dispose();
        }
        _embeds.Clear();
    }
}


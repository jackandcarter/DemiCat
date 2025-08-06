using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Numerics;
using DiscordHelper;
using ImGuiNET;

namespace DalamudPlugin;

public class UiRenderer : IDisposable
{
    private readonly HttpClient _httpClient = new();
    private readonly Config _config;
    private readonly Dictionary<string, EventView> _embeds = new();

    public UiRenderer(Config config)
    {
        _config = config;
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

    public async void RefreshEmbeds()
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.HelperBaseUrl.TrimEnd('/')}/embeds");
            if (!string.IsNullOrEmpty(_config.AuthToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AuthToken);
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
        ImGui.BeginChild("##eventScroll", new Vector2(0, 0), true);

        foreach (var view in _embeds.Values)
        {
            view.Draw();
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
        _httpClient.Dispose();
    }
}


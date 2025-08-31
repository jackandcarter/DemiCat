using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using System.Numerics;

namespace DemiCatPlugin;

public class EmojiPicker
{
    private readonly Config _config;
    private readonly HttpClient _httpClient;
    private readonly List<EmojiDto> _emojis = new();
    private bool _loaded;

    public Action<string?, Action<ISharedImmediateTexture?>>? TextureLoader { get; set; }

    public EmojiPicker(Config config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
    }

    public void Draw(Action<string> onSelected)
    {
        if (!_loaded)
        {
            _ = Fetch();
        }
        var size = 24f;
        var avail = ImGui.GetContentRegionAvail().X;
        var perRow = Math.Max(1, (int)(avail / size));
        var idx = 0;
        foreach (var e in _emojis)
        {
            if (TextureLoader != null && e.Texture == null)
            {
                TextureLoader(e.ImageUrl, t => e.Texture = t);
            }
            if (e.Texture != null)
            {
                var wrap = e.Texture.GetWrapOrEmpty();
                if (ImGui.ImageButton(wrap.Handle, new Vector2(size, size)))
                {
                    var formatted = e.IsAnimated ? $"<a:{e.Name}:{e.Id}>" : $"<:{e.Name}:{e.Id}>";
                    onSelected(formatted);
                }
            }
            else
            {
                ImGui.Dummy(new Vector2(size, size));
            }
            idx++;
            if (idx % perRow != 0)
            {
                ImGui.SameLine();
            }
        }
    }

    private async Task Fetch()
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config))
        {
            return;
        }
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/emojis");
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
            var list = await JsonSerializer.DeserializeAsync<List<EmojiDto>>(stream) ?? new List<EmojiDto>();
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _emojis.Clear();
                _emojis.AddRange(list);
                _loaded = true;
            });
        }
        catch
        {
            // ignore
        }
    }

    public class EmojiDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool IsAnimated { get; set; }
        public string ImageUrl { get; set; } = string.Empty;
        [JsonIgnore]
        public ISharedImmediateTexture? Texture { get; set; }
    }
}

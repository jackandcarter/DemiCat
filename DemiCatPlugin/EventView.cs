using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.IO;
using DiscordHelper;
using Dalamud.Interface.Textures;
using Dalamud.Bindings.ImGui;
using StbImageSharp;

namespace DemiCatPlugin;

public class EventView : IDisposable
{
    private readonly Config _config;
    private readonly HttpClient _httpClient;
    private readonly Func<Task> _refresh;

    private EmbedDto _dto;
    private ISharedImmediateTexture? _authorIcon;
    private ISharedImmediateTexture? _thumbnail;
    private ISharedImmediateTexture? _image;
    private string? _lastResult;

    public EventView(EmbedDto dto, Config config, HttpClient httpClient, Func<Task> refresh)
    {
        _config = config;
        _httpClient = httpClient;
        _refresh = refresh;
        _dto = dto;
        LoadTexture(dto.AuthorIconUrl, t => _authorIcon = t);
        LoadTexture(dto.ThumbnailUrl, t => _thumbnail = t);
        LoadTexture(dto.ImageUrl, t => _image = t);
    }

    public void Update(EmbedDto dto)
    {
        if (_dto.AuthorIconUrl != dto.AuthorIconUrl)
        {
            LoadTexture(dto.AuthorIconUrl, t => _authorIcon = t);
        }
        if (_dto.ThumbnailUrl != dto.ThumbnailUrl)
        {
            LoadTexture(dto.ThumbnailUrl, t => _thumbnail = t);
        }
        if (_dto.ImageUrl != dto.ImageUrl)
        {
            LoadTexture(dto.ImageUrl, t => _image = t);
        }
        _dto = dto;
    }

    public string ChannelId => _dto.ChannelId?.ToString() ?? string.Empty;

    public IReadOnlyList<EmbedButtonDto>? Buttons => _dto.Buttons;

    public void Draw()
    {
        var dto = _dto;

        if (dto.Color.HasValue)
        {
            var color = dto.Color.Value | 0xFF000000;
            var dl = ImGui.GetWindowDrawList();
            var p = ImGui.GetCursorScreenPos();
            var end = new Vector2(p.X + 4, p.Y + ImGui.GetTextLineHeightWithSpacing() * 3);
            dl.AddRectFilled(p, end, color);
            ImGui.Dummy(new Vector2(4, 0));
            ImGui.SameLine();
        }

        if (_authorIcon != null)
        {
            var wrap = _authorIcon.GetWrapOrEmpty();
            ImGui.Image(wrap.Handle, new Vector2(32, 32));
            ImGui.SameLine();
        }

        var header = dto.Title ?? string.Empty;
        if (dto.Timestamp.HasValue)
        {
            header += $" - {dto.Timestamp.Value.LocalDateTime}";
        }
        ImGui.TextUnformatted(header);

        if (!string.IsNullOrEmpty(dto.Description))
        {
            ImGui.TextWrapped(dto.Description);
        }

        if (dto.Fields != null && dto.Fields.Count > 0)
        {
            if (ImGui.BeginTable($"fields{dto.Id}", 2, ImGuiTableFlags.Borders))
            {
                foreach (var field in dto.Fields)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextUnformatted(field.Name);
                    ImGui.TableSetColumnIndex(1);
                    ImGui.TextUnformatted(field.Value);
                }
                ImGui.EndTable();
            }
        }

        if (_image != null)
        {
            var wrap = _image.GetWrapOrEmpty();
            var size = new Vector2(wrap.Width, wrap.Height);
            ImGui.Image(wrap.Handle, size);
        }

        if (_thumbnail != null)
        {
            var wrap = _thumbnail.GetWrapOrEmpty();
            ImGui.Image(wrap.Handle, new Vector2(wrap.Width, wrap.Height));
        }

        if (dto.Mentions != null && dto.Mentions.Count > 0)
        {
            ImGui.Text($"Mentions: {string.Join(", ", dto.Mentions)}");
        }

        ImGui.Separator();
    }

    public void DrawButtons()
    {
        if (Buttons != null)
        {
            foreach (var button in Buttons)
            {
                var id = button.CustomId ?? button.Label;
                if (ImGui.Button($"{button.Label}##{id}{_dto.Id}", new Vector2(-1, 0)))
                {
                    _ = SendInteraction(id);
                }
            }
        }

        if (!string.IsNullOrEmpty(_lastResult))
        {
            ImGui.TextUnformatted(_lastResult);
        }
    }

    private void LoadTexture(string? url, Action<ISharedImmediateTexture?> set)
    {
        if (string.IsNullOrEmpty(url))
        {
            set(null);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var bytes = await _httpClient.GetByteArrayAsync(url).ConfigureAwait(false);
                using var stream = new MemoryStream(bytes);
                var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
                var wrap = PluginServices.TextureProvider.CreateFromRaw(
                    RawImageSpecification.Rgba32(image.Width, image.Height),
                    image.Data);
                var texture = new ForwardingSharedImmediateTexture(wrap);
                _ = PluginServices.Framework.RunOnTick(() => set(texture));
            }
            catch
            {
                _ = PluginServices.Framework.RunOnTick(() => set(null));
            }
        });
    }

    public async Task SendInteraction(string customId)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.HelperBaseUrl.TrimEnd('/')}/api/interactions");
            var body = new { MessageId = _dto.Id, ChannelId = _dto.ChannelId, CustomId = customId };
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            if (!string.IsNullOrEmpty(_config.AuthToken))
            {
                request.Headers.Add("X-Api-Key", _config.AuthToken);
            }
            var response = await _httpClient.SendAsync(request);
            _lastResult = response.IsSuccessStatusCode ? "Signup updated" : "Signup failed";
            if (response.IsSuccessStatusCode)
            {
                await _refresh();
            }
        }
        catch
        {
            _lastResult = "Signup failed";
        }
    }

    public void Dispose()
    {
        // Dispose if the concrete type supports it; the interface doesn’t expose Dispose().
        (_authorIcon as IDisposable)?.Dispose();
        _authorIcon = null;

        (_thumbnail as IDisposable)?.Dispose();
        _thumbnail = null;

        (_image as IDisposable)?.Dispose();
        _image = null;
    }
}

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Numerics;
using System.Text;
using System.Text.Json;
using DiscordHelper;
using Dalamud.Interface.Textures;
using ImGuiNET;
using StbImageSharp;

namespace DalamudPlugin;

public class EventView : IDisposable
{
    private readonly Config _config;
    private readonly HttpClient _httpClient;
    private readonly Action _refresh;

    private EmbedDto _dto;
    private IDalamudTextureWrap? _authorIcon;
    private IDalamudTextureWrap? _thumbnail;
    private IDalamudTextureWrap? _image;
    private string? _lastResult;

    public EventView(EmbedDto dto, Config config, HttpClient httpClient, Action refresh)
    {
        _config = config;
        _httpClient = httpClient;
        _refresh = refresh;
        _dto = dto;
        _authorIcon = LoadTexture(dto.AuthorIconUrl);
        _thumbnail = LoadTexture(dto.ThumbnailUrl);
        _image = LoadTexture(dto.ImageUrl);
    }

    public void Update(EmbedDto dto)
    {
        if (_dto.AuthorIconUrl != dto.AuthorIconUrl)
        {
            _authorIcon?.Dispose();
            _authorIcon = LoadTexture(dto.AuthorIconUrl);
        }
        if (_dto.ThumbnailUrl != dto.ThumbnailUrl)
        {
            _thumbnail?.Dispose();
            _thumbnail = LoadTexture(dto.ThumbnailUrl);
        }
        if (_dto.ImageUrl != dto.ImageUrl)
        {
            _image?.Dispose();
            _image = LoadTexture(dto.ImageUrl);
        }
        _dto = dto;
    }

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
            ImGui.Image(_authorIcon.ImGuiHandle, new Vector2(32, 32));
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
            var size = new Vector2(_image.Width, _image.Height);
            ImGui.Image(_image.ImGuiHandle, size);
        }

        if (_thumbnail != null)
        {
            ImGui.Image(_thumbnail.ImGuiHandle, new Vector2(_thumbnail.Width, _thumbnail.Height));
        }

        if (dto.Mentions != null && dto.Mentions.Count > 0)
        {
            ImGui.Text($"Mentions: {string.Join(", ", dto.Mentions)}");
        }

        if (dto.Buttons != null)
        {
            foreach (var button in dto.Buttons)
            {
                var id = button.CustomId ?? button.Label;
                if (ImGui.Button($"{button.Label}##{id}{dto.Id}"))
                {
                    _ = SendInteraction(id);
                }
                ImGui.SameLine();
            }
            ImGui.NewLine();
        }

        if (!string.IsNullOrEmpty(_lastResult))
        {
            ImGui.TextUnformatted(_lastResult);
        }

        ImGui.Separator();
    }

    private IDalamudTextureWrap? LoadTexture(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        try
        {
            var bytes = _httpClient.GetByteArrayAsync(url).GetAwaiter().GetResult();
            using var stream = new System.IO.MemoryStream(bytes);
            var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            return PluginServices.PluginInterface.UiBuilder.LoadImageRaw(image.Data, image.Width, image.Height, 4);
        }
        catch
        {
            return null;
        }
    }

    private async System.Threading.Tasks.Task SendInteraction(string customId)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.HelperBaseUrl.TrimEnd('/')}/interactions");
            var body = new { MessageId = _dto.Id, ChannelId = _dto.ChannelId, CustomId = customId };
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            if (!string.IsNullOrEmpty(_config.AuthToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AuthToken);
            }
            var response = await _httpClient.SendAsync(request);
            _lastResult = response.IsSuccessStatusCode ? "Signup updated" : "Signup failed";
            if (response.IsSuccessStatusCode)
            {
                _refresh();
            }
        }
        catch
        {
            _lastResult = "Signup failed";
        }
    }

    public void Dispose()
    {
        _authorIcon?.Dispose();
        _thumbnail?.Dispose();
        _image?.Dispose();
    }
}

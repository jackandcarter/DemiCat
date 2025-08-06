using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Numerics;
using System.Text;
using System.Text.Json;
using DiscordHelper;
using ImGuiNET;
using StbImageSharp;

namespace DalamudPlugin;

public class UiRenderer : IDisposable
{
    private readonly HttpClient _httpClient = new();
    private readonly Config _config;

    private readonly Dictionary<string, EmbedState> _embeds = new();

    private class EmbedState
    {
        public EmbedDto Dto { get; set; }
        public IDalamudTextureWrap? AuthorIcon { get; set; }
        public IDalamudTextureWrap? Thumbnail { get; set; }
        public IDalamudTextureWrap? Image { get; set; }

        public EmbedState(EmbedDto dto)
        {
            Dto = dto;
        }
    }

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
            if (_embeds.TryGetValue(dto.Id, out var state))
            {
                // Update DTO and reload textures if URLs changed
                if (state.Dto.AuthorIconUrl != dto.AuthorIconUrl)
                {
                    state.AuthorIcon?.Dispose();
                    state.AuthorIcon = LoadTexture(dto.AuthorIconUrl);
                }
                if (state.Dto.ThumbnailUrl != dto.ThumbnailUrl)
                {
                    state.Thumbnail?.Dispose();
                    state.Thumbnail = LoadTexture(dto.ThumbnailUrl);
                }
                if (state.Dto.ImageUrl != dto.ImageUrl)
                {
                    state.Image?.Dispose();
                    state.Image = LoadTexture(dto.ImageUrl);
                }
                state.Dto = dto;
            }
            else
            {
                var stateNew = new EmbedState(dto)
                {
                    AuthorIcon = LoadTexture(dto.AuthorIconUrl),
                    Thumbnail = LoadTexture(dto.ThumbnailUrl),
                    Image = LoadTexture(dto.ImageUrl)
                };
                _embeds[dto.Id] = stateNew;
            }
        }

        // Remove embeds no longer present
        foreach (var key in _embeds.Keys.Where(k => !ids.Contains(k)).ToList())
        {
            DisposeState(_embeds[key]);
            _embeds.Remove(key);
        }
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
            return Service.Interface.UiBuilder.LoadImageRaw(image.Data, image.Width, image.Height, 4);
        }
        catch
        {
            return null;
        }
    }

    private void DisposeState(EmbedState state)
    {
        state.AuthorIcon?.Dispose();
        state.Thumbnail?.Dispose();
        state.Image?.Dispose();
    }

    public void DrawWindow()
    {
        if (!ImGui.Begin("Events"))
        {
            ImGui.End();
            return;
        }

        ImGui.BeginChild("##eventScroll", new Vector2(0, 0), true);

        foreach (var state in _embeds.Values)
        {
            var dto = state.Dto;

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

            if (state.AuthorIcon != null)
            {
                ImGui.Image(state.AuthorIcon.ImGuiHandle, new Vector2(32, 32));
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

            if (state.Image != null)
            {
                var size = new Vector2(state.Image.Width, state.Image.Height);
                ImGui.Image(state.Image.ImGuiHandle, size);
            }

            if (state.Thumbnail != null)
            {
                ImGui.Image(state.Thumbnail.ImGuiHandle, new Vector2(state.Thumbnail.Width, state.Thumbnail.Height));
            }

            if (dto.Mentions != null && dto.Mentions.Count > 0)
            {
                ImGui.Text($"Mentions: {string.Join(", ", dto.Mentions)}");
            }

            if (ImGui.Button($"✅##yes{dto.Id}"))
            {
                SendRsvp(dto.Id, "✅");
            }
            ImGui.SameLine();
            if (ImGui.Button($"❌##no{dto.Id}"))
            {
                SendRsvp(dto.Id, "❌");
            }
            ImGui.SameLine();
            if (ImGui.Button($"❓##maybe{dto.Id}"))
            {
                SendRsvp(dto.Id, "❓");
            }

            ImGui.Separator();
        }

        ImGui.EndChild();
        ImGui.End();
    }

    private void SendRsvp(string id, string emoji)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post,
                $"{_config.HelperBaseUrl.TrimEnd('/')}/embeds/{id}/rsvp");
            request.Content = new StringContent(JsonSerializer.Serialize(new { Emoji = emoji }), Encoding.UTF8,
                "application/json");
            if (!string.IsNullOrEmpty(_config.AuthToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AuthToken);
            }

            _httpClient.SendAsync(request);
        }
        catch
        {
            // ignored
        }
    }

    public void Dispose()
    {
        foreach (var state in _embeds.Values)
        {
            DisposeState(state);
        }
        _embeds.Clear();
        _httpClient.Dispose();
    }
}


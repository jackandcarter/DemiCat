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
using System.Diagnostics;

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

        if (!string.IsNullOrEmpty(dto.Title))
        {
            if (!string.IsNullOrEmpty(dto.Url))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.2f, 0.6f, 1f, 1f));
                ImGui.TextUnformatted(dto.Title);
                ImGui.PopStyleColor();
                if (ImGui.IsItemClicked())
                {
                    try { Process.Start(new ProcessStartInfo(dto.Url) { UseShellExecute = true }); } catch { }
                }
            }
            else
            {
                ImGui.TextUnformatted(dto.Title);
            }
        }
        if (dto.Timestamp.HasValue)
        {
            if (!string.IsNullOrEmpty(dto.Title))
            {
                ImGui.SameLine();
            }
            ImGui.TextUnformatted($"- {dto.Timestamp.Value.LocalDateTime}");
        }

        if (!string.IsNullOrEmpty(dto.Description))
        {
            ImGui.TextWrapped(dto.Description);
        }

        if (dto.Fields != null && dto.Fields.Count > 0)
        {
            var fields = dto.Fields;
            var index = 0;
            while (index < fields.Count)
            {
                if (fields[index].Inline == true)
                {
                    var group = new List<EmbedFieldDto>();
                    while (index < fields.Count && fields[index].Inline == true)
                    {
                        group.Add(fields[index]);
                        index++;
                    }
                    var cols = Math.Min(3, group.Count);
                    if (ImGui.BeginTable($"ifields{dto.Id}{index}", cols, ImGuiTableFlags.Borders))
                    {
                        for (var i = 0; i < group.Count; i++)
                        {
                            if (i % cols == 0)
                            {
                                ImGui.TableNextRow();
                            }
                            ImGui.TableSetColumnIndex(i % cols);
                            var f = group[i];
                            ImGui.TextUnformatted(f.Name);
                            ImGui.TextWrapped(f.Value);
                        }
                        ImGui.EndTable();
                    }
                }
                else
                {
                    var f = fields[index];
                    index++;
                    ImGui.TextUnformatted(f.Name);
                    ImGui.TextWrapped(f.Value);
                }
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

        DrawButtons();

        ImGui.Separator();
    }

    public void DrawButtons()
    {
        if (Buttons != null)
        {
            foreach (var button in Buttons)
            {
                var id = button.CustomId ?? button.Label;
                var text = string.IsNullOrEmpty(button.Emoji) ? button.Label : $"{button.Emoji} {button.Label}";
                var styled = button.Style.HasValue;
                if (styled)
                {
                    var color = GetStyleColor(button.Style!.Value);
                    ImGui.PushStyleColor(ImGuiCol.Button, color);
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Lighten(color, 1.1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, Lighten(color, 1.2f));
                }
                if (ImGui.Button($"{text}##{id}{_dto.Id}", new Vector2(-1, 0)))
                {
                    _ = SendInteraction(id);
                }
                if (styled)
                {
                    ImGui.PopStyleColor(3);
                }
            }
        }

        if (!string.IsNullOrEmpty(_lastResult))
        {
            ImGui.TextUnformatted(_lastResult);
        }
    }

    private static Vector4 GetStyleColor(ButtonStyle style)
    {
        return style switch
        {
            ButtonStyle.Primary => new Vector4(0.345f, 0.396f, 0.949f, 1f),
            ButtonStyle.Secondary => new Vector4(0.31f, 0.329f, 0.361f, 1f),
            ButtonStyle.Success => new Vector4(0.341f, 0.949f, 0.529f, 1f),
            ButtonStyle.Danger => new Vector4(0.929f, 0.258f, 0.27f, 1f),
            _ => new Vector4(0.345f, 0.396f, 0.949f, 1f),
        };
    }

    private static Vector4 Lighten(Vector4 color, float amount)
    {
        return new Vector4(
            MathF.Min(color.X * amount, 1f),
            MathF.Min(color.Y * amount, 1f),
            MathF.Min(color.Z * amount, 1f),
            color.W);
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
                var wrap = PluginServices.Instance!.TextureProvider.CreateFromRaw(
                    RawImageSpecification.Rgba32(image.Width, image.Height),
                    image.Data);
                var texture = new ForwardingSharedImmediateTexture(wrap);
                _ = PluginServices.Instance!.Framework.RunOnTick(() => set(texture));
            }
            catch
            {
                _ = PluginServices.Instance!.Framework.RunOnTick(() => set(null));
            }
        });
    }

    public async Task SendInteraction(string customId)
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config))
        {
            PluginServices.Instance!.Log.Warning("Cannot send interaction: API base URL is not configured.");
            _lastResult = "Invalid API URL";
            return;
        }

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/interactions");
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

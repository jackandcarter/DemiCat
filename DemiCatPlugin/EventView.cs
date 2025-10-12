using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.IO;
using DiscordHelper;
using Dalamud.Interface.Textures;
using Dalamud.Bindings.ImGui;
using StbImageSharp;
using System.Diagnostics;
using System.Linq;
using DemiCatPlugin.Emoji;

namespace DemiCatPlugin;

public class EventView : IDisposable
{
    private readonly Config _config;
    private readonly HttpClient _httpClient;
    private readonly Func<Task> _refresh;
    private readonly EmojiManager _emojiManager;

    private EmbedDto _dto;
    private ISharedImmediateTexture? _authorIcon;
    private ISharedImmediateTexture? _thumbnail;
    private ISharedImmediateTexture? _image;
    private ISharedImmediateTexture? _footerIcon;
    private string? _lastResult;
    private string? _content;
    private readonly List<string> _warnings = new();
    private static readonly Regex RoleMentionRegex = new("<@&(\\d+)>", RegexOptions.Compiled);

    public EventView(EmbedDto dto, Config config, HttpClient httpClient, Func<Task> refresh, EmojiManager emojiManager, string? content = null, IEnumerable<string>? warnings = null)
    {
        _config = config;
        _httpClient = httpClient;
        _refresh = refresh;
        _emojiManager = emojiManager;
        _dto = dto;
        _content = content;
        SetWarnings(warnings);
        LoadTexture(dto.AuthorIconUrl, t => _authorIcon = t);
        LoadTexture(dto.FooterIconUrl, t => _footerIcon = t);
        LoadTexture(dto.ThumbnailUrl, t => _thumbnail = t);
        LoadTexture(dto.ImageUrl, t => _image = t);
    }

    public void Update(EmbedDto dto, string? content = null, IEnumerable<string>? warnings = null)
    {
        if (_dto.AuthorIconUrl != dto.AuthorIconUrl)
        {
            LoadTexture(dto.AuthorIconUrl, t => _authorIcon = t);
        }
        if (_dto.FooterIconUrl != dto.FooterIconUrl)
        {
            LoadTexture(dto.FooterIconUrl, t => _footerIcon = t);
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
        _content = content;
        SetWarnings(warnings);
    }

    private void SetWarnings(IEnumerable<string>? warnings)
    {
        _warnings.Clear();
        if (warnings == null)
        {
            return;
        }

        foreach (var warning in warnings)
        {
            if (!string.IsNullOrWhiteSpace(warning) && !_warnings.Contains(warning))
            {
                _warnings.Add(warning);
            }
        }
    }

    private string? BuildDisplayContent()
    {
        var raw = _content;
        if (string.IsNullOrWhiteSpace(raw) && _dto.Mentions != null && _dto.Mentions.Count > 0)
        {
            raw = string.Join(" ", _dto.Mentions.Select(id => $"<@&{id}>"));
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string ReplaceMention(Match match)
        {
            var id = match.Groups[1].Value;
            foreach (var role in RoleCache.Roles)
            {
                if (role.Id == id && !string.IsNullOrWhiteSpace(role.Name))
                {
                    return $"@{role.Name}";
                }
            }

            return $"@{id}";
        }

        var replaced = RoleMentionRegex.Replace(raw, ReplaceMention);
        replaced = replaced.Replace("<@everyone>", "@everyone").Replace("<@here>", "@here");
        return replaced;
    }

    public string ChannelId => _dto.ChannelId?.ToString() ?? string.Empty;

    public string GuildId => _dto.GuildId ?? string.Empty;

    public IReadOnlyList<EmbedButtonDto>? Buttons => _dto.Buttons;

    public void Draw()
        => Draw(null);

    public void Draw(float? availableHeight)
    {
        using var emojiFont = _emojiManager.PushEmojiFont();
        var dto = _dto;
        var cursorStart = ImGui.GetCursorPosY();

        if (_warnings.Count > 0)
        {
            var warnColor = new Vector4(1f, 0.75f, 0f, 1f);
            foreach (var warning in _warnings)
            {
                ImGui.TextColored(warnColor, $"⚠ {warning}");
            }
            UiTheme.DrawSectionSeparator();
        }

        var mentionText = BuildDisplayContent();
        if (!string.IsNullOrWhiteSpace(mentionText))
        {
            ImGui.TextWrapped(mentionText);
            ImGui.Spacing();
        }

        float? embedHeight = null;
        if (availableHeight.HasValue)
        {
            var usedHeight = ImGui.GetCursorPosY() - cursorStart;
            var footerReserve = EstimateFooterHeight();
            var remaining = availableHeight.Value - usedHeight - footerReserve;
            if (remaining > 0f)
            {
                embedHeight = remaining;
            }
        }

        DrawEmbed(dto, embedHeight);

        DrawButtons();

        UiTheme.DrawSectionSeparator();
    }

    private float EstimateFooterHeight()
    {
        var style = ImGui.GetStyle();
        var reserve = style.ItemSpacing.Y;

        if (EventEmbedHelpers.ShouldDisplayStartTime(_dto))
        {
            reserve += ImGui.GetTextLineHeight() + style.ItemSpacing.Y;
        }

        if (Buttons != null && Buttons.Count > 0)
        {
            var rowCount = Buttons
                .Where(b => b != null)
                .Select(b => b!.RowIndex ?? 0)
                .Distinct()
                .Count();

            if (rowCount > 0)
            {
                reserve += rowCount * ImGui.GetFrameHeightWithSpacing();
            }
        }
        else if (EventEmbedHelpers.IsApolloEvent(_dto))
        {
            reserve += 3 * ImGui.GetFrameHeightWithSpacing();
        }

        if (!string.IsNullOrEmpty(_lastResult))
        {
            reserve += ImGui.GetTextLineHeightWithSpacing();
        }

        return reserve;
    }

    private void DrawEmbed(EmbedDto dto, float? availableHeight)
    {
        using var emojiFont = _emojiManager.PushEmojiFont();
        const float stripeWidth = 4f;
        const float contentPadding = 8f;
        const float verticalPadding = 4f;

        var indent = contentPadding + (dto.Color.HasValue ? stripeWidth : 0f);
        var availWidth = ImGui.GetContentRegionAvail().X;
        if (availWidth <= 0)
        {
            availWidth = 400f;
        }

        float childHeight;
        if (availableHeight.HasValue)
        {
            childHeight = Math.Max(availableHeight.Value, 0f);
            if (!float.IsFinite(childHeight))
            {
                childHeight = 0f;
            }
        }
        else
        {
            childHeight = 0f;
        }

        ImGui.BeginChild($"eventEmbed{dto.Id}", new Vector2(availWidth, childHeight), false);

        ImGui.Indent(indent);
        ImGui.Dummy(new Vector2(0f, verticalPadding));
        DrawEmbedSections(dto);
        ImGui.Dummy(new Vector2(0f, verticalPadding));
        ImGui.Unindent(indent);

        ImGui.EndChild();

        if (dto.Color.HasValue)
        {
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            var color = ColorUtils.RgbToImGui(dto.Color.Value);
            ImGui.GetWindowDrawList().AddRectFilled(min, new Vector2(min.X + stripeWidth, max.Y), color);
        }
    }

    private void DrawEmbedSections(EmbedDto dto)
    {
        var hasContent = false;

        void BeginSection()
        {
            if (hasContent)
            {
                ImGui.Spacing();
            }
            else
            {
                hasContent = true;
            }
        }

        if (!string.IsNullOrEmpty(dto.ProviderName))
        {
            BeginSection();
            ImGui.TextUnformatted(dto.ProviderName);
        }

        if (dto.Authors != null && dto.Authors.Count > 0)
        {
            var names = dto.Authors
                .Where(a => !string.IsNullOrWhiteSpace(a.Name))
                .Select(a => a.Name!)
                .ToList();

            if (names.Count > 0 || _authorIcon != null)
            {
                BeginSection();
                DrawAuthorSection(names);
            }
        }
        else if (!string.IsNullOrWhiteSpace(dto.AuthorName))
        {
            BeginSection();
            ImGui.TextUnformatted(dto.AuthorName);
        }

        if (!string.IsNullOrEmpty(dto.Title))
        {
            BeginSection();
            DrawTitleSection(dto);
        }

        var description = EventEmbedHelpers.RemoveAppendedStartLine(dto.Description, dto.Timestamp, out _);
        if (!string.IsNullOrEmpty(description))
        {
            BeginSection();
            ImGui.TextWrapped(description);
        }

        if (_thumbnail != null)
        {
            BeginSection();
            DrawThumbnailSection();
        }

        if (dto.Fields != null && dto.Fields.Count > 0)
        {
            BeginSection();
            DrawFields(dto);
        }

        if (_image != null)
        {
            BeginSection();
            DrawImageSection();
        }

        if (_footerIcon != null || !string.IsNullOrEmpty(dto.FooterText))
        {
            BeginSection();
            DrawFooterSection(dto);
        }
    }

    private void DrawAuthorSection(IReadOnlyList<string> names)
    {
        if (_authorIcon != null)
        {
            var wrap = _authorIcon.GetWrapOrEmpty();
            ImGui.Image(wrap.Handle, new Vector2(32, 32));
            if (names.Count > 0)
            {
                ImGui.SameLine();
            }
        }

        if (names.Count > 0)
        {
            ImGui.TextUnformatted(string.Join(", ", names));
        }
    }

    private void DrawTitleSection(EmbedDto dto)
    {
        if (string.IsNullOrEmpty(dto.Title))
        {
            return;
        }

        if (!string.IsNullOrEmpty(dto.Url))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.2f, 0.6f, 1f, 1f));
            ImGui.TextUnformatted(dto.Title);
            ImGui.PopStyleColor();
            if (ImGui.IsItemClicked())
            {
                try
                {
                    Process.Start(new ProcessStartInfo(dto.Url) { UseShellExecute = true });
                }
                catch
                {
                    // ignored
                }
            }
        }
        else
        {
            ImGui.TextUnformatted(dto.Title);
        }
    }

    private static void DrawFields(EmbedDto dto)
    {
        var fields = dto.Fields!;
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
                        if (!string.IsNullOrEmpty(f.Name))
                        {
                            ImGui.TextUnformatted(f.Name);
                        }
                        if (!string.IsNullOrEmpty(f.Value))
                        {
                            ImGui.TextWrapped(f.Value);
                        }
                    }

                    ImGui.EndTable();
                }
            }
            else
            {
                var f = fields[index];
                index++;
                if (!string.IsNullOrEmpty(f.Name))
                {
                    ImGui.TextUnformatted(f.Name);
                }
                if (!string.IsNullOrEmpty(f.Value))
                {
                    ImGui.TextWrapped(f.Value);
                }
            }
        }
    }

    private void DrawThumbnailSection()
    {
        if (_thumbnail == null)
        {
            return;
        }

        var wrap = _thumbnail.GetWrapOrEmpty();
        ImGui.Image(wrap.Handle, new Vector2(wrap.Width, wrap.Height));
    }

    private void DrawImageSection()
    {
        if (_image == null)
        {
            return;
        }

        var wrap = _image.GetWrapOrEmpty();
        ImGui.Image(wrap.Handle, new Vector2(wrap.Width, wrap.Height));
    }

    private void DrawFooterSection(EmbedDto dto)
    {
        var parts = new List<string>(capacity: 2);

        if (!string.IsNullOrWhiteSpace(dto.FooterText))
        {
            parts.Add(dto.FooterText!.Trim());
        }

        var hasText = parts.Count > 0;

        if (_footerIcon != null)
        {
            var wrap = _footerIcon.GetWrapOrEmpty();
            ImGui.Image(wrap.Handle, new Vector2(16, 16));
            if (hasText)
            {
                ImGui.SameLine();
            }
        }

        if (hasText)
        {
            var footerText = string.Join(" • ", parts);
            ImGui.TextUnformatted(footerText);
        }
    }

    public void DrawButtons()
    {
        using var emojiFont = _emojiManager.PushEmojiFont();
        var showStartTime = EventEmbedHelpers.ShouldDisplayStartTime(_dto);
        if (showStartTime)
        {
            var label = $"Starts: {EventEmbedHelpers.FormatLocalStartTime(_dto.Timestamp!.Value)}";
            ImGui.TextUnformatted(label);
            ImGui.Spacing();
        }

        if (Buttons != null && Buttons.Count > 0)
        {
            foreach (var row in Buttons
                         .GroupBy(b => b.RowIndex ?? 0)
                         .OrderBy(g => g.Key))
            {
                var buttons = row.ToList();
                for (var i = 0; i < buttons.Count; i++)
                {
                    if (i > 0) ImGui.SameLine();
                    var button = buttons[i];
                    if (!string.IsNullOrEmpty(button.Emoji))
                    {
                        EmojiRenderer.Draw(button.Emoji, _emojiManager);
                        ImGui.SameLine();
                    }
                    var id = button.CustomId ?? button.Label;
                    var styled = button.Style.HasValue && button.Style.Value != ButtonStyle.Link;
                    if (styled)
                    {
                        var color = EmbedPreviewRenderer.GetStyleColor(button.Style!.Value);
                        ImGui.PushStyleColor(ImGuiCol.Button, color);
                        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, EmbedPreviewRenderer.Lighten(color, 1.1f));
                        ImGui.PushStyleColor(ImGuiCol.ButtonActive, EmbedPreviewRenderer.Lighten(color, 1.2f));
                    }

                    var w = button.Width ?? -1;
                    if (ImGui.Button($"{button.Label}##{id}{_dto.Id}", new Vector2(w, 0)))
                    {
                        if (!string.IsNullOrEmpty(button.Url))
                        {
                            try { Process.Start(new ProcessStartInfo(button.Url) { UseShellExecute = true }); } catch { }
                        }
                        else if (!string.IsNullOrEmpty(button.CustomId))
                        {
                            _ = SendInteraction(button.CustomId);
                        }
                    }

                    if (styled)
                    {
                        ImGui.PopStyleColor(3);
                    }
                }
            }
        }
        else if (EventEmbedHelpers.IsApolloEvent(_dto))
        {
            if (ImGui.Button($"Yes##rsvpYes{_dto.Id}", new Vector2(-1, 0)))
            {
                _ = SendInteraction("rsvp:yes");
            }
            if (ImGui.Button($"Maybe##rsvpMaybe{_dto.Id}", new Vector2(-1, 0)))
            {
                _ = SendInteraction("rsvp:maybe");
            }
            if (ImGui.Button($"No##rsvpNo{_dto.Id}", new Vector2(-1, 0)))
            {
                _ = SendInteraction("rsvp:no");
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
            var tag = customId.Contains(':') ? customId.Split(':', 2)[1] : customId;
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/events/{_dto.Id}/rsvp");
            var body = new { tag = tag };
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            ApiHelpers.AddAuthHeader(request, TokenManager.Instance!);
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

        (_footerIcon as IDisposable)?.Dispose();
        _footerIcon = null;
    }
}

internal static class EventViewImGuiHelpers
{
    public static float BeginPreviewChild(string childId, bool border = false, float minHeight = 0f, ImGuiWindowFlags flags = ImGuiWindowFlags.None)
    {
        var avail = ImGui.GetContentRegionAvail();
        var width = float.IsFinite(avail.X) ? Math.Max(avail.X, 0f) : 0f;
        var height = float.IsFinite(avail.Y) ? Math.Max(avail.Y, 0f) : 0f;

        if (minHeight > 0f)
        {
            height = Math.Max(height, minHeight);
        }

        var size = new Vector2(width, height);
        ImGui.BeginChild(childId, size, border, flags);
        return size.Y;
    }
}

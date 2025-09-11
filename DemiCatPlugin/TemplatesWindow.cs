using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Numerics;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using DiscordHelper;
using DemiCat.UI;
using static DemiCat.UI.IdHelpers;

namespace DemiCatPlugin;

public class TemplatesWindow
{
    private readonly Config _config;
    private readonly HttpClient _httpClient;
    private readonly List<TemplateItem> _templates = new();
    private bool _templatesLoaded;
    private int _selectedIndex = -1;
    private bool _showPreview;
    private EventView? _previewEvent;
    private string? _lastResult;
    private readonly List<ChannelDto> _channels = new();
    private bool _channelsLoaded;
    private bool _channelFetchFailed;
    private string _channelErrorMessage = string.Empty;
    private int _channelIndex;
    private string _channelId = string.Empty;
    private bool _confirmPost;
    private Template? _pendingTemplate;
    private readonly List<RoleDto> _roles = new();
    private readonly HashSet<string> _mentions = new();
    private bool _rolesLoaded;
    private ButtonRows _buttonRows = new(new()
    {
        new() { new ButtonData { Label = "RSVP: Yes" }, new ButtonData { Label = "RSVP: Maybe" } },
        new() { new ButtonData { Label = "RSVP: No" } }
    });

    private readonly ChatBridge? _bridge;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public TemplatesWindow(Config config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
        _channelId = config.EventChannelId;
        var token = TokenManager.Instance;
        if (token != null)
        {
            _bridge = new ChatBridge(config, httpClient, token, BuildWebSocketUri);
            _bridge.TemplatesUpdated += () => _ = LoadTemplates();
        }
    }

    public void StartNetworking()
    {
        _bridge?.Start();
        _templatesLoaded = false;
    }

    public void StopNetworking()
    {
        _bridge?.Stop();
    }

    public void Draw()
    {
        if (!_config.Templates)
        {
            ImGui.TextUnformatted("Feature disabled");
            return;
        }

        if (TokenManager.Instance?.IsReady() != true)
        {
            ImGui.TextUnformatted("Link DemiCat to manage templates");
            return;
        }

        if (!_rolesLoaded)
        {
            _ = LoadRoles();
        }

        if (!_channelsLoaded)
        {
            _ = FetchChannels();
        }
        if (!_templatesLoaded)
        {
            _ = LoadTemplates();
        }
        if (_channels.Count > 0)
        {
            var channelNames = _channels.Select(c => c.Name).ToArray();
            if (ImGui.Combo("Channel", ref _channelIndex, channelNames, channelNames.Length))
            {
                _channelId = _channels[_channelIndex].Id;
                _config.EventChannelId = _channelId;
                SaveConfig();
            }
        }
        else
        {
            ImGui.TextUnformatted(_channelFetchFailed ? _channelErrorMessage : "No channels available");
        }

        ImGui.BeginChild("TemplateList", new Vector2(150, 0), true);
        for (var i = 0; i < _templates.Count; i++)
        {
            var tmplItem = _templates[i];
            var name = tmplItem.Template.Name;
            if (ImGui.Selectable(name, _selectedIndex == i))
            {
                SelectTemplate(i, tmplItem.Template);
            }
        }
        ImGui.EndChild();

        ImGui.SameLine();

        ImGui.BeginChild("TemplateContent", ImGui.GetContentRegionAvail(), false);
        if (_selectedIndex >= 0 && _selectedIndex < _templates.Count)
        {
            var tmpl = _templates[_selectedIndex].Template;
            if (ImGui.Button("Preview"))
            {
                OpenPreview(tmpl);
            }
            ImGui.SameLine();
            if (ImGui.Button("Post"))
            {
                _pendingTemplate = tmpl;
                _confirmPost = true;
            }
            ImGui.SameLine();
            if (ImGui.Button("Delete"))
            {
                var id = _templates[_selectedIndex].Id;
                _ = DeleteTemplate(id);
            }

            if (_rolesLoaded)
            {
                if (_roles.Count > 0)
                {
                    ImGui.Separator();
                    ImGui.Text("Mention Roles");
                    foreach (var role in _roles)
                    {
                        var roleId = role.Id;
                        var sel = _mentions.Contains(roleId);
                        if (ImGui.Checkbox($"{role.Name}##role{role.Id}", ref sel))
                        {
                            if (sel) _mentions.Add(roleId); else _mentions.Remove(roleId);
                        }
                    }
                }
                else
                {
                    var msg = RoleCache.LastErrorMessage ?? "No roles available";
                    ImGui.TextUnformatted(msg);
                }
            }

            ButtonRowsImGui.Draw(_buttonRows, "template-button-rows");
            if (_confirmPost)
                ImGui.OpenPopup("Confirm Template Post");
            var openConfirm = _confirmPost;
            if (_confirmPost && ImGui.BeginPopupModal("Confirm Template Post", ref openConfirm, ImGuiWindowFlags.AlwaysAutoResize))
            {
                var channelName = _channels.FirstOrDefault(c => c.Id == _channelId)?.Name ?? _channelId;
                ImGui.TextUnformatted($"Channel: {channelName}");
                var roleNames = _roles.Where(r => _mentions.Contains(r.Id)).Select(r => r.Name).ToList();
                ImGui.TextUnformatted("Roles: " + (roleNames.Count > 0 ? string.Join(", ", roleNames) : "None"));
                var buttonsCount = _buttonRows.FlattenNonEmpty().Count();
                bool canConfirm = !string.IsNullOrWhiteSpace(_channelId)
                    && DiscordValidation.IsImageUrlAllowed(tmpl.ImageUrl)
                    && DiscordValidation.IsImageUrlAllowed(tmpl.ThumbnailUrl)
                    && buttonsCount > 0;
                ImGui.BeginDisabled(!canConfirm);
                if (ImGui.Button("Confirm"))
                {
                    if (_pendingTemplate != null)
                        _ = PostTemplate(_pendingTemplate);
                    _confirmPost = false;
                    _pendingTemplate = null;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndDisabled();
                ImGui.SameLine();
                if (ImGui.Button("Cancel"))
                {
                    _confirmPost = false;
                    _pendingTemplate = null;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndPopup();
            }
            if (!_confirmPost || !openConfirm)
            {
                _confirmPost = false;
                _pendingTemplate = null;
            }
        }
        else
        {
            ImGui.TextUnformatted("Select a template");
        }
        ImGui.EndChild();

        if (!string.IsNullOrEmpty(_lastResult))
        {
            ImGui.TextUnformatted(_lastResult);
        }

        if (_showPreview && _previewEvent != null)
        {
            if (ImGui.Begin("Template Preview", ref _showPreview))
            {
                _previewEvent.Draw();
            }
            ImGui.End();
        }
    }

    internal void SelectTemplate(int index, Template tmplItem)
    {
        _previewEvent?.Dispose();
        _previewEvent = null;

        _selectedIndex = index;
        _showPreview = false;
        _mentions.Clear();
        if (tmplItem.Mentions != null)
        {
            foreach (var m in tmplItem.Mentions)
                _mentions.Add(m.ToString());
        }

        var buttonsList = tmplItem.Buttons ?? new List<Template.TemplateButton>();
        var changed = false;
        foreach (var btn in buttonsList)
        {
            if (string.IsNullOrWhiteSpace(btn.Tag))
            {
                btn.Tag = Guid.NewGuid().ToString();
                changed = true;
            }
        }

        List<List<ButtonData>> rowsInit = buttonsList
            .Where(b => b.Include && !string.IsNullOrWhiteSpace(b.Label))
            .Chunk(5)
            .Select(chunk => chunk.Select(b => new ButtonData
            {
                Tag = b.Tag,
                Label = b.Label,
                Style = b.Style,
                Emoji = string.IsNullOrWhiteSpace(b.Emoji) ? null : b.Emoji,
                MaxSignups = b.MaxSignups,
                Width = b.Width
            }).ToList())
            .ToList();

        if (changed)
        {
            SaveConfig();
        }

        _buttonRows = new ButtonRows(
            rowsInit.Count != 0
                ? rowsInit
                : new()
                {
                    new() { new ButtonData { Label = "RSVP: Yes" }, new ButtonData { Label = "RSVP: Maybe" } },
                    new() { new ButtonData { Label = "RSVP: No" } }
                });
    }

    internal void OpenPreview(Template tmpl)
    {
        _previewEvent?.Dispose();
        _previewEvent = new EventView(ToEmbedDto(tmpl), _config, _httpClient, () => Task.CompletedTask);
        _showPreview = true;
    }

    private void SaveConfig()
    {
        PluginServices.Instance!.PluginInterface.SavePluginConfig(_config);
    }

    public Task RefreshChannels()
    {
        if (!_config.Templates)
        {
            _channelsLoaded = true;
            return Task.CompletedTask;
        }
        _channelsLoaded = false;
        _channelFetchFailed = false;
        _channelErrorMessage = string.Empty;
        return FetchChannels();
    }

    private async Task FetchChannels(bool refreshed = false)
    {
        if (!_config.Templates)
        {
            _channelsLoaded = true;
            return;
        }
        if (!ApiHelpers.ValidateApiBaseUrl(_config))
        {
            PluginServices.Instance!.Log.Warning("Cannot fetch channels: API base URL is not configured.");
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _channelFetchFailed = true;
                _channelErrorMessage = "Invalid API URL";
                _channelsLoaded = true;
            });
            return;
        }

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/channels");
            ApiHelpers.AddAuthHeader(request, TokenManager.Instance!);
            var response = await ApiHelpers.SendWithRetries(request, _httpClient);
            if (response == null)
            {
                _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    _channelFetchFailed = true;
                    _channelErrorMessage = "Failed to load channels";
                    _channelsLoaded = true;
                });
                return;
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                PluginServices.Instance!.Log.Warning($"Failed to fetch channels. Status: {response.StatusCode}. Response Body: {responseBody}");
                _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    _channelFetchFailed = true;
                    _channelErrorMessage = response.StatusCode == HttpStatusCode.Unauthorized
                        ? "Authentication failed"
                        : "Forbidden \u2013 check API key/roles";
                    _channelsLoaded = true;
                });
                return;
            }
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                PluginServices.Instance!.Log.Warning($"Failed to fetch channels. Status: {response.StatusCode}. Response Body: {responseBody}");
                _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    _channelFetchFailed = true;
                    _channelErrorMessage = "Failed to load channels";
                    _channelsLoaded = true;
                });
                return;
            }
            var stream = await response.Content.ReadAsStreamAsync();
            var dto = await JsonSerializer.DeserializeAsync<ChannelListDto>(stream) ?? new ChannelListDto();
            if (await ChannelNameResolver.Resolve(dto.Event, _httpClient, _config, refreshed, () => FetchChannels(true))) return;
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                SetChannels(dto.Event);
                _channelsLoaded = true;
                _channelFetchFailed = false;
                _channelErrorMessage = string.Empty;
            });
        }
        catch (HttpRequestException ex)
        {
            PluginServices.Instance!.Log.Warning($"Failed to fetch channels. Status: {ex.StatusCode}");
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _channelFetchFailed = true;
                _channelErrorMessage = ex.StatusCode == HttpStatusCode.Unauthorized
                    ? "Authentication failed"
                    : ex.StatusCode == HttpStatusCode.Forbidden
                        ? "Forbidden \u2013 check API key/roles"
                        : "Failed to load channels";
                _channelsLoaded = true;
            });
        }
        catch (Exception ex)
        {
            PluginServices.Instance!.Log.Error(ex, "Error fetching channels");
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _channelFetchFailed = true;
                _channelErrorMessage = "Failed to load channels";
                _channelsLoaded = true;
            });
        }
    }

    private void SetChannels(List<ChannelDto> channels)
    {
        _channels.Clear();
        _channels.AddRange(channels);
        if (!string.IsNullOrEmpty(_channelId))
        {
            _channelIndex = _channels.FindIndex(c => c.Id == _channelId);
            if (_channelIndex < 0) _channelIndex = 0;
        }
        if (_channels.Count > 0)
        {
            _channelId = _channels[_channelIndex].Id;
        }
    }

    private async Task LoadRoles()
    {
        await RoleCache.EnsureLoaded(_httpClient, _config);
        _ = PluginServices.Instance!.Framework.RunOnTick(() =>
        {
            _roles.Clear();
            _roles.AddRange(RoleCache.Roles);
            _rolesLoaded = true;
        });
    }

    public void ResetRoles()
    {
        RoleCache.Reset();
        _roles.Clear();
        _rolesLoaded = false;
    }

    private class ChannelListDto
    {
        [JsonPropertyName(ChannelKind.Event)] public List<ChannelDto> Event { get; set; } = new();
    }

    private record TemplateItem(string Id, Template Template);

    private class TemplateDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public TemplatePayloadDto Payload { get; set; } = new();
    }

    private class TemplatePayloadDto
    {
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? Time { get; set; }
        public string? Url { get; set; }
        public string? ImageUrl { get; set; }
        public string? ThumbnailUrl { get; set; }
        public uint? Color { get; set; }
        public List<EmbedFieldDto>? Fields { get; set; }
        public List<EmbedButtonDto>? Buttons { get; set; }
        public List<string>? Mentions { get; set; }
    }

    internal record ButtonPayload(
        string label,
        string customId,
        int rowIndex,
        int style,
        string? emoji,
        int? maxSignups,
        int? width);

    internal List<ButtonPayload> BuildButtonsPayload(Template tmpl)
    {
        var srcButtons = (tmpl.Buttons ?? new List<Template.TemplateButton>())
            .Where(b => !string.IsNullOrWhiteSpace(b.Label) && !string.IsNullOrWhiteSpace(b.Tag))
            .ToDictionary(b => b.Tag!);
        return _buttonRows.FlattenNonEmpty()
            .Select(x =>
            {
                var label = x.Data.Label.Trim();
                srcButtons.TryGetValue(x.Data.Tag, out var b);
                return new ButtonPayload(
                    Truncate(label, 80),
                    MakeCustomId(label, x.RowIndex, x.ColIndex),
                    x.RowIndex,
                    (int)(b?.Style ?? x.Data.Style),
                    NormalizeEmoji(b?.Emoji ?? x.Data.Emoji),
                    b?.MaxSignups ?? x.Data.MaxSignups,
                    Math.Min(b?.Width ?? x.Data.Width ?? ButtonSizeHelper.ComputeWidth(label), ButtonSizeHelper.Max));
            })
            .ToList();
    }

    private static string? NormalizeEmoji(string? emoji)
    {
        if (string.IsNullOrWhiteSpace(emoji)) return null;
        if (emoji.StartsWith("custom:", StringComparison.OrdinalIgnoreCase))
        {
            var id = emoji.Substring("custom:".Length);
            var name = EmojiPopup.LookupGuildName(id) ?? "emoji";
            var animated = EmojiPopup.IsGuildEmojiAnimated(id);
            return $"<{(animated ? "a" : string.Empty)}:{name}:{id}>";
        }
        return emoji;
    }


    internal EmbedDto ToEmbedDto(Template tmpl)
    {
        DateTimeOffset? ts = null;
        if (!string.IsNullOrWhiteSpace(tmpl.Time) && DateTimeOffset.TryParse(tmpl.Time, out var parsed))
        {
            ts = parsed;
        }

        var buttons = BuildButtonsPayload(tmpl)
            .Select(b => new EmbedButtonDto
            {
                Label = b.label,
                CustomId = b.customId,
                Style = (ButtonStyle)b.style,
                Emoji = b.emoji,
                MaxSignups = b.maxSignups,
                Width = b.width,
                RowIndex = b.rowIndex
            })
            .ToList();

        return new EmbedDto
        {
            Title = tmpl.Title,
            Description = tmpl.Description,
            Url = string.IsNullOrWhiteSpace(tmpl.Url) ? null : tmpl.Url,
            Timestamp = ts,
            ImageUrl = string.IsNullOrWhiteSpace(tmpl.ImageUrl) ? null : tmpl.ImageUrl,
            ThumbnailUrl = string.IsNullOrWhiteSpace(tmpl.ThumbnailUrl) ? null : tmpl.ThumbnailUrl,
            Color = tmpl.Color != 0 ? (uint?)tmpl.Color : null,
            Fields = tmpl.Fields?.Select(f => new EmbedFieldDto { Name = f.Name, Value = f.Value, Inline = f.Inline }).ToList(),
            Buttons = buttons.Count > 0 ? buttons : null,
            Mentions = _mentions.Count > 0 ? _mentions.Select(ulong.Parse).ToList() : null
        };
    }

    private async Task PostTemplate(Template tmpl)
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config) || string.IsNullOrWhiteSpace(_channelId))
        {
            if (string.IsNullOrWhiteSpace(_channelId))
            {
                _lastResult = "No channel selected";
            }
            return;
        }
        try
        {
            var buttonsFlat = BuildButtonsPayload(tmpl);
            var body = new
            {
                channelId = _channelId,
                time = string.IsNullOrWhiteSpace(tmpl.Time) ? null : tmpl.Time,
                buttons = buttonsFlat.Count > 0 ? buttonsFlat : null,
                mentions = _mentions.Count > 0 ? _mentions.Select(ulong.Parse).ToList() : null
            };
            var id = _templates[_selectedIndex].Id;
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/templates/{id}/post");
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            ApiHelpers.AddAuthHeader(request, TokenManager.Instance!);
            var response = await ApiHelpers.SendWithRetries(request, _httpClient);
            if (response?.IsSuccessStatusCode == true)
            {
                _lastResult = "Template posted";
            }
            else if (response != null)
            {
                var bodyText = await response.Content.ReadAsStringAsync();
                _lastResult = $"Failed to post template: {(int)response.StatusCode} {bodyText}";
            }
            else
            {
                _lastResult = "Failed to post template";
            }
        }
        catch
        {
            _lastResult = "Failed to post template";
        }
    }

    private async Task LoadTemplates()
    {
        if (!_config.Templates)
        {
            _templatesLoaded = true;
            return;
        }
        if (!ApiHelpers.ValidateApiBaseUrl(_config))
        {
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _templatesLoaded = true;
                _lastResult = "Invalid API URL";
            });
            return;
        }
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/templates");
            ApiHelpers.AddAuthHeader(request, TokenManager.Instance!);
            var response = await ApiHelpers.SendWithRetries(request, _httpClient);
            if (response == null || !response.IsSuccessStatusCode)
            {
                var bodyText = response != null ? await response.Content.ReadAsStringAsync() : string.Empty;
                _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    _templatesLoaded = true;
                    _lastResult = string.IsNullOrEmpty(bodyText)
                        ? "Failed to load templates"
                        : $"Failed to load templates: {(int)response!.StatusCode} {bodyText}";
                });
                return;
            }
            var stream = await response.Content.ReadAsStreamAsync();
            var dtoList = await JsonSerializer.DeserializeAsync<List<TemplateDto>>(stream, JsonOpts) ?? new();
            var items = dtoList.Select(d => new TemplateItem(d.Id, ConvertTemplate(d))).ToList();
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _templates.Clear();
                _templates.AddRange(items);
                _templatesLoaded = true;
            });
        }
        catch
        {
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _templatesLoaded = true;
                _lastResult = "Failed to load templates";
            });
        }
    }

    private Template ConvertTemplate(TemplateDto dto)
    {
        var payload = dto.Payload;
        return new Template
        {
            Name = dto.Name,
            Type = TemplateType.Event,
            Title = payload.Title,
            Description = payload.Description ?? string.Empty,
            Time = payload.Time ?? string.Empty,
            Url = payload.Url ?? string.Empty,
            ImageUrl = payload.ImageUrl ?? string.Empty,
            ThumbnailUrl = payload.ThumbnailUrl ?? string.Empty,
            Color = payload.Color ?? 0,
            Fields = payload.Fields?.Select(f => new Template.TemplateField
            {
                Name = f.Name,
                Value = f.Value,
                Inline = f.Inline ?? false
            }).ToList() ?? new List<Template.TemplateField>(),
            Buttons = payload.Buttons?.Select(b => new Template.TemplateButton
            {
                Tag = Guid.NewGuid().ToString(),
                Include = true,
                Label = b.Label,
                Emoji = b.Emoji ?? string.Empty,
                Style = b.Style ?? ButtonStyle.Secondary,
                MaxSignups = b.MaxSignups,
                Width = b.Width
            }).ToList() ?? new List<Template.TemplateButton>(),
            Mentions = payload.Mentions?.Select(ulong.Parse).ToList() ?? new List<ulong>()
        };
    }

    private async Task DeleteTemplate(string id)
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config))
        {
            _lastResult = "Invalid API URL";
            return;
        }
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Delete, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/templates/{id}");
            ApiHelpers.AddAuthHeader(request, TokenManager.Instance!);
            var response = await ApiHelpers.SendWithRetries(request, _httpClient);
            if (response?.IsSuccessStatusCode == true)
            {
                _lastResult = "Template deleted";
                _ = LoadTemplates();
            }
            else if (response != null)
            {
                var bodyText = await response.Content.ReadAsStringAsync();
                _lastResult = $"Failed to delete template: {(int)response.StatusCode} {bodyText}";
            }
            else
            {
                _lastResult = "Failed to delete template";
            }
        }
        catch
        {
            _lastResult = "Failed to delete template";
        }
    }

    private Uri BuildWebSocketUri()
    {
        var baseUri = _config.ApiBaseUrl.TrimEnd('/') + "/ws/templates";
        var builder = new UriBuilder(baseUri);
        if (builder.Scheme == "https")
            builder.Scheme = "wss";
        else if (builder.Scheme == "http")
            builder.Scheme = "ws";
        return builder.Uri;
    }

}


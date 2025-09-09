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
        new() { "RSVP: Yes", "RSVP: Maybe" },
        new() { "RSVP: No" }
    });

    public TemplatesWindow(Config config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
        _channelId = config.EventChannelId;
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
        var filteredTemplates = _config.TemplateData.Where(t => t.Type == TemplateType.Event).ToList();
        for (var i = 0; i < filteredTemplates.Count; i++)
        {
            var tmplItem = filteredTemplates[i];
            var name = tmplItem.Name;
            if (ImGui.Selectable(name, _selectedIndex == i))
            {
                _selectedIndex = i;
                _showPreview = false;
                _mentions.Clear();
                if (tmplItem.Mentions != null)
                {
                    foreach (var m in tmplItem.Mentions)
                        _mentions.Add(m.ToString());
                }

                var rowsInit = (tmplItem.Buttons ?? Enumerable.Empty<TemplateButton>())
                    .Where(b => b.Include && !string.IsNullOrWhiteSpace(b.Label))
                    .Chunk(5)
                    .Select(chunk => chunk.Select(b => b.Label).ToList())
                    .ToList();

                _buttonRows = new ButtonRows(
                    rowsInit.Count > 0
                        ? rowsInit
                        : new()
                        {
                            new() { "RSVP: Yes", "RSVP: Maybe" },
                            new() { "RSVP: No" }
                        });
            }
        }
        ImGui.EndChild();

        ImGui.SameLine();

        ImGui.BeginChild("TemplateContent", ImGui.GetContentRegionAvail(), false);
        if (_selectedIndex >= 0 && _selectedIndex < filteredTemplates.Count)
        {
            var tmpl = filteredTemplates[_selectedIndex];
            if (ImGui.Button("Preview"))
            {
                _previewEvent?.Dispose();
                _previewEvent = new EventView(ToEmbedDto(tmpl), _config, _httpClient, () => Task.CompletedTask);
                _showPreview = true;
            }
            ImGui.SameLine();
            if (ImGui.Button("Post"))
            {
                _pendingTemplate = tmpl;
                _confirmPost = true;
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

        if (_showPreview)
        {
            if (ImGui.Begin("Template Preview", ref _showPreview))
            {
                _previewEvent?.Draw();
            }
            ImGui.End();
        }
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
            var response = await _httpClient.SendAsync(request);
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

    internal record ButtonPayload(
        string label,
        string customId,
        int rowIndex,
        int style,
        string? emoji,
        int? maxSignups,
        int? width,
        int? height);

    internal List<ButtonPayload> BuildButtonsPayload()
    {
        return _buttonRows.FlattenNonEmpty()
            .Select(x => new ButtonPayload(
                Truncate(x.Label.Trim(), 80),
                MakeCustomId(x.Label.Trim(), x.RowIndex, x.ColIndex),
                x.RowIndex,
                (int)ButtonStyle.Primary,
                null,
                null,
                null,
                null))
            .ToList();
    }


    internal EmbedDto ToEmbedDto(Template tmpl)
    {
        DateTimeOffset? ts = null;
        if (!string.IsNullOrWhiteSpace(tmpl.Time) && DateTimeOffset.TryParse(tmpl.Time, out var parsed))
        {
            ts = parsed;
        }

        var buttons = BuildButtonsPayload()
            .Select(b => new EmbedButtonDto
            {
                Label = b.label,
                CustomId = b.customId,
                Style = (ButtonStyle)b.style,
                Emoji = b.emoji,
                MaxSignups = b.maxSignups,
                Width = b.width,
                Height = b.height,
                RowIndex = b.row
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
        if (!string.IsNullOrEmpty(tmpl.Description) && tmpl.Description.Length > 2000)
        {
            _lastResult = "Description exceeds 2000 characters";
            return;
        }
        if (!DiscordValidation.IsImageUrlAllowed(tmpl.ImageUrl) ||
            !DiscordValidation.IsImageUrlAllowed(tmpl.ThumbnailUrl))
        {
            _lastResult = "Invalid image or thumbnail URL";
            return;
        }
        try
        {

            var buttonsFlat = BuildButtonsPayload();
            if (buttonsFlat.Count == 0)
            {
                _lastResult = "Add at least one button";
                return;
            }

            var body = new
            {
                channelId = _channelId,
                title = tmpl.Title,
                time = string.IsNullOrWhiteSpace(tmpl.Time) ? DateTime.UtcNow.ToString("O") : tmpl.Time,
                description = tmpl.Description,
                url = string.IsNullOrWhiteSpace(tmpl.Url) ? null : tmpl.Url,
                imageUrl = string.IsNullOrWhiteSpace(tmpl.ImageUrl) ? null : tmpl.ImageUrl,
                thumbnailUrl = string.IsNullOrWhiteSpace(tmpl.ThumbnailUrl) ? null : tmpl.ThumbnailUrl,
                color = tmpl.Color != 0 ? (uint?)tmpl.Color : null,
                fields = tmpl.Fields != null && tmpl.Fields.Count > 0
                    ? tmpl.Fields.Where(f => !string.IsNullOrWhiteSpace(f.Name) && !string.IsNullOrWhiteSpace(f.Value))
                                 .Select(f => new { name = f.Name, value = f.Value, inline = f.Inline })
                                 .ToList()
                    : null,
                buttons = buttonsFlat,
                mentions = _mentions.Count > 0 ? _mentions.Select(ulong.Parse).ToList() : null
            };


            var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/events");
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            ApiHelpers.AddAuthHeader(request, TokenManager.Instance!);
            await _httpClient.SendAsync(request);
            _lastResult = "Template posted";
        }
        catch
        {
            _lastResult = "Failed to post template";
        }
    }

}


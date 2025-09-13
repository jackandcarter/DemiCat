using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using Dalamud.Bindings.ImGui;
using DiscordHelper;
using DemiCat.UI;

namespace DemiCatPlugin;

public class EventCreateWindow
{
    private readonly Config _config;
    private readonly HttpClient _httpClient;
    private readonly ChannelService _channelService;

    private string _title = string.Empty;
    private string _description = string.Empty;
    private string _time = DateTime.UtcNow.ToString("O");
    private string _imageUrl = string.Empty;
    private string _url = string.Empty;
    private string _thumbnailUrl = string.Empty;
    private uint _color;
    private readonly List<Field> _fields = new();
    private string? _lastResult;
    private readonly List<Template.TemplateButton> _buttons = new();
    private readonly SignupOptionEditor _optionEditor;
    private int _editingButtonIndex = -1;
    private int _selectedPreset = -1;
    private string _presetName = string.Empty;
    private RepeatOption _repeat = RepeatOption.None;
    private readonly List<RepeatSchedule> _schedules = new();
    private bool _schedulesLoaded;
    private readonly List<RoleDto> _roles = new();
    private readonly HashSet<string> _mentions = new();
    private bool _rolesLoaded;
    private readonly List<ChannelDto> _channels = new();
    private bool _channelsLoaded;
    private bool _channelFetchFailed;
    private string _channelErrorMessage = string.Empty;
    private int _selectedIndex;
    private string _channelId = string.Empty;
    private EmbedDto? _preview;
    private bool _confirmCreate;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public EventCreateWindow(Config config, HttpClient httpClient, ChannelService channelService)
    {
        _config = config;
        _httpClient = httpClient;
        _channelService = channelService;
        _channelId = config.EventChannelId;
        _optionEditor = new SignupOptionEditor(config, httpClient);
        ResetDefaultButtons();
    }

    public void StartNetworking()
    {
        if (!_config.Events || TokenManager.Instance?.IsReady() != true)
        {
            return;
        }

        _ = SignupPresetService.EnsureLoaded(_httpClient, _config);
        _ = RefreshChannels();
    }

    public void Draw()
    {
        if (TokenManager.Instance?.IsReady() != true)
        {
            ImGui.TextUnformatted("Link DemiCat to create events");
            return;
        }

        if (!_config.Events)
        {
            ImGui.TextUnformatted("Feature disabled");
            return;
        }
        var footer = ImGui.GetFrameHeightWithSpacing() * 2;
        var avail = ImGui.GetContentRegionAvail();
        ImGui.BeginChild("eventCreateScroll", new Vector2(avail.X, avail.Y - footer), true);

        if (!_channelsLoaded)
        {
            _ = RefreshChannels();
        }
        if (_channels.Count > 0)
        {
            var channelNames = _channels.Select(c => c.Name).ToArray();
            if (ImGui.Combo("Channel", ref _selectedIndex, channelNames, channelNames.Length))
            {
                _channelId = _channels[_selectedIndex].Id;
                _config.EventChannelId = _channelId;
                SaveConfig();
            }
        }
        else
        {
            ImGui.TextUnformatted(_channelFetchFailed ? _channelErrorMessage : "No channels available");
        }

        ImGui.InputText("Title", ref _title, 256);
        ImGui.InputText("Time", ref _time, 64);
        var repeatPreview = _repeat.ToString();
        if (ImGui.BeginCombo("Repeat", repeatPreview))
        {
            foreach (RepeatOption opt in Enum.GetValues<RepeatOption>())
            {
                var sel = opt == _repeat;
                if (ImGui.Selectable(opt.ToString(), sel)) _repeat = opt;
                if (sel) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        if (_repeat != RepeatOption.None &&
            DateTime.TryParse(_time, null, DateTimeStyles.AdjustToUniversal, out var baseTime))
        {
            var next = _repeat == RepeatOption.Daily ? baseTime.AddDays(1) : baseTime.AddDays(7);
            var nextStr = next.ToUniversalTime().ToString("O");
            ImGui.TextUnformatted($"Next: {nextStr}");
        }
        ImGui.InputTextMultiline("Description", ref _description, 4096, new Vector2(400, 100));
        ImGui.InputText("URL", ref _url, 260);
        ImGui.InputText("Image URL", ref _imageUrl, 260);
        ImGui.InputText("Thumbnail URL", ref _thumbnailUrl, 260);
        var colorVec = ColorUtils.ImGuiToVector(_color);
        if (ImGui.ColorEdit3("Color", ref colorVec))
        {
            _color = ColorUtils.VectorToImGui(colorVec);
        }

        if (!_rolesLoaded)
        {
            _ = LoadRoles();
        }
        if (_rolesLoaded)
        {
            if (_roles.Count > 0)
            {
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
        for (var i = 0; i < _buttons.Count; i++)
        {
            var button = _buttons[i];
            ImGui.PushID(i);
            var include = button.Include;
            if (ImGui.Checkbox("Include", ref include))
                button.Include = include;
            ImGui.SameLine();
            ImGui.TextUnformatted($"{button.Label} ({button.Tag})");
            ImGui.SameLine();
            if (ImGui.Button("Edit"))
            {
                _editingButtonIndex = i;
                _optionEditor.Open(button, b => _buttons[_editingButtonIndex] = b);
            }
            ImGui.SameLine();
            if (ImGui.Button("Remove"))
            {
                _buttons.RemoveAt(i);
                i--;
            }
            ImGui.PopID();
        }
        if (ImGui.Button("Add Option"))
        {
            _editingButtonIndex = -1;
            var btn = new Template.TemplateButton
            {
                Tag = $"opt{_buttons.Count + 1}",
                Label = "Option",
                Emoji = string.Empty,
                Style = ButtonStyle.Secondary
            };
            _optionEditor.Open(btn, b => _buttons.Add(b));
        }
        _optionEditor.Draw();

        _ = SignupPresetService.EnsureLoaded(_httpClient, _config);
        var presets = SignupPresetService.Presets;
        if (presets.Count > 0)
        {
            var preview = _selectedPreset >= 0 && _selectedPreset < presets.Count
                ? presets[_selectedPreset].Name
                : string.Empty;
            if (ImGui.BeginCombo("Presets", preview))
            {
                for (var i = 0; i < presets.Count; i++)
                {
                    var name = presets[i].Name;
                    var sel = i == _selectedPreset;
                    if (ImGui.Selectable(name, sel))
                    {
                        _selectedPreset = i;
                        LoadPreset(presets[i]);
                    }
                    if (sel) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
        }
        ImGui.InputText("Preset Name", ref _presetName, 64);
        ImGui.SameLine();
        if (ImGui.Button("Save Preset"))
        {
            SavePreset();
        }

        if (ImGui.TreeNode("Fields"))
        {
            for (var i = 0; i < _fields.Count; i++)
            {
                var field = _fields[i];
                ImGui.InputText($"Name##{i}", ref field.Name, 256);
                ImGui.InputTextMultiline($"Value##{i}", ref field.Value, 1024, new Vector2(400, 60));
                ImGui.Checkbox($"Inline##{i}", ref field.Inline);
                if (ImGui.Button($"Remove##{i}"))
                {
                    _fields.RemoveAt(i);
                    i--;
                }
                ImGui.Separator();
            }
            if (ImGui.Button("Add Field"))
            {
                _fields.Add(new Field());
            }
            ImGui.TreePop();
        }
        _preview = BuildPreview();
        ImGui.Separator();
        EmbedPreviewRenderer.Draw(_preview, (_, __) => { });

        if (!_schedulesLoaded)
        {
            _ = FetchSchedules();
        }
        if (_schedulesLoaded && _schedules.Count > 0)
        {
            ImGui.Separator();
            ImGui.TextUnformatted("Repeat Schedules");
            for (var i = 0; i < _schedules.Count; i++)
            {
                var s = _schedules[i];
                ImGui.TextUnformatted($"{s.Title} ({s.Repeat}) next {s.Next}");
                ImGui.SameLine();
                if (ImGui.Button($"Cancel##{i}"))
                {
                    _ = CancelSchedule(s.Id);
                }
            }
        }

        if (!string.IsNullOrEmpty(_lastResult))
        {
            ImGui.TextUnformatted(_lastResult);
        }

        ImGui.EndChild();

        if (ImGui.Button("Create"))
        {
            _confirmCreate = true;
        }
        ImGui.SameLine();
        if (ImGui.Button("Save Template"))
        {
            _ = SaveTemplate();
        }

        if (_confirmCreate)
            ImGui.OpenPopup("Confirm Event Create");
        var openConfirm = _confirmCreate;
        if (_confirmCreate && ImGui.BeginPopupModal("Confirm Event Create", ref openConfirm, ImGuiWindowFlags.AlwaysAutoResize))
        {
            var channelName = _channels.FirstOrDefault(c => c.Id == _channelId)?.Name ?? _channelId;
            ImGui.TextUnformatted($"Channel: {channelName}");
            var roleNames = _roles.Where(r => _mentions.Contains(r.Id)).Select(r => r.Name).ToList();
            ImGui.TextUnformatted("Roles: " + (roleNames.Count > 0 ? string.Join(", ", roleNames) : "None"));
            if (ImGui.Button("Confirm"))
            {
                _ = CreateEvent();
                _confirmCreate = false;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                _confirmCreate = false;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
        if (!_confirmCreate || !openConfirm) _confirmCreate = false;
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

    private async Task FetchSchedules()
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config))
        {
            PluginServices.Instance!.Log.Warning("Cannot fetch schedules: API base URL is not configured.");
            _schedulesLoaded = true;
            _lastResult = "Invalid API URL";
            return;
        }
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/events/repeat");
            ApiHelpers.AddAuthHeader(request, TokenManager.Instance!);
            var response = await ApiHelpers.SendWithRetries(request, _httpClient);
            if (response?.IsSuccessStatusCode == true)
            {
                var stream = await response.Content.ReadAsStreamAsync();
                var schedules = await JsonSerializer.DeserializeAsync<List<RepeatSchedule>>(stream) ?? new();
                _ = PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    _schedules.Clear();
                    _schedules.AddRange(schedules);
                    _schedulesLoaded = true;
                });
            }
            else
            {
                _schedulesLoaded = true;
                if (response == null)
                {
                    _lastResult = "Failed to load schedules";
                }
                else
                {
                    var body = await response.Content.ReadAsStringAsync();
                    _lastResult = $"Failed to load schedules: {(int)response.StatusCode} {body}";
                }
            }
        }
        catch
        {
            _schedulesLoaded = true;
            _lastResult = "Failed to load schedules";
        }
    }

    private async Task CancelSchedule(string id)
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config)) return;
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Delete, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/events/{id}/repeat");
            ApiHelpers.AddAuthHeader(request, TokenManager.Instance!);
            var response = await ApiHelpers.SendWithRetries(request, _httpClient);
            if (response?.IsSuccessStatusCode == true)
            {
                _schedulesLoaded = false;
            }
            else if (response != null)
            {
                var body = await response.Content.ReadAsStringAsync();
                _lastResult = $"Failed to cancel schedule: {(int)response.StatusCode} {body}";
            }
            else
            {
                _lastResult = "Failed to cancel schedule";
            }
        }
        catch
        {
            // ignored
        }
    }

    public void LoadTemplate(Template template)
    {
        _title = template.Title;
        _description = template.Description;
        _time = string.IsNullOrEmpty(template.Time)
            ? DateTime.UtcNow.ToString("O")
            : template.Time;
        _url = template.Url;
        _imageUrl = template.ImageUrl;
        _thumbnailUrl = template.ThumbnailUrl;
        _color = ColorUtils.RgbToImGui(template.Color);
        _mentions.Clear();
        if (template.Mentions != null)
        {
            foreach (var m in template.Mentions)
            {
                _mentions.Add(m.ToString());
            }
        }

        _fields.Clear();
        if (template.Fields != null)
        {
            foreach (var f in template.Fields)
            {
                _fields.Add(new Field { Name = f.Name, Value = f.Value, Inline = f.Inline });
            }
        }

        if (template.Buttons != null && template.Buttons.Count > 0)
        {
            _buttons.Clear();
            foreach (var b in template.Buttons)
            {
                _buttons.Add(new Template.TemplateButton
                {
                    Tag = b.Tag,
                    Include = b.Include,
                    Label = b.Label,
                    Emoji = EmojiUtils.Normalize(b.Emoji),
                    Style = b.Style,
                    MaxSignups = b.MaxSignups,
                    Width = b.Width
                });
            }
        }
        else
        {
            ResetDefaultButtons();
        }
    }

    private async Task SaveTemplate()
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config))
        {
            _lastResult = "Invalid API URL";
            return;
        }
        try
        {
            var dto = BuildPreview();
            var buttons = dto.Buttons ?? new List<EmbedButtonDto>();
            var payload = new
            {
                channelId = _channelId,
                title = dto.Title,
                time = _time,
                description = dto.Description,
                url = dto.Url,
                imageUrl = dto.ImageUrl,
                thumbnailUrl = dto.ThumbnailUrl,
                color = dto.Color,
                fields = dto.Fields?.Select(f => new { name = f.Name, value = f.Value, inline = f.Inline }).ToList(),
                buttons = buttons.Select(b => new { label = b.Label, customId = b.CustomId, style = b.Style.HasValue ? (int?)b.Style : null, emoji = b.Emoji, maxSignups = b.MaxSignups, width = b.Width, rowIndex = b.RowIndex }).ToList(),
                mentions = _mentions.Count > 0 ? _mentions.ToList() : null
            };
            var body = new
            {
                name = _title,
                description = _description,
                payload
            };
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/templates");
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json");
            ApiHelpers.AddAuthHeader(request, TokenManager.Instance!);
            var response = await ApiHelpers.SendWithRetries(request, _httpClient);
            if (response?.IsSuccessStatusCode == true)
            {
                _lastResult = "Template saved";
            }
            else if (response != null)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                _lastResult = $"Failed to save template: {(int)response.StatusCode} {responseBody}";
            }
            else
            {
                _lastResult = "Failed to save template";
            }
        }
        catch
        {
            _lastResult = "Failed to save template";
        }
    }

    private async Task CreateEvent()
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config) || string.IsNullOrWhiteSpace(_channelId))
        {
            if (string.IsNullOrWhiteSpace(_channelId))
            {
                _lastResult = "No channel selected";
            }
            return;
        }
        if (_description.Length > 2000)
        {
            _lastResult = "Description exceeds 2000 characters";
            return;
        }
        try
        {
            var dto = BuildPreview();
            var buttons = dto.Buttons ?? new List<EmbedButtonDto>();
            var body = new
            {
                channelId = _channelId,
                title = dto.Title,
                time = dto.Timestamp?.ToString("O") ?? _time,
                description = dto.Description,
                url = dto.Url,
                imageUrl = dto.ImageUrl,
                thumbnailUrl = dto.ThumbnailUrl,
                color = dto.Color,
                fields = dto.Fields?.Select(f => new { name = f.Name, value = f.Value, inline = f.Inline }).ToList(),
                buttons = buttons.Select(b => new { label = b.Label, customId = b.CustomId, url = b.Url, emoji = b.Emoji, style = b.Style.HasValue ? (int)b.Style : (int?)null, maxSignups = b.MaxSignups, width = b.Width }).ToList(),
                mentions = _mentions.Count > 0 ? _mentions.Select(ulong.Parse).ToList() : null,
                repeat = _repeat == RepeatOption.None ? null : _repeat.ToString().ToLowerInvariant()
            };

            var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/events");
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json");
            ApiHelpers.AddAuthHeader(request, TokenManager.Instance!);

            var response = await ApiHelpers.SendWithRetries(request, _httpClient);
            if (response?.IsSuccessStatusCode == true)
            {
                _lastResult = "Event posted";
            }
            else if (response != null)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                _lastResult = $"Error {(int)response.StatusCode}: {responseBody}";
            }
            else
            {
                _lastResult = "Failed to post event";
            }
        }
        catch (Exception ex)
        {
            _lastResult = $"Failed: {ex.Message}";
        }
    }

    private void LoadPreset(SignupPreset preset)
    {
        _buttons.Clear();
        foreach (var b in preset.Buttons)
        {
                _buttons.Add(new Template.TemplateButton
                {
                    Tag = b.Tag,
                    Include = b.Include,
                    Label = b.Label,
                    Emoji = EmojiUtils.Normalize(b.Emoji),
                    Style = b.Style,
                    MaxSignups = b.MaxSignups,
                    Width = b.Width
                });
            }
        }

    private void SaveConfig()
    {
        PluginServices.Instance!.PluginInterface.SavePluginConfig(_config);
    }

    private EmbedDto BuildPreview()
    {
        var dto = new EmbedDto
        {
            Title = _title,
            Description = _description,
            Url = string.IsNullOrWhiteSpace(_url) ? null : _url,
            ImageUrl = string.IsNullOrWhiteSpace(_imageUrl) ? null : _imageUrl,
            ThumbnailUrl = string.IsNullOrWhiteSpace(_thumbnailUrl) ? null : _thumbnailUrl,
            Color = _color > 0 ? (uint?)ColorUtils.ImGuiToRgb(_color) : null,
            Timestamp = DateTime.TryParse(_time, null, DateTimeStyles.AdjustToUniversal, out var ts) ? ts : (DateTimeOffset?)null,
            Fields = _fields
                .Where(f => !string.IsNullOrWhiteSpace(f.Name) && !string.IsNullOrWhiteSpace(f.Value))
                .Select(f => new EmbedFieldDto { Name = f.Name, Value = f.Value, Inline = f.Inline })
                .ToList(),
            Buttons = _buttons
                .Where(b => b.Include)
                .Select((b, i) => new EmbedButtonDto
                {
                    Label = b.Label,
                    CustomId = $"rsvp:{b.Tag}",
                    Emoji = string.IsNullOrWhiteSpace(b.Emoji) ? null : b.Emoji,
                    Style = b.Style,
                    MaxSignups = b.MaxSignups,
                    Width = Math.Min(b.Width ?? ButtonSizeHelper.ComputeWidth(b.Label), ButtonSizeHelper.Max),
                    RowIndex = i / 5
                })
                .ToList()
        };
        if (dto.Fields != null && dto.Fields.Count == 0) dto.Fields = null;
        if (dto.Buttons != null && dto.Buttons.Count == 0) dto.Buttons = null;
        return dto;
    }

    public Task RefreshChannels()
    {
        if (!_config.Events)
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
        if (!_config.Events)
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
            var dto = (await _channelService.FetchAsync(ChannelKind.Event, CancellationToken.None)).ToList();
            if (await ChannelNameResolver.Resolve(dto, _httpClient, _config, refreshed, () => FetchChannels(true))) return;
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _channels.Clear();
                _channels.AddRange(dto);
                if (!string.IsNullOrEmpty(_channelId))
                {
                    _selectedIndex = _channels.FindIndex(c => c.Id == _channelId);
                    if (_selectedIndex < 0) _selectedIndex = 0;
                }
                if (_channels.Count > 0)
                {
                    _channelId = _channels[_selectedIndex].Id;
                }
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
                        ? "Forbidden – check API key/roles"
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

    private void SavePreset()
    {
        if (string.IsNullOrWhiteSpace(_presetName)) return;
        var preset = new SignupPreset
        {
            Name = _presetName,
            Buttons = _buttons.Select(b => new Template.TemplateButton
            {
                Tag = b.Tag,
                Include = b.Include,
                Label = b.Label,
                Emoji = EmojiUtils.Normalize(b.Emoji),
                Style = b.Style,
                MaxSignups = b.MaxSignups,
                Width = b.Width
            }).ToList()
        };
        _ = SignupPresetService.Create(preset, _httpClient, _config);
        _presetName = string.Empty;
    }

    public void ResetRoles()
    {
        RoleCache.Reset();
        _roles.Clear();
        _rolesLoaded = false;
    }

    private void ResetDefaultButtons()
    {
        _buttons.Clear();
        _buttons.Add(new Template.TemplateButton
        {
            Tag = "yes",
            Label = "Yes",
            Style = ButtonStyle.Success
        });
        _buttons.Add(new Template.TemplateButton
        {
            Tag = "maybe",
            Label = "Maybe",
            Style = ButtonStyle.Secondary
        });
        _buttons.Add(new Template.TemplateButton
        {
            Tag = "no",
            Label = "No",
            Style = ButtonStyle.Danger
        });
    }


    private enum RepeatOption
    {
        None,
        Daily,
        Weekly
    }

    private class Field
    {
        public string Name = string.Empty;
        public string Value = string.Empty;
        public bool Inline;
    }

    private class RepeatSchedule
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Repeat { get; set; } = string.Empty;
        public string Next { get; set; } = string.Empty;
    }
}

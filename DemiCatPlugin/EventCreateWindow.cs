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
using DemiCatPlugin.Emoji;

namespace DemiCatPlugin;

public class EventCreateWindow
{
    private readonly Config _config;
    private readonly HttpClient _httpClient;
    private readonly ChannelService _channelService;
    private readonly EmojiManager _emojiManager;

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
    private readonly ChannelSelectionService _channelSelection;
    private EventPreviewFormatter.Result? _preview;
    private EventView? _previewView;
    private bool _confirmCreate;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public EventCreateWindow(Config config, HttpClient httpClient, ChannelService channelService, ChannelSelectionService channelSelection, EmojiManager emojiManager)
    {
        _config = config;
        _httpClient = httpClient;
        _channelService = channelService;
        _emojiManager = emojiManager;
        _channelSelection = channelSelection;
        _optionEditor = new SignupOptionEditor(config, httpClient, emojiManager);
        ResetDefaultButtons();
        _channelSelection.ChannelChanged += HandleChannelChanged;
    }

    private string ChannelId => _channelSelection.GetChannel(ChannelKind.Event, _config.GuildId);

    private void HandleChannelChanged(string kind, string guildId, string oldId, string newId)
    {
        if (kind != ChannelKind.Event) return;
        if (!string.Equals(ChannelKeyHelper.NormalizeGuildId(guildId), ChannelKeyHelper.NormalizeGuildId(_config.GuildId), StringComparison.Ordinal))
            return;
        PluginServices.Instance!.Framework.RunOnTick(() =>
        {
            if (_channels.Count > 0)
            {
                _selectedIndex = _channels.FindIndex(c => c.Id == newId);
            }
        });
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
            var channelNames = _channels.Select(c => c.ParentId == null ? c.Name : "  " + c.Name).ToArray();
            if (ImGui.Combo("Channel", ref _selectedIndex, channelNames, channelNames.Length))
            {
                var newId = _channels[_selectedIndex].Id;
                _channelSelection.SetChannel(ChannelKind.Event, _config.GuildId, newId);
            }
        }
        else
        {
            ImGui.TextUnformatted(_channelFetchFailed ? _channelErrorMessage : "No channels available");
        }

        var style = ImGui.GetStyle();
        var labelColumnWidth = ImGui.CalcTextSize("Thumbnail URL").X + style.ItemInnerSpacing.X * 2f;
        if (!_rolesLoaded)
        {
            _ = LoadRoles();
        }

        if (ImGui.BeginTable("eventCreateForm", 2, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthFixed, labelColumnWidth);
            ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);

            void DrawFormRow(string label, Action drawControl, bool alignLabel = true, bool setFullWidth = true)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (alignLabel)
                {
                    ImGui.AlignTextToFramePadding();
                }
                ImGui.TextUnformatted(label);
                ImGui.TableNextColumn();
                if (setFullWidth)
                {
                    ImGui.SetNextItemWidth(-1f);
                }
                drawControl();
            }

            DrawFormRow("Title", () => ImGui.InputText("##Title", ref _title, 256));
            DrawFormRow("Time", () => ImGui.InputText("##Time", ref _time, 64));
            DrawFormRow(
                "Description",
                () =>
                {
                    var avail = ImGui.GetContentRegionAvail();
                    var descHeight = ImGui.GetTextLineHeight() * 6f;
                    ImGui.InputTextMultiline("##Description", ref _description, 4096, new Vector2(avail.X, descHeight));
                },
                alignLabel: false,
                setFullWidth: false);

            DrawFormRow("Repeat", () =>
            {
                var repeatPreview = _repeat.ToString();
                if (ImGui.BeginCombo("##Repeat", repeatPreview))
                {
                    foreach (RepeatOption opt in Enum.GetValues<RepeatOption>())
                    {
                        var sel = opt == _repeat;
                        if (ImGui.Selectable(opt.ToString(), sel)) _repeat = opt;
                        if (sel) ImGui.SetItemDefaultFocus();
                    }
                    ImGui.EndCombo();
                }
            });

            if (_repeat != RepeatOption.None &&
                DateTime.TryParse(_time, null, DateTimeStyles.AdjustToUniversal, out var baseTime))
            {
                var next = _repeat == RepeatOption.Daily ? baseTime.AddDays(1) : baseTime.AddDays(7);
                var nextStr = next.ToUniversalTime().ToString("O");
                DrawFormRow(string.Empty, () => ImGui.TextUnformatted($"Next: {nextStr}"), alignLabel: false, setFullWidth: false);
            }

            DrawFormRow(
                "Mention Roles",
                () =>
                {
                    if (_rolesLoaded)
                    {
                        if (_roles.Count > 0)
                        {
                            var selectedRoleNames = _roles.Where(r => _mentions.Contains(r.Id)).Select(r => r.Name).ToList();
                            var previewLabel = selectedRoleNames.Count > 0 ? string.Join(", ", selectedRoleNames) : "Select roles";
                            ImGui.SetNextItemWidth(-1f);
                            if (ImGui.BeginCombo("##MentionRoles", previewLabel))
                            {
                                foreach (var role in _roles)
                                {
                                    var roleId = role.Id;
                                    var selected = _mentions.Contains(roleId);
                                    if (ImGui.Selectable($"{role.Name}##role{role.Id}", selected, ImGuiSelectableFlags.DontClosePopups))
                                    {
                                        if (selected)
                                        {
                                            _mentions.Remove(roleId);
                                        }
                                        else
                                        {
                                            _mentions.Add(roleId);
                                        }
                                    }
                                }
                                ImGui.EndCombo();
                            }
                        }
                        else
                        {
                            var msg = RoleCache.LastErrorMessage ?? "No roles available";
                            ImGui.TextUnformatted(msg);
                        }
                    }
                    else
                    {
                        ImGui.TextUnformatted("Loading roles...");
                    }
                },
                setFullWidth: false);

            DrawFormRow("URL", () => ImGui.InputText("##Url", ref _url, 260));
            DrawFormRow("Image URL", () => ImGui.InputText("##ImageUrl", ref _imageUrl, 260));
            DrawFormRow("Thumbnail URL", () => ImGui.InputText("##ThumbnailUrl", ref _thumbnailUrl, 260));

            DrawFormRow(
                "Preset Name",
                () =>
                {
                    var avail = ImGui.GetContentRegionAvail().X;
                    var buttonLabel = "Save Preset";
                    var buttonWidth = ImGui.CalcTextSize(buttonLabel).X + style.FramePadding.X * 2f;
                    var spacing = style.ItemSpacing.X;
                    var inputWidth = Math.Max(1f, avail - buttonWidth - spacing);
                    ImGui.SetNextItemWidth(inputWidth);
                    ImGui.InputText("##PresetName", ref _presetName, 64);
                    ImGui.SameLine();
                    if (ImGui.Button(buttonLabel))
                    {
                        SavePreset();
                    }
                },
                setFullWidth: false);

            ImGui.EndTable();
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Embed Banner Color:");
        ImGui.SameLine();

        var colorVec = ColorUtils.ImGuiToVector(_color);
        var colorDisplaySize = new Vector2(ImGui.GetFrameHeight() * 2.5f, ImGui.GetFrameHeight());
        if (ImGui.ColorButton("##embedColorButton", new Vector4(colorVec, 1f), ImGuiColorEditFlags.None, colorDisplaySize))
        {
            ImGui.OpenPopup("embedColorPicker");
        }

        if (ImGui.BeginPopup("embedColorPicker"))
        {
            var pickerColor = colorVec;
            if (ImGui.ColorPicker3("##embedColorPicker", ref pickerColor, ImGuiColorEditFlags.NoSidePreview | ImGuiColorEditFlags.NoSmallPreview))
            {
                _color = ColorUtils.VectorToImGui(pickerColor);
            }
            if (ImGui.Button("Clear"))
            {
                _color = 0;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        ImGui.Separator();

        for (var i = 0; i < _buttons.Count; i++)
        {
            var button = _buttons[i];
            ImGui.PushID(i);
            var include = button.Include;
            if (ImGui.Checkbox("Include", ref include))
                button.Include = include;
            ImGui.SameLine();
            if (!string.IsNullOrWhiteSpace(button.Emoji))
            {
                EmojiRenderer.Draw(button.Emoji, _emojiManager);
                ImGui.SameLine();
            }
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
        var previewResult = BuildPreview();
        _preview = previewResult;
        if (_previewView == null)
        {
            _previewView = new EventView(
                previewResult.Embed,
                _config,
                _httpClient,
                () => Task.CompletedTask,
                _emojiManager,
                previewResult.Content,
                previewResult.Warnings);
        }
        else
        {
            _previewView.Update(previewResult.Embed, previewResult.Content, previewResult.Warnings);
        }

        ImGui.Separator();
        _previewView!.Draw();

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
            var channelName = _channels.FirstOrDefault(c => c.Id == ChannelId)?.Name ?? ChannelId;
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
                    Emoji = EmojiFormatter.Normalize(_emojiManager, b.Emoji) ?? string.Empty,
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
            var preview = BuildPreview();
            var dto = preview.Embed;
            var buttons = preview.Buttons.ToList();
            var payload = new
            {
                channelId = ChannelId,
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
        var channelId = ChannelId;
        if (!ApiHelpers.ValidateApiBaseUrl(_config) || string.IsNullOrWhiteSpace(channelId))
        {
            if (string.IsNullOrWhiteSpace(channelId))
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
            var preview = BuildPreview();
            var dto = preview.Embed;
            var buttons = preview.Buttons.ToList();
            var body = new
            {
                channelId,
                title = dto.Title,
                time = dto.Timestamp?.ToString("O") ?? _time,
                description = dto.Description,
                url = dto.Url,
                imageUrl = dto.ImageUrl,
                thumbnailUrl = dto.ThumbnailUrl,
                color = dto.Color,
                fields = dto.Fields?.Select(f => new { name = f.Name, value = f.Value, inline = f.Inline }).ToList(),
                buttons = buttons.Select(b => new { label = b.Label, customId = b.CustomId, url = b.Url, emoji = b.Emoji, style = b.Style.HasValue ? (int)b.Style : (int?)null, maxSignups = b.MaxSignups, width = b.Width, rowIndex = b.RowIndex }).ToList(),
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

                string? detailText = null;
                try
                {
                    using var doc = JsonDocument.Parse(responseBody);
                    if (doc.RootElement.TryGetProperty("detail", out var detail))
                        detailText = detail.GetString();
                    else if (doc.RootElement.TryGetProperty("message", out var message))
                        detailText = message.GetString();
                }
                catch
                {
                    // ignore parse errors
                }
                var lower = detailText?.ToLowerInvariant() ?? string.Empty;
                if (lower == "channel not configured" || lower == "unsupported channel type" || response.StatusCode == HttpStatusCode.NotFound)
                {
                    _ = PluginServices.Instance!.Framework.RunOnTick(async () => await FetchChannels(true));
                    _ = PluginServices.Instance!.Framework.RunOnTick(() => ChannelWatcher.Instance?.TriggerRefresh(true));
                }
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
                    Emoji = EmojiFormatter.Normalize(_emojiManager, b.Emoji) ?? string.Empty,
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

    private EventPreviewFormatter.Result BuildPreview()
    {
        var fields = _fields
            .Where(f => !string.IsNullOrWhiteSpace(f.Name) && !string.IsNullOrWhiteSpace(f.Value))
            .Select(f => new EmbedFieldDto { Name = f.Name, Value = f.Value, Inline = f.Inline })
            .ToList();

        var buttons = _buttons
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
            .ToList();

        var mentionIds = new List<ulong>();
        foreach (var mention in _mentions)
        {
            if (ulong.TryParse(mention, out var parsed))
            {
                mentionIds.Add(parsed);
            }
        }

        DateTimeOffset? timestamp = null;
        if (DateTimeOffset.TryParse(_time, null, DateTimeStyles.AdjustToUniversal, out var parsedTs))
        {
            timestamp = parsedTs;
        }

        return EventPreviewFormatter.Build(
            _title,
            _description,
            timestamp,
            string.IsNullOrWhiteSpace(_url) ? null : _url,
            string.IsNullOrWhiteSpace(_imageUrl) ? null : _imageUrl,
            string.IsNullOrWhiteSpace(_thumbnailUrl) ? null : _thumbnailUrl,
            _color > 0 ? (uint?)ColorUtils.ImGuiToRgb(_color) : null,
            fields,
            buttons,
            mentionIds,
            embedId: "event-create-preview");
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
            var dto = ChannelDtoExtensions.SortForDisplay((await _channelService.FetchAsync(ChannelKind.Event, CancellationToken.None)).ToList());
            if (await ChannelNameResolver.Resolve(dto, _httpClient, _config, refreshed, () => FetchChannels(true))) return;
            _ = PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _channels.Clear();
                _channels.AddRange(dto);
                var current = ChannelId;
                if (!string.IsNullOrEmpty(current))
                {
                    _selectedIndex = _channels.FindIndex(c => c.Id == current);
                    if (_selectedIndex < 0) _selectedIndex = 0;
                }
                if (_channels.Count > 0)
                {
                    _channelSelection.SetChannel(ChannelKind.Event, _config.GuildId, _channels[_selectedIndex].Id);
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
                Emoji = EmojiFormatter.Normalize(_emojiManager, b.Emoji) ?? string.Empty,
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

using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using Dalamud.Interface.ImGuiFileDialog;
using DiscordHelper;
using DemiCat.UI;
using DemiCatPlugin.Emoji;
using System.IO;

namespace DemiCatPlugin;

public class EventCreateWindow
{
    private readonly Config _config;
    private readonly HttpClient _httpClient;
    private readonly ChannelService _channelService;
    private readonly EmojiManager _emojiManager;

    private string _title = string.Empty;
    private string _description = string.Empty;
    private int _descriptionSelectionStart;
    private int _descriptionSelectionEnd;
    private bool _focusDescriptionNextFrame;
    private string _time = string.Empty;
    private string _imageUrl = string.Empty;
    private string _url = string.Empty;
    private string _thumbnailUrl = string.Empty;
    private uint _color;
    private readonly List<Field> _fields = new();
    private string? _lastResult;
    private readonly List<Template.TemplateButton> _buttons = new();
    private readonly SignupOptionEditor _optionEditor;
    private readonly EmojiPopup _descriptionEmojiPopup;
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
    private string[] _channelDisplayNames = Array.Empty<string>();
    private bool _channelsLoaded;
    private bool _channelFetchFailed;
    private string _channelErrorMessage = string.Empty;
    private int _selectedIndex;
    private readonly ChannelSelectionService _channelSelection;
    private EventPreviewFormatter.Result? _preview;
    private EventView? _previewView;
    private bool _confirmCreate;
    private readonly FileDialogManager _imageFileDialog = new();
    private readonly FileDialogManager _thumbnailFileDialog = new();
    private readonly ImageUploadState _bannerUpload = new();
    private readonly ImageUploadState _thumbnailUpload = new();
    private readonly DateTimePicker _timePicker;

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
        _descriptionEmojiPopup = new EmojiPopup(config, emojiManager, "EventDescriptionEmoji", SaveConfig);
        var defaultTime = DateTimePicker.GetDefaultTime();
        _timePicker = new DateTimePicker(defaultTime);
        _time = defaultTime.ToUniversalTime().ToString("O");
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

        _ = MembershipCache.EnsureLoaded(_httpClient, _config);
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

        _imageFileDialog.Draw();
        _thumbnailFileDialog.Draw();

        var footer = ImGui.GetFrameHeightWithSpacing() * 2;
        var avail = ImGui.GetContentRegionAvail();
        ImGui.BeginChild("eventCreateScroll", new Vector2(avail.X, avail.Y - footer), true);

        if (!_channelsLoaded)
        {
            _ = RefreshChannels();
        }
        if (_channels.Count > 0)
        {
            var channelNames = _channelDisplayNames;
            {
                using var emojiFont = _emojiManager.PushEmojiFont();
                if (ImGui.Combo("Channel", ref _selectedIndex, channelNames, channelNames.Length))
                {
                    var newId = _channels[_selectedIndex].Id;
                    _channelSelection.SetChannel(ChannelKind.Event, _config.GuildId, newId);
                }
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
            DrawFormRow(
                "Time",
                () =>
                {
                    if (_timePicker.Draw("EventTime", out var selected))
                    {
                        ApplyTime(selected);
                    }
                });
            DrawFormRow(
                "Description",
                () =>
                {
                    if (ImGui.SmallButton("B##descBold")) WrapDescription("**", "**");
                    ImGui.SameLine();
                    if (ImGui.SmallButton("I##descItalic")) WrapDescription("*", "*");
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Code##descCode")) WrapDescription("`", "`");
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Spoiler##descSpoiler")) WrapDescription("||", "||");
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Link##descLink")) WrapDescription("[", "](url)");
                    ImGui.SameLine();
                    using (var emojiFont = _emojiManager.PushEmojiFont())
                    {
                        if (ImGui.SmallButton("😊##descEmoji"))
                        {
                            _descriptionEmojiPopup.Open(InsertDescriptionText);
                        }
                    }
                    ImGui.Spacing();

                    var avail = ImGui.GetContentRegionAvail();
                    var descHeight = ImGui.GetTextLineHeight() * 6f;
                    if (_focusDescriptionNextFrame)
                    {
                        ImGui.SetKeyboardFocusHere();
                        _focusDescriptionNextFrame = false;
                    }
                    ImGui.InputTextMultiline(
                        "##Description",
                        ref _description,
                        4096u,
                        new Vector2(avail.X, descHeight),
                        ImGuiInputTextFlags.CallbackAlways,
                        OnDescriptionEdited,
                        IntPtr.Zero
                    );
                    _descriptionEmojiPopup.Draw();
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

            if (_repeat != RepeatOption.None)
            {
                var baseTime = _timePicker.Value;
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
                            using var emojiFont = _emojiManager.PushEmojiFont();
                            var selectedRoleNames = _roles.Where(r => _mentions.Contains(r.Id)).Select(r => r.Name).ToList();
                            var previewLabel = selectedRoleNames.Count > 0 ? string.Join(", ", selectedRoleNames) : "Select roles";
                            var maxTextWidth = ImGui.CalcTextSize(previewLabel).X;
                            maxTextWidth = Math.Max(maxTextWidth, ImGui.CalcTextSize("Select roles").X);
                            foreach (var role in _roles)
                            {
                                maxTextWidth = Math.Max(maxTextWidth, ImGui.CalcTextSize(role.Name).X);
                            }

                            var arrowPadding = style.FramePadding.X * 2f + style.ItemInnerSpacing.X + ImGui.GetFrameHeight();
                            var comboWidth = Math.Min(maxTextWidth + arrowPadding, ImGui.GetContentRegionAvail().X);
                            comboWidth = Math.Max(comboWidth, 1f);
                            ImGui.SetNextItemWidth(comboWidth);
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
                            var msg = _config.MentionRoleIds.Count > 0 && RoleCache.LastErrorMessage == null
                                ? "No mentionable roles configured"
                                : RoleCache.LastErrorMessage ?? "No roles available";
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
            DrawFormRow("Image", () => DrawImageInput(ImageUploadKind.Banner), setFullWidth: false);
            DrawFormRow("Thumbnail", () => DrawImageInput(ImageUploadKind.Thumbnail), setFullWidth: false);

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
        ImGui.TextUnformatted("RSVP Buttons");
        ImGui.Spacing();

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
            {
                using var emojiFont = _emojiManager.PushEmojiFont();
                ImGui.TextUnformatted($"{button.Label} ({button.Tag})");
            }
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
            using var emojiFont = _emojiManager.PushEmojiFont();
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
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Preset Name");
        ImGui.SameLine();
        var availWidth = ImGui.GetContentRegionAvail().X;
        const string presetButtonLabel = "Save Preset";
        var presetButtonWidth = ImGui.CalcTextSize(presetButtonLabel).X + style.FramePadding.X * 2f;
        var presetSpacing = style.ItemSpacing.X;
        var presetInputWidth = Math.Max(1f, availWidth - presetButtonWidth - presetSpacing);
        ImGui.SetNextItemWidth(presetInputWidth);
        ImGui.InputText("##PresetName", ref _presetName, 31);
        ImGui.SameLine();
        if (ImGui.Button(presetButtonLabel))
        {
            SavePreset();
        }

        if (ImGui.TreeNode("Fields"))
        {
            for (var i = 0; i < _fields.Count; i++)
            {
                var field = _fields[i];
                ImGui.InputText($"Name##{i}", ref field.Name, 256);
                ImGui.InputTextMultiline($"Value##{i}", ref field.Value, 1024u, new Vector2(400, 60));
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

        if (_previewView != null)
        {
            ImGui.Separator();
            var previewHeight = EventViewImGuiHelpers.BeginPreviewChild("eventCreatePreview");
            _previewView.Draw(previewHeight);
            ImGui.EndChild();
        }

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
                {
                    using var emojiFont = _emojiManager.PushEmojiFont();
                    ImGui.TextUnformatted($"{s.Title} ({s.Repeat}) next {s.Next}");
                }
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
            {
                using var emojiFont = _emojiManager.PushEmojiFont();
                ImGui.TextUnformatted($"Channel: {channelName}");
            }
            var roleNames = _roles.Where(r => _mentions.Contains(r.Id)).Select(r => r.Name).ToList();
            {
                using var emojiFont = _emojiManager.PushEmojiFont();
                ImGui.TextUnformatted("Roles: " + (roleNames.Count > 0 ? string.Join(", ", roleNames) : "None"));
            }
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
            IEnumerable<RoleDto> roles = RoleCache.Roles;
            List<RoleDto> filtered;
            if (_config.MentionRoleIds.Count > 0)
            {
                var allowed = new HashSet<string>(_config.MentionRoleIds, StringComparer.Ordinal);
                filtered = roles.Where(role => allowed.Contains(role.Id)).ToList();
            }
            else
            {
                filtered = roles.ToList();
            }

            var validIds = new HashSet<string>(filtered.Select(role => role.Id), StringComparer.Ordinal);
            _roles.Clear();
            _roles.AddRange(filtered);
            if (_mentions.Count > 0)
            {
                _mentions.RemoveWhere(id => !validIds.Contains(id));
            }
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
            var requestUri = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/events/repeat";
            var tokenManager = TokenManager.Instance!;
            var response = await ApiHelpers.SendWithRetries(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                ApiHelpers.AddAuthHeader(request, tokenManager);
                return request;
            }, _httpClient);
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
            var requestUri = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/events/{id}/repeat";
            var tokenManager = TokenManager.Instance!;
            var response = await ApiHelpers.SendWithRetries(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Delete, requestUri);
                ApiHelpers.AddAuthHeader(request, tokenManager);
                return request;
            }, _httpClient);
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
        _description = template.Description ?? string.Empty;
        _descriptionSelectionStart = _descriptionSelectionEnd = _description.Length;
        SetTimeFromString(template.Time);
        _url = template.Url;
        _imageUrl = template.ImageUrl;
        _thumbnailUrl = template.ThumbnailUrl;
        ResetUploadState(_bannerUpload, template.ImageId, template.ImageUrl);
        ResetUploadState(_thumbnailUpload, template.ThumbnailId, template.ThumbnailUrl);
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
                    Url = b.Url,
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
            var isoTime = GetIsoTime();
            var payload = new
            {
                channelId = ChannelId,
                title = dto.Title,
                time = isoTime,
                description = dto.Description,
                url = dto.Url,
                imageUrl = dto.ImageUrl,
                imageId = ResolveUploadId(_bannerUpload, dto.ImageUrl),
                thumbnailUrl = dto.ThumbnailUrl,
                thumbnailId = ResolveUploadId(_thumbnailUpload, dto.ThumbnailUrl),
                color = dto.Color,
                fields = dto.Fields?.Select(f => new { name = f.Name, value = f.Value, inline = f.Inline }).ToList(),
                buttons = buttons.Select(b => new { label = b.Label, customId = b.CustomId, url = b.Url, style = b.Style.HasValue ? (int?)b.Style : null, emoji = b.Emoji, maxSignups = b.MaxSignups, width = b.Width, rowIndex = b.RowIndex }).ToList(),
                mentions = _mentions.Count > 0 ? _mentions.ToList() : null
            };
            var body = new
            {
                name = _title,
                description = _description,
                payload
            };
            var requestUri = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/templates";
            var json = JsonSerializer.Serialize(body, JsonOpts);
            var tokenManager = TokenManager.Instance!;
            var response = await ApiHelpers.SendWithRetries(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                ApiHelpers.AddAuthHeader(request, tokenManager);
                return request;
            }, _httpClient);
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
            var isoTime = GetIsoTime();
            var body = new
            {
                channelId,
                title = dto.Title,
                time = isoTime,
                description = dto.Description,
                url = dto.Url,
                imageUrl = dto.ImageUrl,
                imageId = ResolveUploadId(_bannerUpload, dto.ImageUrl),
                thumbnailUrl = dto.ThumbnailUrl,
                thumbnailId = ResolveUploadId(_thumbnailUpload, dto.ThumbnailUrl),
                color = dto.Color,
                fields = dto.Fields?.Select(f => new { name = f.Name, value = f.Value, inline = f.Inline }).ToList(),
                buttons = buttons.Select(b => new { label = b.Label, customId = b.CustomId, url = b.Url, emoji = b.Emoji, style = b.Style.HasValue ? (int)b.Style : (int?)null, maxSignups = b.MaxSignups, width = b.Width, rowIndex = b.RowIndex }).ToList(),
                mentions = _mentions.Count > 0 ? _mentions.Select(ulong.Parse).ToList() : null,
                repeat = _repeat == RepeatOption.None ? null : _repeat.ToString().ToLowerInvariant()
            };

            var requestUri = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/events";
            var json = JsonSerializer.Serialize(body, JsonOpts);
            var tokenManager = TokenManager.Instance!;
            var response = await ApiHelpers.SendWithRetries(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                ApiHelpers.AddAuthHeader(request, tokenManager);
                return request;
            }, _httpClient);
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
                Url = b.Url,
                MaxSignups = b.MaxSignups,
                Width = b.Width
            });
        }
    }

    private void WrapDescription(string prefix, string suffix)
    {
        var description = _description;
        MarkdownSelectionHelper.WrapSelection(ref description, prefix, suffix, ref _descriptionSelectionStart, ref _descriptionSelectionEnd);
        _description = description ?? string.Empty;
    }

    private void InsertDescriptionText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var value = _description ?? string.Empty;
        var start = Math.Min(_descriptionSelectionStart, _descriptionSelectionEnd);
        var end = Math.Max(_descriptionSelectionStart, _descriptionSelectionEnd);
        start = Math.Clamp(start, 0, value.Length);
        end = Math.Clamp(end, 0, value.Length);

        var builder = new StringBuilder(value.Length + text.Length);
        if (start > 0)
        {
            builder.Append(value.AsSpan(0, start));
        }

        builder.Append(text);

        if (end < value.Length)
        {
            builder.Append(value.AsSpan(end));
        }

        _description = builder.ToString();
        var caret = start + text.Length;
        _descriptionSelectionStart = _descriptionSelectionEnd = caret;
        _focusDescriptionNextFrame = true;
    }

    private unsafe int OnDescriptionEdited(ImGuiInputTextCallbackData* data)
    {
        if (data == null)
        {
            return 0;
        }

        _descriptionSelectionStart = data->SelectionStart;
        _descriptionSelectionEnd = data->SelectionEnd;
        return 0;
    }

    private void ApplyTime(DateTimeOffset value)
    {
        _timePicker.SetValue(value);
        _time = value.ToUniversalTime().ToString("O");
    }

    private void SetTimeFromString(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            DateTimeOffset.TryParse(value, null, DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            ApplyTime(parsed);
        }
        else
        {
            ApplyTime(DateTimePicker.GetDefaultTime());
        }
    }

    private string GetIsoTime()
    {
        var iso = _timePicker.Value.ToUniversalTime().ToString("O");
        _time = iso;
        return iso;
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
                Url = string.IsNullOrWhiteSpace(b.Url) ? null : b.Url,
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

        DateTimeOffset? timestamp = _timePicker.Value.ToUniversalTime();

        var creatorLabel = MembershipCache.GetCreatorLabel();

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
            creatorLabel: creatorLabel,
            embedId: "event-create-preview");
    }

    private void DrawImageInput(ImageUploadKind kind)
    {
        ref var url = ref kind == ImageUploadKind.Banner ? ref _imageUrl : ref _thumbnailUrl;
        var state = kind == ImageUploadKind.Banner ? _bannerUpload : _thumbnailUpload;
        var label = kind == ImageUploadKind.Banner ? "Image" : "Thumbnail";
        var idSuffix = kind == ImageUploadKind.Banner ? "Image" : "Thumbnail";

        ImGui.SetNextItemWidth(-1f);
        var changed = ImGui.InputText($"##{idSuffix}Url", ref url, 260);
        if (changed && state.Info != null && !UrlMatchesUpload(state.Info, url))
        {
            state.Info = null;
            state.Status = string.Empty;
            state.IsError = false;
        }

        ImGui.SameLine();
        var dialog = kind == ImageUploadKind.Banner ? _imageFileDialog : _thumbnailFileDialog;
        ImGui.BeginDisabled(state.Uploading);
        if (ImGui.Button($"Upload##{idSuffix}"))
        {
            dialog.OpenFileDialog(
                $"Select {label}",
                "Image files{.png,.jpg,.jpeg,.gif,.bmp,.webp}",
                (ok, files) =>
                {
                    if (!ok || files.Count == 0) return;
                    var path = files[0];
                    PluginServices.Instance!.Framework.RunOnTick(() => BeginImageUpload(kind, path));
                },
                1,
                ".");
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button($"Clear##{idSuffix}"))
        {
            ClearUploadedImage(kind);
        }

        var status = state.Uploading
            ? (string.IsNullOrEmpty(state.Status) ? "Uploading..." : state.Status)
            : state.Status;
        var infoText = state.Info != null && string.IsNullOrEmpty(status)
            ? $"Using uploaded image ({state.Info.FileName ?? state.Info.Id})"
            : null;

        if (!string.IsNullOrEmpty(status))
        {
            ImGui.SameLine();
            if (state.IsError)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.4f, 0.4f, 1f));
                ImGui.TextUnformatted(status);
                ImGui.PopStyleColor();
            }
            else
            {
                ImGui.TextUnformatted(status);
            }
        }
        else if (!string.IsNullOrEmpty(infoText))
        {
            ImGui.SameLine();
            ImGui.TextUnformatted(infoText);
        }
    }

    private void BeginImageUpload(ImageUploadKind kind, string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            SetUploadFailure(kind, "File not found");
            return;
        }

        if (!ApiHelpers.ValidateApiBaseUrl(_config))
        {
            SetUploadFailure(kind, "Invalid API URL");
            return;
        }

        var tokenManager = TokenManager.Instance;
        if (tokenManager?.IsReady() != true)
        {
            SetUploadFailure(kind, "Link DemiCat to upload images");
            return;
        }

        var state = kind == ImageUploadKind.Banner ? _bannerUpload : _thumbnailUpload;
        if (state.Uploading)
        {
            return;
        }

        var fileName = Path.GetFileName(path);
        SetUploadInProgress(kind, $"Uploading {fileName}...");
        _ = Task.Run(() => UploadImageInternal(kind, path, tokenManager));
    }

    private async Task UploadImageInternal(ImageUploadKind kind, string path, TokenManager tokenManager)
    {
        try
        {
            var requestUri = BuildUploadUri(kind);
            var response = await ApiHelpers.SendWithRetries(
                () => BuildUploadRequest(path, requestUri, tokenManager),
                _httpClient);

            if (response?.IsSuccessStatusCode == true)
            {
                var body = await response.Content.ReadAsStringAsync();
                var info = ParseUploadResponse(body, kind, Path.GetFileName(path));
                if (info != null && !string.IsNullOrEmpty(info.Url))
                {
                    SetUploadSuccess(kind, info);
                }
                else
                {
                    var message = string.IsNullOrEmpty(body)
                        ? "Upload succeeded but response was empty"
                        : "Upload succeeded but response was missing data";
                    SetUploadFailure(kind, message);
                }
            }
            else if (response != null)
            {
                var body = await response.Content.ReadAsStringAsync();
                SetUploadFailure(kind, $"Upload failed: {(int)response.StatusCode} {body}");
            }
            else
            {
                SetUploadFailure(kind, "Upload failed");
            }
        }
        catch (Exception ex)
        {
            PluginServices.Instance!.Log.Error(ex, "Error uploading event image");
            SetUploadFailure(kind, "Upload failed");
        }
    }

    private static HttpRequestMessage BuildUploadRequest(string path, string requestUri, TokenManager tokenManager)
    {
        var stream = File.OpenRead(path);
        var content = new StreamContent(stream);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = content
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        ApiHelpers.AddAuthHeader(request, tokenManager);
        return request;
    }

    private string BuildUploadUri(ImageUploadKind kind)
    {
        var baseUrl = _config.ApiBaseUrl.TrimEnd('/');
        var segment = kind == ImageUploadKind.Banner ? "banner" : "thumbnail";
        return $"{baseUrl}/api/event-images/{segment}";
    }

    private void SetUploadInProgress(ImageUploadKind kind, string message)
    {
        _ = PluginServices.Instance!.Framework.RunOnTick(() =>
        {
            var state = kind == ImageUploadKind.Banner ? _bannerUpload : _thumbnailUpload;
            state.Uploading = true;
            state.Status = message;
            state.IsError = false;
        });
    }

    private void SetUploadSuccess(ImageUploadKind kind, UploadedImageInfo info)
    {
        _ = PluginServices.Instance!.Framework.RunOnTick(() =>
        {
            var state = kind == ImageUploadKind.Banner ? _bannerUpload : _thumbnailUpload;
            state.Uploading = false;
            state.Info = info;
            state.Status = $"Uploaded {info.FileName ?? info.Id}";
            state.IsError = false;
            if (kind == ImageUploadKind.Banner)
            {
                _imageUrl = info.Url;
            }
            else
            {
                _thumbnailUrl = info.Url;
            }
        });
    }

    private void SetUploadFailure(ImageUploadKind kind, string message)
    {
        _ = PluginServices.Instance!.Framework.RunOnTick(() =>
        {
            var state = kind == ImageUploadKind.Banner ? _bannerUpload : _thumbnailUpload;
            state.Uploading = false;
            state.Status = message;
            state.IsError = true;
        });
    }

    private void ClearUploadedImage(ImageUploadKind kind)
    {
        _ = PluginServices.Instance!.Framework.RunOnTick(() =>
        {
            if (kind == ImageUploadKind.Banner)
            {
                _imageUrl = string.Empty;
                _bannerUpload.Info = null;
                _bannerUpload.Status = string.Empty;
                _bannerUpload.IsError = false;
                _bannerUpload.Uploading = false;
            }
            else
            {
                _thumbnailUrl = string.Empty;
                _thumbnailUpload.Info = null;
                _thumbnailUpload.Status = string.Empty;
                _thumbnailUpload.IsError = false;
                _thumbnailUpload.Uploading = false;
            }
        });
    }

    private static UploadedImageInfo? ParseUploadResponse(string body, ImageUploadKind kind, string fileName)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string? id;
            string? url;
            string? altUrl;

            if (kind == ImageUploadKind.Banner)
            {
                id = TryGetString(root, "imageId") ?? TryGetString(root, "id");
                url = TryGetString(root, "url") ?? TryGetString(root, "imageUrl") ?? TryGetString(root, "cdnUrl");
                altUrl = TryGetString(root, "thumbnailUrl");
            }
            else
            {
                id = TryGetString(root, "thumbnailId") ?? TryGetString(root, "id");
                url = TryGetString(root, "thumbnailUrl") ?? TryGetString(root, "url") ?? TryGetString(root, "imageUrl");
                altUrl = TryGetString(root, "url") ?? TryGetString(root, "imageUrl");
            }

            if (string.IsNullOrEmpty(url))
            {
                url = altUrl;
            }

            var contentType = TryGetString(root, "contentType") ?? TryGetString(root, "mimeType");
            var width = TryGetInt(root, "width") ?? TryGetInt(root, "imageWidth");
            var height = TryGetInt(root, "height") ?? TryGetInt(root, "imageHeight");
            var size = TryGetInt(root, "size") ?? TryGetInt(root, "fileSize");
            var responseFileName = TryGetString(root, "fileName") ?? TryGetString(root, "filename") ?? fileName;

            return new UploadedImageInfo
            {
                Id = id ?? string.Empty,
                Url = url ?? string.Empty,
                ThumbnailUrl = TryGetString(root, "thumbnailUrl"),
                FileName = responseFileName,
                ContentType = contentType,
                Width = width,
                Height = height,
                Size = size
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? TryGetInt(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static bool UrlMatchesUpload(UploadedImageInfo info, string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        return string.Equals(url, info.Url, StringComparison.OrdinalIgnoreCase) ||
               (!string.IsNullOrEmpty(info.ThumbnailUrl) && string.Equals(url, info.ThumbnailUrl, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ResolveUploadId(ImageUploadState state, string? currentUrl)
    {
        if (state.Info == null || string.IsNullOrEmpty(state.Info.Id) || string.IsNullOrWhiteSpace(currentUrl))
        {
            return null;
        }

        return UrlMatchesUpload(state.Info, currentUrl) ? state.Info.Id : null;
    }

    private static void ResetUploadState(ImageUploadState state, string? id, string url)
    {
        state.Uploading = false;
        state.Status = string.Empty;
        state.IsError = false;
        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(url))
        {
            state.Info = new UploadedImageInfo
            {
                Id = id!,
                Url = url,
                FileName = id
            };
        }
        else
        {
            state.Info = null;
        }
    }

    private enum ImageUploadKind
    {
        Banner,
        Thumbnail
    }

    private class ImageUploadState
    {
        public bool Uploading;
        public string Status = string.Empty;
        public bool IsError;
        public UploadedImageInfo? Info;
    }

    private class UploadedImageInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string? ThumbnailUrl { get; set; }
        public string? FileName { get; set; }
        public string? ContentType { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public int? Size { get; set; }
    }

    private void ApplyEventChannels(IReadOnlyList<ChannelDto> channels)
    {
        _channels.Clear();
        foreach (var channel in channels)
        {
            if (channel != null)
            {
                _channels.Add(channel);
            }
        }
        UpdateChannelDisplayNames();
        var current = ChannelId;
        if (!string.IsNullOrEmpty(current))
        {
            _selectedIndex = _channels.FindIndex(c => c.Id == current);
            if (_selectedIndex < 0)
            {
                _selectedIndex = 0;
            }
        }
        if (_channels.Count > 0)
        {
            _channelSelection.SetChannel(ChannelKind.Event, _config.GuildId, _channels[_selectedIndex].Id);
        }
        _channelsLoaded = true;
        _channelFetchFailed = false;
        _channelErrorMessage = string.Empty;
    }

    internal Task ApplyChannelRefreshResult(ChannelRefreshResult result)
    {
        switch (result.Error)
        {
            case ChannelRefreshError.None:
            case ChannelRefreshError.FeatureDisabled:
            {
                var channels = result.Channels ?? Array.Empty<ChannelDto>();
                return PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    ApplyEventChannels(channels);
                });
            }
            case ChannelRefreshError.TokenMissing:
            {
                return PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    _channelsLoaded = false;
                    _channelFetchFailed = false;
                    _channelErrorMessage = string.Empty;
                    _channels.Clear();
                    UpdateChannelDisplayNames();
                });
            }
            case ChannelRefreshError.InvalidApiUrl:
            {
                return PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    _channelFetchFailed = true;
                    _channelErrorMessage = "Invalid API URL";
                    _channelsLoaded = true;
                });
            }
            case ChannelRefreshError.Unauthorized:
            {
                return PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    _channelFetchFailed = true;
                    _channelErrorMessage = "Authentication failed";
                    _channelsLoaded = true;
                });
            }
            case ChannelRefreshError.Forbidden:
            {
                return PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    _channelFetchFailed = true;
                    _channelErrorMessage = "Forbidden – check API key/roles";
                    _channelsLoaded = true;
                });
            }
            case ChannelRefreshError.Generic:
            {
                return PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    _channelFetchFailed = true;
                    _channelErrorMessage = "Failed to load channels";
                    _channelsLoaded = true;
                });
            }
            default:
                return Task.CompletedTask;
        }
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
                UpdateChannelDisplayNames();
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

    private void UpdateChannelDisplayNames()
    {
        if (_channels.Count == 0)
        {
            _channelDisplayNames = Array.Empty<string>();
            return;
        }

        _channelDisplayNames = _channels
            .Select(c => c.ParentId == null ? c.Name : "  " + c.Name)
            .ToArray();
    }

    private void SavePreset()
    {
        if (_presetName.Length > 30) _presetName = _presetName[..30];
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
                Url = b.Url,
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

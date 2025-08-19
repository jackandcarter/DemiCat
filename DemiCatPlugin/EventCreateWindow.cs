using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Numerics;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using Dalamud.Bindings.ImGui;
using DiscordHelper;

namespace DemiCatPlugin;

public class EventCreateWindow
{
    private readonly Config _config;
    private readonly HttpClient _httpClient;

    private string _title = string.Empty;
    private string _description = string.Empty;
    private string _time = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'");
    private string _imageUrl = string.Empty;
    private string _url = string.Empty;
    private string _thumbnailUrl = string.Empty;
    private int _color;
    private readonly List<Field> _fields = new();
    private string? _lastResult;
    private readonly List<ButtonConfig> _buttons = new();
    private int _selectedPreset = -1;
    private string _presetName = string.Empty;
    private RepeatOption _repeat = RepeatOption.None;
    private readonly List<RepeatSchedule> _schedules = new();
    private bool _schedulesLoaded;
    private readonly List<RoleDto> _roles = new();
    private readonly HashSet<string> _mentions = new();
    private bool _rolesLoaded;
    private bool _roleFetchFailed;

    public string ChannelId { private get; set; } = string.Empty;

    public EventCreateWindow(Config config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
        ResetDefaultButtons();
        _ = SignupPresetService.EnsureLoaded(_httpClient, _config);
    }

    public void Draw()
    {
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
            var nextStr = next.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'");
            ImGui.TextUnformatted($"Next: {nextStr}");
        }
        ImGui.InputTextMultiline("Description", ref _description, 4096, new Vector2(400, 100));
        ImGui.InputText("URL", ref _url, 260);
        ImGui.InputText("Image URL", ref _imageUrl, 260);
        ImGui.InputText("Thumbnail URL", ref _thumbnailUrl, 260);
        var colorVec = new Vector3(
            ((_color >> 16) & 0xFF) / 255f,
            ((_color >> 8) & 0xFF) / 255f,
            (_color & 0xFF) / 255f);
        if (ImGui.ColorEdit3("Color", ref colorVec))
        {
            _color = ((int)(colorVec.X * 255) << 16) |
                     ((int)(colorVec.Y * 255) << 8) |
                     (int)(colorVec.Z * 255);
        }

        if (!_rolesLoaded)
        {
            _ = FetchRoles();
        }
        if (_rolesLoaded)
        {
            if (_roles.Count > 0)
            {
                ImGui.Text("Mention Roles");
                foreach (var role in _roles)
                {
                    var roleId = role.Id.ToString();
                    var sel = _mentions.Contains(roleId);
                    if (ImGui.Checkbox($"{role.Name}##role{role.Id}", ref sel))
                    {
                        if (sel) _mentions.Add(roleId); else _mentions.Remove(roleId);
                    }
                }
            }
            else if (_roleFetchFailed)
            {
                ImGui.TextUnformatted("Failed to load roles");
            }
        }
        for (var i = 0; i < _buttons.Count; i++)
        {
            var button = _buttons[i];
            ImGui.PushID(i);
            ImGui.Checkbox("Include", ref button.Include);
            ImGui.SameLine();
            ImGui.InputText("Tag", ref button.Tag, 32);
            ImGui.SameLine();
            ImGui.InputText("Label", ref button.Label, 32);
            ImGui.SameLine();
            ImGui.InputText("Emoji", ref button.Emoji, 16);
            ImGui.SameLine();
            var max = button.MaxSignups ?? 0;
            ImGui.SetNextItemWidth(60);
            if (ImGui.InputInt("Max", ref max))
            {
                button.MaxSignups = max > 0 ? max : null;
            }
            ImGui.SameLine();
            var style = button.Style.ToString();
            if (ImGui.BeginCombo("Style", style))
            {
                foreach (ButtonStyle bs in Enum.GetValues<ButtonStyle>())
                {
                    var sel = bs == button.Style;
                    if (ImGui.Selectable(bs.ToString(), sel)) button.Style = bs;
                    if (sel) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
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
            _buttons.Add(new ButtonConfig($"opt{_buttons.Count + 1}", "Option", string.Empty, ButtonStyle.Secondary));
        }

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
        if (ImGui.Button("Create"))
        {
            _ = CreateEvent();
        }
        ImGui.SameLine();
        if (ImGui.Button("Save Template"))
        {
            SaveTemplate();
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
    }

    private async Task FetchRoles()
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config))
        {
            _rolesLoaded = true;
            _roleFetchFailed = true;
            return;
        }
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/guild-roles");
            if (!string.IsNullOrEmpty(_config.AuthToken))
            {
                request.Headers.Add("X-Api-Key", _config.AuthToken);
            }
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _rolesLoaded = true;
                _roleFetchFailed = true;
                return;
            }
            var stream = await response.Content.ReadAsStreamAsync();
            var roles = await JsonSerializer.DeserializeAsync<List<RoleDto>>(stream) ?? new List<RoleDto>();
            PluginServices.Instance!.Framework.RunOnTick(() =>
            {
                _roles.Clear();
                _roles.AddRange(roles);
                _rolesLoaded = true;
            });
        }
        catch
        {
            _rolesLoaded = true;
            _roleFetchFailed = true;
        }
    }

    private async Task FetchSchedules()
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config))
        {
            _schedulesLoaded = true;
            return;
        }
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/events/repeat");
            if (!string.IsNullOrEmpty(_config.AuthToken))
            {
                request.Headers.Add("X-Api-Key", _config.AuthToken);
            }
            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var stream = await response.Content.ReadAsStreamAsync();
                var schedules = await JsonSerializer.DeserializeAsync<List<RepeatSchedule>>(stream) ?? new();
                PluginServices.Instance!.Framework.RunOnTick(() =>
                {
                    _schedules.Clear();
                    _schedules.AddRange(schedules);
                    _schedulesLoaded = true;
                });
            }
            else
            {
                _schedulesLoaded = true;
            }
        }
        catch
        {
            _schedulesLoaded = true;
        }
    }

    private async Task CancelSchedule(string id)
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config)) return;
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Delete, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/events/{id}/repeat");
            if (!string.IsNullOrEmpty(_config.AuthToken))
            {
                request.Headers.Add("X-Api-Key", _config.AuthToken);
            }
            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                _schedulesLoaded = false;
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
            ? DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'")
            : template.Time;
        _url = template.Url;
        _imageUrl = template.ImageUrl;
        _thumbnailUrl = template.ThumbnailUrl;
        _color = (int)template.Color;
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
                _buttons.Add(new ButtonConfig(b.Tag, b.Label, b.Emoji, b.Style)
                {
                    Include = b.Include,
                    MaxSignups = b.MaxSignups
                });
            }
        }
        else
        {
            ResetDefaultButtons();
        }
    }

    private void SaveTemplate()
    {
        var tmpl = new Template
        {
            Name = _title,
            Type = TemplateType.Event,
            Title = _title,
            Description = _description,
            Time = _time,
            Url = _url,
            ImageUrl = _imageUrl,
            ThumbnailUrl = _thumbnailUrl,
            Color = (uint)_color,
            Fields = _fields.Select(f => new Template.TemplateField
            {
                Name = f.Name,
                Value = f.Value,
                Inline = f.Inline
            }).ToList(),
            Buttons = _buttons.Select(b => new Template.TemplateButton
            {
                Tag = b.Tag,
                Include = b.Include,
                Label = b.Label,
                Emoji = b.Emoji,
                Style = b.Style,
                MaxSignups = b.MaxSignups
            }).ToList(),
            Mentions = _mentions.Select(ulong.Parse).ToList()
        };

        _config.Templates.Add(tmpl);
        PluginServices.Instance!.PluginInterface.SavePluginConfig(_config);
        _lastResult = "Template saved";
    }

    private async Task CreateEvent()
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config) || string.IsNullOrWhiteSpace(ChannelId))
        {
            if (string.IsNullOrWhiteSpace(ChannelId))
            {
                _lastResult = "No channel selected";
            }
            return;
        }
        try
        {
            var buttons = _buttons
                .Where(b => b.Include)
                .Select(b => new
                {
                    label = b.Label,
                    customId = $"rsvp:{b.Tag}",
                    emoji = string.IsNullOrWhiteSpace(b.Emoji) ? null : b.Emoji,
                    style = (int)b.Style,
                    maxSignups = b.MaxSignups
                })
                .ToList();

            var body = new
            {
                channelId = ChannelId,
                title = _title,
                time = _time,
                description = _description,
                url = string.IsNullOrWhiteSpace(_url) ? null : _url,
                imageUrl = string.IsNullOrWhiteSpace(_imageUrl) ? null : _imageUrl,
                thumbnailUrl = string.IsNullOrWhiteSpace(_thumbnailUrl) ? null : _thumbnailUrl,
                color = _color > 0 ? (uint?)_color : null,
                fields = _fields.Count > 0
                    ? _fields
                        .Where(f => !string.IsNullOrWhiteSpace(f.Name) && !string.IsNullOrWhiteSpace(f.Value))
                        .Select(f => new { name = f.Name, value = f.Value, inline = f.Inline })
                        .ToList()
                    : null,
                buttons = buttons.Count > 0 ? buttons : null,
                mentions = _mentions.Count > 0 ? _mentions.Select(ulong.Parse).ToList() : null,
                repeat = _repeat == RepeatOption.None ? null : _repeat.ToString().ToLowerInvariant()
            };

            var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ApiBaseUrl.TrimEnd('/')}/api/events");
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            if (!string.IsNullOrEmpty(_config.AuthToken))
            {
                request.Headers.Add("X-Api-Key", _config.AuthToken);
            }

            var response = await _httpClient.SendAsync(request);
            _lastResult = response.IsSuccessStatusCode ? "Event posted" : $"Error: {response.StatusCode}";
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
            _buttons.Add(new ButtonConfig(b.Tag, b.Label, b.Emoji, b.Style)
            {
                Include = b.Include,
                MaxSignups = b.MaxSignups
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
                Emoji = b.Emoji,
                Style = b.Style,
                MaxSignups = b.MaxSignups
            }).ToList()
        };
        _ = SignupPresetService.Create(preset, _httpClient, _config);
        _presetName = string.Empty;
    }

    private void ResetDefaultButtons()
    {
        _buttons.Clear();
        _buttons.Add(new ButtonConfig("yes", "Yes", "✅", ButtonStyle.Success));
        _buttons.Add(new ButtonConfig("maybe", "Maybe", "❔", ButtonStyle.Secondary));
        _buttons.Add(new ButtonConfig("no", "No", "❌", ButtonStyle.Danger));
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

    private class ButtonConfig
    {
        public ButtonConfig(string tag, string label, string emoji, ButtonStyle style)
        {
            Tag = tag;
            Label = label;
            Emoji = emoji;
            Style = style;
        }

        public string Tag;
        public bool Include = true;
        public string Label;
        public string Emoji;
        public ButtonStyle Style;
        public int? MaxSignups;
    }

    private class RepeatSchedule
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Repeat { get; set; } = string.Empty;
        public string Next { get; set; } = string.Empty;
    }
}

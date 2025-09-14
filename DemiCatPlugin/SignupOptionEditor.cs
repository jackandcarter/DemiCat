using System;
using Dalamud.Bindings.ImGui;
using DiscordHelper;
using System.Net.Http;
using DemiCat.UI;
using DemiCatPlugin.Emoji;

namespace DemiCatPlugin;

public class SignupOptionEditor
{
    private bool _open;
    private Template.TemplateButton _working = new();
    private Action<Template.TemplateButton>? _onSave;
    private readonly EmojiService _emojiService;
    private readonly EmojiPicker _emojiPicker;

    public SignupOptionEditor(Config config, HttpClient httpClient)
    {
        _emojiService = new EmojiService(httpClient, TokenManager.Instance!, config);
        _emojiPicker = new EmojiPicker(_emojiService);
        _ = _emojiService.RefreshAsync();
    }

    public void Open(Template.TemplateButton button, Action<Template.TemplateButton> onSave)
    {
        _working = new Template.TemplateButton
        {
            Tag = button.Tag,
            Include = button.Include,
            Label = button.Label,
            Emoji = button.Emoji,
            Style = button.Style,
            MaxSignups = button.MaxSignups,
            Width = button.Width
        };
        _onSave = onSave;
        _open = true;
    }

    public void Draw()
    {
        if (!_open) return;
        ImGui.OpenPopup("Signup Option");
        var open = _open;
        if (ImGui.BeginPopupModal("Signup Option", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            var tag = _working.Tag;
            if (ImGui.InputText("Tag", ref tag, 32))
                _working.Tag = tag;
            var label = _working.Label;
            if (ImGui.InputText("Label", ref label, 64))
                _working.Label = label;
            ImGui.SameLine();
            if (ImGui.Button("Pick Emoji")) ImGui.OpenPopup("##dc_btn_emoji");
            if (ImGui.BeginPopup("##dc_btn_emoji"))
            {
                string temp = _working.Emoji ?? string.Empty;
                _emojiPicker.Draw(ref temp);
                ImGui.Separator();
                if (ImGui.Button("Use Above"))
                {
                    _working.Emoji = temp;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndPopup();
            }
            if (!string.IsNullOrWhiteSpace(_working.Emoji))
            {
                ImGui.SameLine();
                ImGui.TextUnformatted(_working.Emoji);
            }
            var max = _working.MaxSignups ?? 0;
            if (ImGui.InputInt("Max Signups", ref max))
            {
                _working.MaxSignups = max > 0 ? max : null;
            }
            var width = _working.Width ?? 0;
            if (ImGui.InputInt("Width", ref width))
            {
                _working.Width = width > 0 ? Math.Min(width, ButtonSizeHelper.Max) : null;
            }
            ImGui.SameLine();
            ImGui.Text($"Auto: {ButtonSizeHelper.ComputeWidth(_working.Label)}");
            var style = _working.Style.ToString();
            if (ImGui.BeginCombo("Style", style))
            {
                foreach (ButtonStyle bs in Enum.GetValues<ButtonStyle>())
                {
                    if (bs == ButtonStyle.Link) continue;
                    var sel = bs == _working.Style;
                    if (ImGui.Selectable(bs.ToString(), sel)) _working.Style = bs;
                    if (sel) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
            if (ImGui.Button("Save"))
            {
                _onSave?.Invoke(new Template.TemplateButton
                {
                    Tag = _working.Tag,
                    Include = _working.Include,
                    Label = _working.Label,
                    Emoji = _working.Emoji,
                    Style = _working.Style,
                    MaxSignups = _working.MaxSignups,
                    Width = _working.Width ?? ButtonSizeHelper.ComputeWidth(_working.Label)
                });
                _open = false;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                _open = false;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
        if (!open) _open = false;
    }
}

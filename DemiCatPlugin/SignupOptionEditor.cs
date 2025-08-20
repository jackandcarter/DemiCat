using System;
using Dalamud.Bindings.ImGui;
using DiscordHelper;
using System.Numerics;

namespace DemiCatPlugin;

public class SignupOptionEditor
{
    private bool _open;
    private Template.TemplateButton _working = new();
    private Action<Template.TemplateButton>? _onSave;

    public void Open(Template.TemplateButton button, Action<Template.TemplateButton> onSave)
    {
        _working = new Template.TemplateButton
        {
            Tag = button.Tag,
            Include = button.Include,
            Label = button.Label,
            Emoji = button.Emoji,
            Style = button.Style,
            MaxSignups = button.MaxSignups
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
            ImGui.InputText("Tag", ref _working.Tag, 32);
            ImGui.InputText("Label", ref _working.Label, 64);
            ImGui.InputText("Emoji", ref _working.Emoji, 16);
            var max = _working.MaxSignups ?? 0;
            if (ImGui.InputInt("Max Signups", ref max))
            {
                _working.MaxSignups = max > 0 ? max : null;
            }
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
                    MaxSignups = _working.MaxSignups
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

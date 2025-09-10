using System;
using Dalamud.Bindings.ImGui;
using DiscordHelper;
using System.Numerics;
using System.Net.Http;

namespace DemiCatPlugin;

public class SignupOptionEditor
{
    private bool _open;
    private Template.TemplateButton _working = new();
    private Action<Template.TemplateButton>? _onSave;
    private readonly EmojiPopup _emojiPopup;

    public SignupOptionEditor(Config config, HttpClient httpClient)
    {
        _emojiPopup = new EmojiPopup(config, httpClient);
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
            Width = button.Width,
            Height = button.Height
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
            var emoji = _working.Emoji;
            if (ImGui.InputText("Emoji", ref emoji, 16))
                _working.Emoji = emoji;
            ImGui.SameLine();
            if (ImGui.Button("Pick"))
            {
                _emojiPopup.Open(e => _working.Emoji = e);
            }

            if (!string.IsNullOrWhiteSpace(_working.Emoji))
            {
                if (_working.Emoji.StartsWith("custom:", StringComparison.OrdinalIgnoreCase))
                {
                    var id = _working.Emoji.Substring("custom:".Length);
                    var ext = EmojiPopup.IsGuildEmojiAnimated(id) ? "gif" : "png"; // we won’t animate but PNG works for most
                    var url = $"https://cdn.discordapp.com/emojis/{id}.{ext}";
                    WebTextureCache.Get(url, tex =>
                    {
                        if (tex != null)
                        {
                            var wrap = tex.GetWrapOrEmpty();
                            ImGui.Image(wrap.Handle, new Vector2(20, 20));
                            ImGui.SameLine();
                        }
                    });
                }
                else
                {
                    ImGui.TextUnformatted(_working.Emoji);
                    ImGui.SameLine();
                }
            }
            var max = _working.MaxSignups ?? 0;
            if (ImGui.InputInt("Max Signups", ref max))
            {
                _working.MaxSignups = max > 0 ? max : null;
            }
            var width = _working.Width ?? 0;
            if (ImGui.InputInt("Width", ref width))
            {
                _working.Width = width > 0 ? width : null;
            }
            var height = _working.Height ?? 0;
            if (ImGui.InputInt("Height", ref height))
            {
                _working.Height = height > 0 ? height : null;
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
            _emojiPopup.Draw();

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
                    Width = _working.Width,
                    Height = _working.Height
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

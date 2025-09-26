using System;
using Dalamud.Bindings.ImGui;
using DiscordHelper;
using System.Numerics;
using System.Net.Http;
using System.Text;
using DemiCat.UI;
using DemiCatPlugin.Emoji;

namespace DemiCatPlugin;

public class SignupOptionEditor
{
    private bool _open;
    private Template.TemplateButton _working = new();
    private Action<Template.TemplateButton>? _onSave;
    private readonly EmojiPopup _emojiPopup;
    private readonly EmojiManager _emojiManager;
    private int _emojiSelectionStart;
    private int _emojiSelectionEnd;
    private bool _focusEmojiNextFrame;

    public SignupOptionEditor(Config config, HttpClient httpClient, EmojiManager emojiManager)
    {
        _emojiManager = emojiManager;
        _emojiPopup = new EmojiPopup(emojiManager);
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
            Url = button.Url,
            MaxSignups = button.MaxSignups,
            Width = button.Width
        };
        _emojiSelectionStart = _emojiSelectionEnd = _working.Emoji?.Length ?? 0;
        _focusEmojiNextFrame = false;
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
            var emojiBuf = ImGuiTextUtil.MakeUtf8Buffer(_working.Emoji ?? string.Empty, 64);
            if (_focusEmojiNextFrame)
            {
                ImGui.SetKeyboardFocusHere();
                _focusEmojiNextFrame = false;
            }
            ImGui.InputText(
                "Emoji",
                emojiBuf,
                ImGuiInputTextFlags.CallbackAlways,
                OnEmojiEdited
            );
            var emoji = ImGuiTextUtil.ReadUtf8Buffer(emojiBuf);
            if (_working.Emoji != emoji)
            {
                _working.Emoji = emoji;
                _emojiSelectionStart = Math.Clamp(_emojiSelectionStart, 0, _working.Emoji.Length);
                _emojiSelectionEnd = Math.Clamp(_emojiSelectionEnd, 0, _working.Emoji.Length);
            }
            ImGui.SameLine();
            if (ImGui.Button("Pick"))
            {
                _emojiPopup.Open(InsertEmojiText);
            }

            if (!string.IsNullOrWhiteSpace(_working.Emoji))
            {
                EmojiRenderer.Draw(_working.Emoji, _emojiManager);
                ImGui.SameLine();
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
                    var color = EmbedPreviewRenderer.GetStyleColor(bs);
                    ImGui.PushStyleColor(ImGuiCol.Text, color);
                    if (ImGui.Selectable(bs.ToString(), sel)) _working.Style = bs;
                    ImGui.PopStyleColor();
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
                    Url = _working.Url,
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

    private void InsertEmojiText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var value = _working.Emoji ?? string.Empty;
        var start = Math.Min(_emojiSelectionStart, _emojiSelectionEnd);
        var end = Math.Max(_emojiSelectionStart, _emojiSelectionEnd);
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

        var result = builder.ToString();
        _working.Emoji = result;
        var caret = start + text.Length;
        _emojiSelectionStart = _emojiSelectionEnd = caret;
        _focusEmojiNextFrame = true;
    }

    private int OnEmojiEdited(scoped ref ImGuiInputTextCallbackData data)
    {
        _emojiSelectionStart = data.SelectionStart;
        _emojiSelectionEnd = data.SelectionEnd;
        return 0;
    }
}

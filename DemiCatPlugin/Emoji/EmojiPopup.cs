using System;
using ImGuiNET;

namespace DemiCatPlugin.Emoji;

public sealed class EmojiPopup
{
    private readonly EmojiPicker _picker;
    private readonly string _popupId;
    private Action<string>? _onSelected;

    public EmojiPopup(Config config, EmojiManager manager, string popupId = "PickEmoji", Action? persistSettings = null)
    {
        _picker = new EmojiPicker(manager, config, persistSettings);
        _popupId = popupId;
    }

    public void Open(Action<string> onSelected)
    {
        _onSelected = onSelected;
        ImGui.OpenPopup(_popupId);
    }

    public void Draw()
    {
        if (!ImGui.BeginPopup(_popupId))
        {
            return;
        }

        var selected = _picker.Draw();
        if (!string.IsNullOrEmpty(selected))
        {
            _onSelected?.Invoke(selected);
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }
}

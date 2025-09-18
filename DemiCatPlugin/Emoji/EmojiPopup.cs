using System;
using Dalamud.Bindings.ImGui;

namespace DemiCatPlugin.Emoji;

public sealed class EmojiPopup
{
    private readonly EmojiPicker _picker;
    private readonly string _popupId;
    private Action<string>? _onSelected;

    public EmojiPopup(EmojiManager manager, string popupId = "PickEmoji")
    {
        _picker = new EmojiPicker(manager);
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

        var selected = string.Empty;
        _picker.Draw(ref selected);
        if (!string.IsNullOrEmpty(selected))
        {
            _onSelected?.Invoke(selected);
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }
}

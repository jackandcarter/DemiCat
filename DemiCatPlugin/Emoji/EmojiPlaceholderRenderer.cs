using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace DemiCatPlugin.Emoji;

internal static class EmojiPlaceholderRenderer
{
    private static readonly uint FillColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.06f));
    private static readonly uint BorderColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.15f));
    private const float CornerRadius = 4f;

    public static void Draw(Vector2 size)
    {
        ImGui.Dummy(size);
        DrawDecoration();
    }

    public static bool DrawButton(Vector2 size)
    {
        var clicked = ImGui.InvisibleButton("##placeholder", size);
        DrawDecoration();
        return clicked;
    }

    private static void DrawDecoration()
    {
        if (!ImGui.IsItemVisible())
        {
            return;
        }

        var drawList = ImGui.GetWindowDrawList();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        drawList.AddRectFilled(min, max, FillColor, CornerRadius);
        drawList.AddRect(min, max, BorderColor, CornerRadius);
    }
}

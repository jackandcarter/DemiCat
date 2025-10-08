// Adds the old bool-border overload back so existing code compiles.
// Works because ImGui is a partial class in ImGui.NET.
using System.Numerics;

namespace ImGuiNET
{
    public static partial class ImGui
    {
        public static bool BeginChild(string str_id, Vector2 size, bool border, ImGuiWindowFlags flags = ImGuiWindowFlags.None)
        {
            var childFlags = border ? ImGuiChildFlags.Borders : ImGuiChildFlags.None;
            return BeginChild(str_id, size, childFlags, flags);
        }
    }
}

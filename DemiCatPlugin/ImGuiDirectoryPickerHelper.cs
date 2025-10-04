using System;
using System.IO;
using System.Numerics;
using ImGuiNET;
using Dalamud.Interface.ImGuiFileDialog;

namespace DemiCatPlugin;

public static class ImGuiDirectoryPickerHelper
{
    public static void DrawDirectoryPicker(
        string label,
        string helpText,
        Func<string?> getter,
        Action<string?> setter,
        FileDialogManager dialog,
        string idSuffix,
        Func<string?, string> normalizeDirectory,
        Action<FileDialogManager, string, string?, Action<string?>> openFolderDialog)
    {
        var value = getter() ?? string.Empty;
        if (ImGui.InputText(label, ref value, 260))
        {
            setter(value);
        }

        ImGui.SameLine();
        if (ImGui.Button($"Browse##{idSuffix}"))
        {
            openFolderDialog(dialog, label, getter(), setter);
        }

        var current = getter();
        if (!string.IsNullOrEmpty(normalizeDirectory(current)))
        {
            ImGui.SameLine();
            if (ImGui.Button($"Clear##{idSuffix}"))
            {
                setter(string.Empty);
            }
        }

        if (!string.IsNullOrWhiteSpace(helpText))
        {
            ImGui.TextDisabled(helpText);
        }

        current = normalizeDirectory(getter());
        if (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
        {
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.6f, 1f), "Directory not found; automatic detection will be used instead.");
        }

        ImGui.Spacing();
    }
}

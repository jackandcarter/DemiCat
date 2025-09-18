using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace DemiCatPlugin.Emoji;

public sealed class EmojiPicker
{
    private readonly EmojiManager _manager;
    private EmojiTab _tab;
    private string _search = string.Empty;

    private enum EmojiTab
    {
        Standard,
        Custom
    }

    public EmojiPicker(EmojiManager manager) => _manager = manager;

    public void Draw(ref string targetText, float buttonSize = 28f)
    {
        var previous = _tab;
        if (ImGui.BeginTabBar("##dc_emoji_tabs"))
        {
            if (ImGui.BeginTabItem("Standard"))
            {
                _tab = EmojiTab.Standard;
                DrawStandard(ref targetText, buttonSize);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Custom"))
            {
                _tab = EmojiTab.Custom;
                DrawCustom(ref targetText, buttonSize);
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        if (_tab != previous)
        {
            _search = string.Empty;
        }
    }

    private void DrawStandard(ref string targetText, float size)
    {
        ImGui.InputTextWithHint("##emoji_std_search", "Search…", ref _search, 64);
        ImGui.Separator();

        if (!_manager.CanLoadStandard)
        {
            ImGui.TextDisabled("Link DemiCat to load emoji.");
            return;
        }

        _ = _manager.EnsureUnicodeAsync();

        var status = _manager.UnicodeStatus;
        var items = _manager.Unicode;
        IReadOnlyList<UnicodeEmoji> filtered = items;

        if (!string.IsNullOrWhiteSpace(_search))
        {
            var matches = new List<UnicodeEmoji>();
            foreach (var emoji in items)
            {
                if (emoji.Name.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
                    emoji.Emoji.Contains(_search, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(emoji);
                }
            }
            filtered = matches;
        }

        if (filtered.Count == 0)
        {
            if (status.Loading)
            {
                ImGui.TextDisabled("Loading emoji…");
            }
            else if (status.HasError)
            {
                DrawError(status.Error!);
            }
            else
            {
                ImGui.TextDisabled(string.IsNullOrWhiteSpace(_search) ? "No emoji available." : "No matches.");
            }
            return;
        }

        ImGui.BeginChild("##emoji_std_grid", new Vector2(0, 220f), false);
        var avail = ImGui.GetContentRegionAvail().X;
        var columns = Math.Max(1, (int)Math.Floor((avail + 4f) / (size + 4f)));
        var column = 0;

        for (var i = 0; i < filtered.Count; i++)
        {
            if (column >= columns)
            {
                ImGui.NewLine();
                column = 0;
            }

            var emoji = filtered[i];
            ImGui.PushID(i);
            if (ImGui.Button(emoji.Emoji, new Vector2(size, size)))
            {
                EmojiFormatter.InsertUnicode(ref targetText, emoji);
            }
            if (!string.IsNullOrEmpty(emoji.Name) && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(emoji.Name);
            }
            ImGui.PopID();

            column++;
            if (column < columns)
            {
                ImGui.SameLine();
            }
        }

        ImGui.EndChild();
    }

    private void DrawCustom(ref string targetText, float size)
    {
        ImGui.InputTextWithHint("##emoji_custom_search", "Search :name:", ref _search, 64);
        ImGui.SameLine();
        var canLoad = _manager.CanLoadCustom;
        if (!canLoad) ImGui.BeginDisabled();
        if (ImGui.Button("Refresh"))
        {
            _ = _manager.RefreshCustomAsync();
        }
        if (!canLoad) ImGui.EndDisabled();
        ImGui.Separator();

        if (!canLoad)
        {
            ImGui.TextDisabled("Set GuildId in config to enable server emoji.");
            return;
        }

        _ = _manager.EnsureCustomAsync();

        var status = _manager.CustomStatus;
        IReadOnlyList<CustomEmoji> items = _manager.Custom;

        if (!string.IsNullOrWhiteSpace(_search))
        {
            var matches = new List<CustomEmoji>();
            foreach (var emoji in items)
            {
                if (emoji.Name.Contains(_search, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(emoji);
                }
            }
            items = matches;
        }

        if (items.Count == 0)
        {
            if (status.Loading)
            {
                ImGui.TextDisabled("Loading server emoji…");
            }
            else if (status.HasError)
            {
                DrawError(status.Error!);
            }
            else
            {
                ImGui.TextDisabled(string.IsNullOrWhiteSpace(_search) ? "No server emoji found." : "No matches.");
            }
            return;
        }

        ImGui.BeginChild("##emoji_custom_grid", new Vector2(0, 220f), false);
        var avail = ImGui.GetContentRegionAvail().X;
        var columns = Math.Max(1, (int)Math.Floor((avail + 6f) / (size + 6f)));
        var column = 0;

        foreach (var emoji in items)
        {
            if (column >= columns)
            {
                ImGui.NewLine();
                column = 0;
            }

            var clicked = false;
            var tooltip = emoji.Animated ? $":{emoji.Name}: (gif)" : $":{emoji.Name}:";
            WebTextureCache.Get(emoji.ImageUrl, tex =>
            {
                ImGui.PushID(emoji.Id);
                if (tex != null)
                {
                    var wrap = tex.GetWrapOrEmpty();
                    if (ImGui.ImageButton(wrap.Handle, new Vector2(size, size)))
                    {
                        clicked = true;
                    }
                }
                else
                {
                    if (ImGui.Button(tooltip, new Vector2(size * 3f, size)))
                    {
                        clicked = true;
                    }
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(tooltip);
                }
                ImGui.PopID();
            });

            if (clicked)
            {
                EmojiFormatter.InsertCustom(ref targetText, emoji);
            }

            column++;
            if (column < columns)
            {
                ImGui.SameLine();
            }
        }

        ImGui.EndChild();
    }

    private static void DrawError(string message)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.4f, 0.4f, 1f));
        ImGui.TextWrapped(message);
        ImGui.PopStyleColor();
    }
}

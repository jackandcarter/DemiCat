using System;
using System.Linq;
using Dalamud.Bindings.ImGui;

namespace DemiCatPlugin.Emoji
{
    public sealed class EmojiPicker
    {
        private readonly EmojiService _svc;
        private int _tabIndex; // 0=Standard, 1=Custom
        private string _search = "";

        public EmojiPicker(EmojiService svc) => _svc = svc;

        public void Draw(ref string targetText, float buttonSize = 28f)
        {
            var prev = _tabIndex;
            if (ImGui.BeginTabBar("##dc_emoji_tabs"))
            {
                if (ImGui.BeginTabItem("Standard")) { _tabIndex = 0; DrawStandard(ref targetText, buttonSize); ImGui.EndTabItem(); }
                if (ImGui.BeginTabItem("Custom"))   { _tabIndex = 1; DrawCustom(ref targetText, buttonSize);   ImGui.EndTabItem(); }
                ImGui.EndTabBar();
            }
            if (prev != _tabIndex)
                _search = string.Empty;
        }

        private void DrawStandard(ref string targetText, float size)
        {
            ImGui.InputTextWithHint("##search_std", "Search…", ref _search, 64);
            ImGui.Separator();

            var items = string.IsNullOrWhiteSpace(_search)
                ? EmojiStrings.Popular
                : EmojiStrings.Popular.Where(p => p.Label.Contains(_search, StringComparison.OrdinalIgnoreCase)
                                               || p.Emoji.Contains(_search, StringComparison.OrdinalIgnoreCase)).ToArray();

            int col = 0;
            foreach (var (emoji, label) in items)
            {
                if (ImGui.Button(emoji, new(size, size)))
                { EmojiInsert.InsertUnicode(ref targetText, emoji); }
                if (++col % 10 != 0) ImGui.SameLine();
            }
        }

        private void DrawCustom(ref string targetText, float size)
        {
            ImGui.InputTextWithHint("##search_cus", "Search :name:", ref _search, 64);
            ImGui.SameLine();
            if (ImGui.Button("Refresh")) _ = _svc.RefreshAsync();

            ImGui.Separator();

            var items = string.IsNullOrWhiteSpace(_search)
                ? _svc.Custom
                : _svc.Custom.Where(c => c.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)).ToList();

            const int columns = 8;
            var col = 0;
            foreach (var e in items)
            {
                if (col == 0) ImGui.BeginGroup();

                var tooltip = e.Animated ? $":{e.Name}: (gif)" : $":{e.Name}:";
                void Insert() => EmojiInsert.InsertCustom(ref targetText, e);
                WebTextureCache.Get(e.ImageUrl, tex =>
                {
                    if (tex != null)
                    {
                        WebTextureCache.DrawImageButton($"custom_{e.Id}", tex, new(size, size), Insert);
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(tooltip);
                    }
                    else
                    {
                        if (ImGui.Button(tooltip, new(size * 3.2f, size)))
                        {
                            Insert();
                        }
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(tooltip);
                    }
                });

                col++;
                if (col >= columns)
                {
                    ImGui.NewLine();
                    ImGui.EndGroup();
                    col = 0;
                }
                else
                {
                    ImGui.SameLine();
                }
            }

            if (col != 0)
            {
                ImGui.NewLine();
                ImGui.EndGroup();
            }
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using DemiCatPlugin;

namespace DemiCatPlugin.Emoji;

public sealed class EmojiPicker
{
    private readonly EmojiManager _manager;
    private readonly Config _config;
    private readonly Action? _persistSettings;
    private float _tileSize;
    private float _gridHeight;
    private EmojiTab _tab;
    private string _search = string.Empty;
    private readonly ConcurrentDictionary<string, bool> _customTextureRequests = new();

    private enum EmojiTab
    {
        Standard,
        Custom
    }

    public EmojiPicker(EmojiManager manager, Config config, Action? persistSettings = null)
    {
        _manager = manager;
        _config = config;
        _persistSettings = persistSettings;
        _tileSize = Config.SanitizeEmojiTileSize(config.EmojiTileSize);
        _gridHeight = Config.SanitizeEmojiGridHeight(config.EmojiGridHeight);
    }

    public string? Draw()
    {
        var previous = _tab;
        string? selected = null;

        var size = _tileSize;
        if (ImGui.SliderFloat(
                "Emoji Size",
                ref size,
                Config.MinEmojiTileSize,
                Config.MaxEmojiTileSize,
                "%.0f px"))
        {
            SetTileSize(size);
        }

        var height = _gridHeight;
        if (ImGui.DragFloat(
                "Grid Height",
                ref height,
                1f,
                0f,
                Config.MaxEmojiGridHeight,
                height <= 0f ? "Fill" : "%.0f px"))
        {
            SetGridHeight(height);
        }

        ImGui.Separator();

        if (ImGui.BeginTabBar("##dc_emoji_tabs"))
        {
            if (ImGui.BeginTabItem("Standard"))
            {
                _tab = EmojiTab.Standard;
                selected = DrawStandard();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Custom"))
            {
                _tab = EmojiTab.Custom;
                selected ??= DrawCustom();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        if (_tab != previous)
        {
            _search = string.Empty;
        }

        return string.IsNullOrEmpty(selected) ? null : selected;
    }

    private string? DrawStandard()
    {
        ImGui.InputTextWithHint("##emoji_std_search", "Search…", ref _search, 64);
        ImGui.Separator();

        if (!_manager.CanLoadStandard)
        {
            ImGui.TextDisabled("Link DemiCat to load emoji.");
            return null;
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
            return null;
        }

        var childSize = _gridHeight > 0f ? new Vector2(0, _gridHeight) : Vector2.Zero;
        ImGui.BeginChild("##emoji_std_grid", childSize, false);
        var avail = ImGui.GetContentRegionAvail().X;
        var columns = Math.Max(1, (int)Math.Floor((avail + 4f) / (_tileSize + 4f)));
        var column = 0;
        string? selected = null;

        for (var i = 0; i < filtered.Count; i++)
        {
            if (column >= columns)
            {
                ImGui.NewLine();
                column = 0;
            }

            var emoji = filtered[i];
            ImGui.PushID(i);
            using var _ = _manager.PushEmojiFont();
            if (ImGui.Button(emoji.Emoji, new Vector2(_tileSize, _tileSize)))
            {
                selected = EmojiFormatter.CreateUnicodeToken(emoji);
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

            if (!string.IsNullOrEmpty(selected))
            {
                break;
            }
        }

        ImGui.EndChild();
        return string.IsNullOrEmpty(selected) ? null : selected;
    }

    private string? DrawCustom()
    {
        ImGui.InputTextWithHint("##emoji_custom_search", "Search :name:", ref _search, 64);
        ImGui.SameLine();
        var canLoad = _manager.CanLoadCustom;
        if (!canLoad) ImGui.BeginDisabled();
        if (ImGui.Button("Refresh"))
        {
            _customTextureRequests.Clear();
            _ = _manager.RefreshCustomAsync();
        }
        if (!canLoad) ImGui.EndDisabled();
        ImGui.Separator();

        if (!canLoad)
        {
            ImGui.TextDisabled("Set GuildId in config to enable server emoji.");
            return null;
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
            return null;
        }

        var childSize = _gridHeight > 0f ? new Vector2(0, _gridHeight) : Vector2.Zero;
        ImGui.BeginChild("##emoji_custom_grid", childSize, false);
        var avail = ImGui.GetContentRegionAvail().X;
        var columns = Math.Max(1, (int)Math.Floor((avail + 6f) / (_tileSize + 6f)));
        var column = 0;
        string? selected = null;

        foreach (var emoji in items)
        {
            if (column >= columns)
            {
                ImGui.NewLine();
                column = 0;
            }

            var clicked = false;
            var tooltip = emoji.Animated ? $":{emoji.Name}: (gif)" : $":{emoji.Name}:";
            var imageUrl = emoji.ImageUrl;

            if (!string.IsNullOrEmpty(imageUrl))
            {
                _customTextureRequests.GetOrAdd(imageUrl, key =>
                {
                    WebTextureCache.Get(key, tex =>
                    {
                        if (tex != null)
                        {
                            _customTextureRequests[key] = true;
                        }
                        else
                        {
                            _customTextureRequests.TryRemove(key, out _);
                        }
                    });

                    return false;
                });
            }

            ImGui.PushID(emoji.Id);
            if (!string.IsNullOrEmpty(imageUrl) &&
                WebTextureCache.TryGetTexture(imageUrl, out var texture) &&
                texture != null)
            {
                var wrap = texture.GetWrapOrEmpty();
                if (wrap.Handle.Handle != 0 && wrap.Width > 0 && wrap.Height > 0 &&
                    ImGui.ImageButton(wrap.ToImGuiHandle(), new Vector2(_tileSize, _tileSize)))
                {
                    clicked = true;
                }
            }
            else
            {
                if (ImGui.Button(tooltip, new Vector2(_tileSize * 3f, _tileSize)))
                {
                    clicked = true;
                }
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(tooltip);
            }
            ImGui.PopID();

            if (clicked)
            {
                selected = EmojiFormatter.CreateCustomToken(emoji);
            }

            column++;
            if (column < columns)
            {
                ImGui.SameLine();
            }

            if (!string.IsNullOrEmpty(selected))
            {
                break;
            }
        }

        ImGui.EndChild();
        return string.IsNullOrEmpty(selected) ? null : selected;
    }

    private static void DrawError(string message)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.4f, 0.4f, 1f));
        ImGui.TextWrapped(message);
        ImGui.PopStyleColor();
    }

    private void SetTileSize(float value)
    {
        var sanitized = Config.SanitizeEmojiTileSize(value);
        if (Math.Abs(sanitized - _tileSize) < 0.01f)
        {
            return;
        }

        _tileSize = sanitized;
        if (Math.Abs(_config.EmojiTileSize - sanitized) >= 0.01f)
        {
            _config.EmojiTileSize = sanitized;
            PersistSettings();
        }
    }

    private void SetGridHeight(float value)
    {
        var sanitized = Config.SanitizeEmojiGridHeight(value);
        if (Math.Abs(sanitized - _gridHeight) < 0.01f)
        {
            return;
        }

        _gridHeight = sanitized;
        if (Math.Abs(_config.EmojiGridHeight - sanitized) >= 0.01f)
        {
            _config.EmojiGridHeight = sanitized;
            PersistSettings();
        }
    }

    private void PersistSettings()
    {
        _persistSettings?.Invoke();
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;

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
    private readonly ConcurrentDictionary<string, bool> _unicodeTextureRequests = new();
    private readonly ConcurrentDictionary<string, DateTime> _customTextureFailures = new();
    private readonly ConcurrentDictionary<string, DateTime> _unicodeTextureFailures = new();
    private static readonly TimeSpan TextureFailureRetryDelay = TimeSpan.FromMinutes(2);
    private const int GridVirtualizationOverscanRows = 5;

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

        UiTheme.DrawSectionSeparator();

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
        UiTheme.DrawSectionSeparator();

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
        var grid = BeginGridVirtualization(filtered.Count, columns);
        string? selected = null;

        if (grid.HasRows)
        {
            for (var row = grid.FirstRow; row < grid.LastRow && string.IsNullOrEmpty(selected); row++)
            {
                var rowStartIndex = row * columns;
                var rowPos = new Vector2(grid.StartCursor.X, grid.StartCursor.Y + row * grid.RowHeight);
                ImGui.SetCursorPos(rowPos);

                for (var column = 0; column < columns; column++)
                {
                    var index = rowStartIndex + column;
                    if (index >= filtered.Count)
                    {
                        break;
                    }

                    if (column > 0)
                    {
                        ImGui.SameLine();
                    }

                    var emoji = filtered[index];
                    var texture = AcquireTexture(emoji.ImageUrl, _unicodeTextureRequests, _unicodeTextureFailures);
                    var clicked = false;

                    ImGui.PushID(index);
                    if (texture != null)
                    {
                        var wrap = texture.GetWrapOrEmpty();
                        if (ImGui.ImageButton(wrap.Handle, new Vector2(_tileSize, _tileSize)))
                        {
                            clicked = true;
                        }
                    }
                    else
                    {
                        clicked = EmojiPlaceholderRenderer.DrawButton(new Vector2(_tileSize, _tileSize));
                    }

                    if (!string.IsNullOrEmpty(emoji.Name) && ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(emoji.Name);
                    }
                    ImGui.PopID();

                    if (clicked)
                    {
                        selected = EmojiFormatter.CreateUnicodeToken(emoji);
                        break;
                    }
                }
            }
        }

        FinalizeGridVirtualization(grid);
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
        UiTheme.DrawSectionSeparator();

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
        var grid = BeginGridVirtualization(items.Count, columns);
        string? selected = null;

        if (grid.HasRows)
        {
            for (var row = grid.FirstRow; row < grid.LastRow && string.IsNullOrEmpty(selected); row++)
            {
                var rowStartIndex = row * columns;
                var rowPos = new Vector2(grid.StartCursor.X, grid.StartCursor.Y + row * grid.RowHeight);
                ImGui.SetCursorPos(rowPos);

                for (var column = 0; column < columns; column++)
                {
                    var index = rowStartIndex + column;
                    if (index >= items.Count)
                    {
                        break;
                    }

                    if (column > 0)
                    {
                        ImGui.SameLine();
                    }

                    var emoji = items[index];
                    var tooltip = emoji.Animated ? $":{emoji.Name}: (gif)" : $":{emoji.Name}:";
                    var texture = AcquireTexture(emoji.ImageUrl, _customTextureRequests, _customTextureFailures);
                    var clicked = false;

                    ImGui.PushID(emoji.Id);
                    if (texture != null)
                    {
                        var wrap = texture.GetWrapOrEmpty();
                        if (ImGui.ImageButton(wrap.Handle, new Vector2(_tileSize, _tileSize)))
                        {
                            clicked = true;
                        }
                    }
                    else
                    {
                        clicked = EmojiPlaceholderRenderer.DrawButton(new Vector2(_tileSize, _tileSize));
                    }

                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(tooltip);
                    }
                    ImGui.PopID();

                    if (clicked)
                    {
                        selected = EmojiFormatter.CreateCustomToken(emoji);
                        break;
                    }
                }
            }
        }

        FinalizeGridVirtualization(grid);
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

    private GridVirtualizationInfo BeginGridVirtualization(int itemCount, int columns)
    {
        var startCursor = ImGui.GetCursorPos();
        var totalRows = Math.Max(0, (itemCount + columns - 1) / columns);
        if (totalRows <= 0)
        {
            return new GridVirtualizationInfo(startCursor, 0, 0, 0, 0f);
        }

        var style = ImGui.GetStyle();
        var rowHeight = Math.Max(1f, _tileSize + style.ItemSpacing.Y);
        var scrollY = ImGui.GetScrollY();
        var windowHeight = ImGui.GetWindowHeight();
        if (!float.IsFinite(windowHeight) || windowHeight <= 0f)
        {
            windowHeight = rowHeight;
        }

        var firstRow = Math.Max(0, (int)Math.Floor(scrollY / rowHeight) - GridVirtualizationOverscanRows);
        var lastRow = Math.Min(totalRows, (int)Math.Ceiling((scrollY + windowHeight) / rowHeight) + GridVirtualizationOverscanRows);

        if (firstRow >= totalRows)
        {
            firstRow = Math.Max(0, totalRows - 1);
        }

        if (lastRow <= firstRow)
        {
            lastRow = Math.Min(totalRows, firstRow + GridVirtualizationOverscanRows * 2 + 1);
        }

        return new GridVirtualizationInfo(startCursor, firstRow, lastRow, totalRows, rowHeight);
    }

    private static void FinalizeGridVirtualization(in GridVirtualizationInfo info)
    {
        if (!info.HasRows)
        {
            return;
        }

        var nextRowY = info.StartCursor.Y + info.LastRow * info.RowHeight;
        ImGui.SetCursorPos(new Vector2(info.StartCursor.X, nextRowY));

        var remainingRows = info.TotalRows - info.LastRow;
        if (remainingRows > 0)
        {
            ImGui.Dummy(new Vector2(0f, remainingRows * info.RowHeight));
        }
    }

    private static ISharedImmediateTexture? AcquireTexture(
        string? imageUrl,
        ConcurrentDictionary<string, bool> requests,
        ConcurrentDictionary<string, DateTime> failures)
    {
        if (string.IsNullOrEmpty(imageUrl))
        {
            return null;
        }

        if (WebTextureCache.TryGetTexture(imageUrl, out var texture) && texture != null)
        {
            return texture;
        }

        if (failures.TryGetValue(imageUrl, out var lastFailure))
        {
            if (DateTime.UtcNow - lastFailure < TextureFailureRetryDelay)
            {
                return null;
            }

            failures.TryRemove(imageUrl, out _);
        }

        if (requests.TryGetValue(imageUrl, out var completed) && completed)
        {
            requests.TryRemove(imageUrl, out _);
        }

        if (requests.TryAdd(imageUrl, false))
        {
            WebTextureCache.Get(imageUrl, tex =>
            {
                if (tex != null)
                {
                    requests[imageUrl] = true;
                    failures.TryRemove(imageUrl, out _);
                }
                else
                {
                    failures[imageUrl] = DateTime.UtcNow;
                    requests.TryRemove(imageUrl, out _);
                }
            });
        }

        return null;
    }

    private readonly struct GridVirtualizationInfo
    {
        public GridVirtualizationInfo(Vector2 startCursor, int firstRow, int lastRow, int totalRows, float rowHeight)
        {
            StartCursor = startCursor;
            FirstRow = firstRow;
            LastRow = lastRow;
            TotalRows = totalRows;
            RowHeight = rowHeight;
        }

        public Vector2 StartCursor { get; }
        public int FirstRow { get; }
        public int LastRow { get; }
        public int TotalRows { get; }
        public float RowHeight { get; }
        public bool HasRows => TotalRows > 0 && RowHeight > 0f;
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;

namespace DemiCatPlugin;

public sealed class NotePadWindow : IDisposable
{
    private const int MaxTitleLength = 25;
    private static readonly ImGuiMouseCursor ResizeEwCursor = ResolveResizeEwCursor();

    private readonly Config _config;
    private readonly NotePadService _service;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly List<string> _sectionOrder = new();
    private readonly Dictionary<string, string> _sectionRenameBuffers = new();
    private readonly Dictionary<string, string> _pageRenameBuffers = new();
    private readonly TimeSpan _autosaveDelay = TimeSpan.FromMinutes(1);

    private string? _selectedSectionId;
    private string? _selectedPageId;
    private string _editorContent = string.Empty;
    private int _editorVersion;
    private int _selectionStart;
    private int _selectionEnd;
    private bool _dirty;
    private DateTime _lastEditUtc;
    private bool _showConflictModal;
    private string _conflictMessage = string.Empty;
    private NotePadPage? _conflictServerPage;
    private string? _conflictSectionId;
    private string? _conflictPageId;
    private int? _conflictServerVersion;
    private bool _pendingReload;
    private bool _sectionOrderDirty;
    private bool _pageOrderDirty;
    private bool _focusEditorNextFrame;
    private string _newSectionName = string.Empty;
    private bool _showNewSectionPopup;
    private string? _draggingSectionId;
    private string? _draggingPageId;

    public bool IsReadOnly { get; set; }

    public NotePadWindow(Config config, NotePadService service)
    {
        _config = config;
        _service = service;
        _service.Changed += HandleServiceChanged;
        _selectedSectionId = string.IsNullOrEmpty(config.NotePadLastSectionId) ? null : config.NotePadLastSectionId;
        _selectedPageId = string.IsNullOrEmpty(config.NotePadLastPageId) ? null : config.NotePadLastPageId;
    }

    public void Dispose()
    {
        _service.Changed -= HandleServiceChanged;
        _saveLock.Dispose();
    }

    public void Draw()
    {
        HandleKeyboardShortcuts();
        HandleAutosave();

        var sections = _service.Sections;
        EnsureSectionSelection(sections);
        var selectedSection = sections.FirstOrDefault(s => string.Equals(s.Id, _selectedSectionId, StringComparison.Ordinal));
        EnsurePageSelection(selectedSection);
        var selectedPage = selectedSection?.Pages.FirstOrDefault(p => string.Equals(p.Id, _selectedPageId, StringComparison.Ordinal));

        DrawConflictModal(selectedPage);

        DrawSectionTabs(sections);
        UiTheme.DrawSectionSeparator();

        var region = ImGui.GetContentRegionAvail();
        if (region.X <= 0f || region.Y <= 0f)
        {
            return;
        }

        var splitterWidth = 6f;
        var ratio = Math.Clamp(_config.NotePadPageListWidthRatio, 0.15f, 0.6f);
        var pageListWidth = Math.Clamp(region.X * ratio, 160f, Math.Max(160f, region.X - 200f));
        var editorWidth = Math.Max(200f, region.X - pageListWidth - splitterWidth);

        ImGui.BeginChild("NotePadPageList", new Vector2(pageListWidth, region.Y), true);
        DrawPageList(selectedSection, selectedPage);
        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.InvisibleButton("NotePadSplitter", new Vector2(splitterWidth, region.Y));
        if (ImGui.IsItemActive())
        {
            var delta = ImGui.GetIO().MouseDelta.X;
            if (Math.Abs(delta) > float.Epsilon)
            {
                var newRatio = Math.Clamp(pageListWidth + delta, 160f, Math.Max(160f, region.X - 200f)) / region.X;
                newRatio = Math.Clamp(newRatio, 0.15f, 0.6f);
                if (Math.Abs(newRatio - _config.NotePadPageListWidthRatio) > 0.0001f)
                {
                    _config.NotePadPageListWidthRatio = newRatio;
                    SaveConfig();
                }
            }
        }
        else if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ResizeEwCursor);
        }

        ImGui.SameLine();
        ImGui.BeginChild("NotePadEditor", new Vector2(editorWidth, region.Y), false);
        DrawEditor(selectedSection, selectedPage);
        ImGui.EndChild();

        if (_sectionOrderDirty)
        {
            _sectionOrderDirty = false;
            _ = Task.Run(() => _service.ReorderSectionsAsync(_sectionOrder, CancellationToken.None));
        }

        if (_pageOrderDirty && selectedSection != null)
        {
            _pageOrderDirty = false;
            var order = selectedSection.Pages.Select(p => p.Id).ToList();
            _ = Task.Run(() => _service.ReorderPagesAsync(selectedSection.Id, order, CancellationToken.None));
        }
    }

    private void DrawSectionTabs(IReadOnlyList<NotePadSection> sections)
    {
        var style = ImGui.GetStyle();
        var availableWidth = ImGui.GetContentRegionAvail().X;
        if (availableWidth <= 0f)
        {
            availableWidth = float.MaxValue;
        }

        var rowHeight = ImGui.GetFrameHeight() + style.FramePadding.Y;
        var spacing = style.ItemSpacing.X;
        var rowWidth = 0f;

        ImGui.BeginGroup();

        foreach (var section in sections)
        {
            var title = string.IsNullOrWhiteSpace(section.Name) ? "Untitled" : section.Name;
            var metrics = CalculateSectionTabMetrics(title);
            var buttonWidth = metrics.Width;
            if (rowWidth > 0f && rowWidth + buttonWidth > availableWidth)
            {
                ImGui.NewLine();
                rowWidth = 0f;
            }

            if (rowWidth > 0f)
            {
                ImGui.SameLine(0f, spacing);
            }

            ImGui.PushID(section.Id);
            var isSelected = string.Equals(section.Id, _selectedSectionId, StringComparison.Ordinal);
            var buttonSize = new Vector2(buttonWidth, rowHeight);
            DrawSectionTabButton(section, title, buttonSize, isSelected, metrics);

            if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && !isSelected)
            {
                SelectSection(section.Id);
            }

            if (!section.IsBuiltIn && ImGui.BeginDragDropSource())
            {
                _draggingSectionId = section.Id;
                ImGui.SetDragDropPayload("NotePadSection", ReadOnlySpan<byte>.Empty);
                ImGui.TextUnformatted(title);
                ImGui.EndDragDropSource();
            }

            if (!section.IsBuiltIn && ImGui.BeginDragDropTarget())
            {
                var payload = ImGui.AcceptDragDropPayload("NotePadSection");
                if (!payload.Equals(default(ImGuiPayloadPtr)) && !string.IsNullOrEmpty(_draggingSectionId) &&
                    !string.Equals(_draggingSectionId, section.Id, StringComparison.Ordinal))
                {
                    ReorderSections(_draggingSectionId, section.Id);
                    _draggingSectionId = null;
                }
                ImGui.EndDragDropTarget();
            }

            if (ImGui.BeginPopupContextItem($"SectionContext##{section.Id}"))
            {
                DrawSectionContext(section);
                ImGui.EndPopup();
            }

            ImGui.PopID();

            rowWidth += buttonWidth + spacing;
        }

        var plusWidth = CalculatePlusButtonWidth();
        if (rowWidth > 0f && rowWidth + plusWidth > availableWidth)
        {
            ImGui.NewLine();
            rowWidth = 0f;
        }

        if (rowWidth > 0f)
        {
            ImGui.SameLine(0f, spacing);
        }

        if (ImGui.Button("+", new Vector2(plusWidth, rowHeight)))
        {
            OpenNewSectionPopup();
        }

        ImGui.EndGroup();

        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            _draggingSectionId = null;
        }

        DrawNewSectionPopup();

        ImGui.Dummy(new Vector2(0f, style.ItemSpacing.Y));
    }

    private void DrawSectionContext(NotePadSection section)
    {
        if (section.IsBuiltIn)
        {
            if (ImGui.MenuItem("Copy Link"))
            {
                ImGui.SetClipboardText(section.Name);
            }
            return;
        }

        if (!IsReadOnly && ImGui.MenuItem("Rename"))
        {
            _sectionRenameBuffers[section.Id] = section.Name;
            ImGui.OpenPopup($"RenameSection##{section.Id}");
        }

        if (!IsReadOnly && ImGui.BeginPopup($"RenameSection##{section.Id}"))
        {
            var buffer = _sectionRenameBuffers.GetValueOrDefault(section.Id, section.Name) ?? string.Empty;
            var submitted = ImGui.InputText("##SectionRename", ref buffer, 128, ImGuiInputTextFlags.EnterReturnsTrue);
            buffer = ClampTitleLength(buffer);
            if (submitted)
            {
                _ = Task.Run(() => RenameSectionAsync(section.Id, buffer));
                ImGui.CloseCurrentPopup();
            }
            _sectionRenameBuffers[section.Id] = buffer;

            if (ImGui.Button("Save"))
            {
                _ = Task.Run(() => RenameSectionAsync(section.Id, buffer));
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        var canDeleteSection = OfficerPermissions.HasAccess(_config);
        if (!IsReadOnly)
        {
            if (!canDeleteSection)
            {
                ImGui.BeginDisabled();
            }

            if (ImGui.MenuItem("Delete"))
            {
                _ = Task.Run(() => _service.DeleteSectionAsync(section.Id, CancellationToken.None));
            }

            if (!canDeleteSection)
            {
                ImGui.EndDisabled();
            }
        }

        if (ImGui.MenuItem("Copy Link"))
        {
            ImGui.SetClipboardText(section.Name);
        }
    }

    private void DrawPageList(NotePadSection? section, NotePadPage? selectedPage)
    {
        if (section == null)
        {
            if (ImGui.Button("Create Section"))
            {
                OpenNewSectionPopup();
            }
            return;
        }

        var style = ImGui.GetStyle();
        var drawList = ImGui.GetWindowDrawList();
        var headerPos = ImGui.GetCursorScreenPos();
        var headerWidth = ImGui.GetContentRegionAvail().X;
        var headerHeight = ImGui.GetTextLineHeightWithSpacing() + style.FramePadding.Y * 2f;
        var headerRectMax = headerPos + new Vector2(headerWidth, headerHeight);

        var headerColor = style.Colors[(int)ImGuiCol.FrameBg];
        headerColor.W = MathF.Min(headerColor.W + 0.15f, 1f);
        drawList.AddRectFilled(headerPos, headerRectMax, ImGui.ColorConvertFloat4ToU32(headerColor), style.FrameRounding);
        drawList.AddRect(headerPos, headerRectMax, ImGui.ColorConvertFloat4ToU32(style.Colors[(int)ImGuiCol.Border]), style.FrameRounding);

        var title = string.IsNullOrWhiteSpace(section.Name) ? "Notes" : section.Name;
        var titlePos = headerPos + new Vector2(style.FramePadding.X, style.FramePadding.Y);
        ImGui.SetCursorScreenPos(titlePos);
        ImGui.TextUnformatted(title);

        var buttonText = "+";
        var buttonTextSize = ImGui.CalcTextSize(buttonText);
        var buttonPadding = style.FramePadding;
        var buttonSize = new Vector2(buttonTextSize.X + buttonPadding.X * 2f, buttonTextSize.Y + buttonPadding.Y * 2f);
        var buttonPos = new Vector2(
            headerRectMax.X - buttonSize.X - style.FramePadding.X,
            headerPos.Y + (headerHeight - buttonSize.Y) * 0.5f);
        ImGui.SetCursorScreenPos(buttonPos);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, style.FrameRounding);
        if (IsReadOnly || isBuiltIn)
        {
            ImGui.BeginDisabled();
            ImGui.Button(buttonText, buttonSize);
            ImGui.EndDisabled();
        }
        else if (ImGui.Button(buttonText, buttonSize))
        {
            CreateNewPage(section);
        }
        ImGui.PopStyleVar();

        ImGui.SetCursorScreenPos(new Vector2(headerPos.X, headerRectMax.Y + style.ItemSpacing.Y));
        UiTheme.DrawSectionSeparator();

        if (section.Pages.Count == 0)
        {
            ImGui.TextDisabled("No notes yet.");
        }
        else
        {
            foreach (var page in section.Pages)
            {
                DrawNoteListEntry(section, page, selectedPage);
            }
        }

        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            _draggingPageId = null;
        }
    }

    private void DrawNoteListEntry(NotePadSection section, NotePadPage page, NotePadPage? selectedPage)
    {
        var style = ImGui.GetStyle();
        var available = ImGui.GetContentRegionAvail();
        var lineHeight = ImGui.GetTextLineHeight();
        var entryHeight = lineHeight * 2f + style.FramePadding.Y * 3f;
        var itemSize = new Vector2(available.X, entryHeight);

        ImGui.PushID(page.Id);
        var flags = ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight;
        ImGui.InvisibleButton("##NoteRow", itemSize, flags);

        var hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        var clickedLeft = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        var itemMin = ImGui.GetItemRectMin();
        var itemMax = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        var isSelected = selectedPage != null && string.Equals(page.Id, selectedPage.Id, StringComparison.Ordinal);
        var background = GetNoteRowColor(isSelected, hovered);
        drawList.AddRectFilled(itemMin, itemMax, background, 8f);
        if (isSelected)
        {
            var borderColor = ImGui.ColorConvertFloat4ToU32(style.Colors[(int)ImGuiCol.Border]);
            drawList.AddRect(itemMin, itemMax, borderColor, 8f, ImDrawFlags.RoundCornersAll, 1.5f);
        }

        var padding = new Vector2(style.FramePadding.X + 6f, style.FramePadding.Y + 3f);
        var titlePos = itemMin + padding;
        var title = string.IsNullOrWhiteSpace(page.Title) ? "Untitled" : page.Title;
        drawList.AddText(titlePos, ImGui.GetColorU32(ImGuiCol.Text), title);

        var timestamp = FormatTimestamp(page.UpdatedAt);
        if (!string.IsNullOrEmpty(timestamp))
        {
            var timeSize = ImGui.CalcTextSize(timestamp);
            var timePos = new Vector2(itemMax.X - padding.X - timeSize.X, titlePos.Y);
            drawList.AddText(timePos, ImGui.GetColorU32(ImGuiCol.TextDisabled), timestamp);
        }

        var preview = BuildPreviewText(page);
        if (!string.IsNullOrEmpty(preview))
        {
            var previewPos = titlePos + new Vector2(0f, lineHeight + style.ItemSpacing.Y * 0.5f);
            drawList.AddText(previewPos, ImGui.GetColorU32(ImGuiCol.TextDisabled), preview);
        }

        ImGui.SetCursorScreenPos(new Vector2(itemMin.X, itemMax.Y + style.ItemSpacing.Y));

        if (clickedLeft)
        {
            SelectPage(section.Id, page.Id);
        }

        if (ImGui.BeginDragDropSource())
        {
            _draggingPageId = page.Id;
            ImGui.SetDragDropPayload("NotePadPage", ReadOnlySpan<byte>.Empty);
            ImGui.TextUnformatted(title);
            ImGui.EndDragDropSource();
        }

        if (ImGui.BeginDragDropTarget())
        {
            var payload = ImGui.AcceptDragDropPayload("NotePadPage");
            if (!payload.Equals(default(ImGuiPayloadPtr)) && !string.IsNullOrEmpty(_draggingPageId) &&
                !string.Equals(_draggingPageId, page.Id, StringComparison.Ordinal))
            {
                ReorderPages(section, _draggingPageId, page.Id);
                _draggingPageId = null;
            }
            ImGui.EndDragDropTarget();
        }

        if (ImGui.BeginPopupContextItem($"PageContext##{page.Id}"))
        {
            DrawPageContext(section, page);
            ImGui.EndPopup();
        }

        ImGui.PopID();
    }

    private static uint GetNoteRowColor(bool isSelected, bool hovered)
    {
        var style = ImGui.GetStyle();
        Vector4 color;
        if (isSelected)
        {
            color = style.Colors[(int)ImGuiCol.HeaderActive];
        }
        else if (hovered)
        {
            color = style.Colors[(int)ImGuiCol.HeaderHovered];
        }
        else
        {
            color = style.Colors[(int)ImGuiCol.FrameBg];
            color.W = MathF.Min(color.W + 0.08f, 1f);
        }

        return ImGui.ColorConvertFloat4ToU32(color);
    }

    private static string BuildPreviewText(NotePadPage page)
    {
        var content = page.Content ?? string.Empty;
        var normalized = content.ReplaceLineEndings(" ").Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return "No additional text";
        }

        const int maxLength = 160;
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        var truncated = normalized[..maxLength].TrimEnd();
        return truncated + "…";
    }

    private static DateTime ToLocalTimeSafe(DateTime value)
    {
        if (value == default)
        {
            return value;
        }

        if (value.Kind == DateTimeKind.Unspecified)
        {
            value = DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        return value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value;
    }

    private static string FormatTimestamp(DateTime value)
    {
        if (value == default)
        {
            return string.Empty;
        }

        var local = ToLocalTimeSafe(value);
        var now = DateTime.Now;
        if (local.Date == now.Date)
        {
            return local.ToString("h:mm tt", CultureInfo.CurrentCulture);
        }

        if (local.Year == now.Year)
        {
            return local.ToString("MMM d", CultureInfo.CurrentCulture);
        }

        return local.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
    }

    private static string FormatFullTimestamp(DateTime value)
    {
        if (value == default)
        {
            return string.Empty;
        }

        var local = ToLocalTimeSafe(value);
        return local.ToString("MMMM d, yyyy h:mm tt", CultureInfo.CurrentCulture);
    }

    private static string GenerateNewNoteTitle(NotePadSection section)
    {
        const string baseTitle = "New Note";
        if (!section.Pages.Any(p => string.Equals(p.Title, baseTitle, StringComparison.OrdinalIgnoreCase)))
        {
            return baseTitle;
        }

        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{baseTitle} {i}";
            if (!section.Pages.Any(p => string.Equals(p.Title, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        return $"{baseTitle} {DateTime.UtcNow.Ticks}";
    }

    private void CreateNewPage(NotePadSection section)
    {
        if (section.IsBuiltIn)
        {
            PluginServices.Instance?.ToastGui.ShowError("The About NotePad tab is read-only.");
            return;
        }

        if (IsReadOnly)
        {
            PluginServices.Instance?.ToastGui.ShowError("You do not have permission to create notes.");
            return;
        }

        _ = Task.Run(async () =>
        {
            var title = GenerateNewNoteTitle(section);
            var page = await _service.CreatePageAsync(section.Id, title, CancellationToken.None).ConfigureAwait(false);
            if (page != null)
            {
                SelectPage(section.Id, page.Id);
            }
        });
    }

    private bool CanDeletePage(NotePadSection section, NotePadPage page)
    {
        if (section.IsBuiltIn)
        {
            return false;
        }

        if (OfficerPermissions.HasAccess(_config))
        {
            return true;
        }

        var current = MembershipCache.DiscordUserId;
        if (string.IsNullOrEmpty(current))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(page.CreatedByDiscordId))
        {
            return string.Equals(page.CreatedByDiscordId, current, StringComparison.Ordinal);
        }

        return false;
    }

    private void DrawPageContext(NotePadSection section, NotePadPage page)
    {
        var creator = string.IsNullOrWhiteSpace(page.CreatedByDisplayName)
            ? (!string.IsNullOrEmpty(page.CreatedByDiscordId) ? page.CreatedByDiscordId : "Unknown")
            : page.CreatedByDisplayName;
        ImGui.MenuItem($"Created by {creator}", string.Empty, false, false);

        var createdAt = FormatFullTimestamp(page.CreatedAt);
        if (!string.IsNullOrEmpty(createdAt))
        {
            ImGui.MenuItem($"Created {createdAt}", string.Empty, false, false);
        }

        var updatedAt = FormatFullTimestamp(page.UpdatedAt);
        if (!string.IsNullOrEmpty(updatedAt))
        {
            ImGui.MenuItem($"Updated {updatedAt}", string.Empty, false, false);
        }

        ImGui.Separator();

        var allowPageEdits = !IsReadOnly && !section.IsBuiltIn;

        if (allowPageEdits && ImGui.MenuItem("Rename"))
        {
            _pageRenameBuffers[page.Id] = page.Title;
            ImGui.OpenPopup($"RenamePage##{page.Id}");
        }

        if (allowPageEdits && ImGui.BeginPopup($"RenamePage##{page.Id}"))
        {
            var buffer = _pageRenameBuffers.GetValueOrDefault(page.Id, page.Title) ?? string.Empty;
            var submitted = ImGui.InputText("##PageRename", ref buffer, 128, ImGuiInputTextFlags.EnterReturnsTrue);
            buffer = ClampTitleLength(buffer);
            if (submitted)
            {
                _ = Task.Run(() => RenamePageAsync(section.Id, page.Id, buffer));
                ImGui.CloseCurrentPopup();
            }
            _pageRenameBuffers[page.Id] = buffer;

            if (ImGui.Button("Save"))
            {
                _ = Task.Run(() => RenamePageAsync(section.Id, page.Id, buffer));
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        if (!IsReadOnly && !section.IsBuiltIn)
        {
            var canDelete = CanDeletePage(section, page);
            if (!canDelete)
            {
                ImGui.BeginDisabled();
            }

            if (ImGui.MenuItem("Delete Note"))
            {
                _ = Task.Run(() => _service.DeletePageAsync(section.Id, page.Id, CancellationToken.None));
            }

            if (!canDelete)
            {
                ImGui.EndDisabled();
            }
        }

        if (ImGui.MenuItem("Copy Link"))
        {
            ImGui.SetClipboardText(page.Title);
        }
    }

    private void DrawEditor(NotePadSection? section, NotePadPage? page)
    {
        if (section == null || page == null)
        {
            ImGui.TextWrapped("Select a section and page to begin editing.");
            return;
        }

        if (_pendingReload)
        {
            LoadPageContent(page);
            _pendingReload = false;
        }

        if (section.IsBuiltIn)
        {
            var content = page.Content ?? string.Empty;
            ImGui.BeginChild("NotePadAbout", ImGui.GetContentRegionAvail(), true);
            ImGui.PushTextWrapPos();
            if (string.IsNullOrEmpty(content))
            {
                ImGui.TextDisabled("No content available.");
            }
            else
            {
                ImGui.TextUnformatted(content);
            }
            ImGui.PopTextWrapPos();
            ImGui.EndChild();
            _editorContent = content;
            _editorVersion = page.Version;
            return;
        }

        DrawFormattingToolbar();

        if (IsReadOnly)
        {
            var content = page.Content ?? string.Empty;
            var formatted = MarkdownFormatter.Format(content);
            ImGui.BeginChild("NotePadReadOnlyPreview", ImGui.GetContentRegionAvail(), true);
            ImGui.PushTextWrapPos();
            if (string.IsNullOrEmpty(formatted))
            {
                ImGui.TextDisabled("No content available.");
            }
            else
            {
                ImGui.TextUnformatted(formatted);
            }
            ImGui.PopTextWrapPos();
            ImGui.EndChild();
            _editorContent = content;
            _editorVersion = page.Version;
            return;
        }

        var buffer = ImGuiTextUtil.MakeUtf8Buffer(_editorContent, Math.Max(1024, _editorContent.Length + 512));
        if (_focusEditorNextFrame)
        {
            ImGui.SetKeyboardFocusHere();
            _focusEditorNextFrame = false;
        }

        var style = ImGui.GetStyle();
        var available = ImGui.GetContentRegionAvail();
        var previewHeight = 0f;
        if (available.Y > 220f)
        {
            var maxPreview = MathF.Max(120f, available.Y - 160f);
            var previewTarget = available.Y * 0.35f;
            previewHeight = MathF.Min(MathF.Max(previewTarget, 120f), maxPreview);
        }

        if (previewHeight > 0f)
        {
            ImGui.BeginChild("NotePadLivePreview", new Vector2(-1, previewHeight), true);
            ImGui.PushTextWrapPos();
            var formatted = MarkdownFormatter.Format(_editorContent);
            if (string.IsNullOrEmpty(formatted))
            {
                ImGui.TextDisabled("Start typing to see formatted output.");
            }
            else
            {
                ImGui.TextUnformatted(formatted);
            }
            ImGui.PopTextWrapPos();
            ImGui.EndChild();
            ImGui.Dummy(new Vector2(0f, style.ItemSpacing.Y * 0.5f));
        }

        var editorHeight = MathF.Max(150f, ImGui.GetContentRegionAvail().Y);
        ImGui.PushItemWidth(-1);
        var flags = ImGuiInputTextFlags.AllowTabInput | ImGuiInputTextFlags.CallbackAlways | ImGuiInputTextFlags.CallbackHistory;
        var edited = ImGui.InputTextMultiline("##NotePadEditor", buffer, new Vector2(-1, editorHeight), flags, new ImGui.ImGuiInputTextCallbackDelegate(OnEditorEdited));
        ImGui.PopItemWidth();

        var newValue = ImGuiTextUtil.ReadUtf8Buffer(buffer);
        if (!string.Equals(newValue, _editorContent, StringComparison.Ordinal))
        {
            _editorContent = newValue;
            _dirty = true;
            _lastEditUtc = DateTime.UtcNow;
        }

        if (edited && ImGui.IsKeyPressed(ImGuiKey.Enter) && ImGui.GetIO().KeyCtrl)
        {
            _ = SavePageAsync(section.Id, page.Id, force: true);
        }
    }

    private void DrawFormattingToolbar()
    {
        if (IsReadOnly)
        {
            return;
        }

        if (ImGui.SmallButton("B")) ApplyFormatting("**", "**");
        ImGui.SameLine();
        if (ImGui.SmallButton("I")) ApplyFormatting("*", "*");
        ImGui.SameLine();
        if (ImGui.SmallButton("U")) ApplyFormatting("__", "__");
        ImGui.SameLine();
        if (ImGui.SmallButton("Code")) ApplyFormatting("`", "`");
        ImGui.SameLine();
        if (ImGui.SmallButton("Quote")) ApplyFormatting("> ", string.Empty);
        ImGui.SameLine();
        if (ImGui.SmallButton("Link")) ApplyFormatting("[", "](url)");
        ImGui.Spacing();
    }

    private void DrawConflictModal(NotePadPage? selectedPage)
    {
        if (_showConflictModal)
        {
            ImGui.OpenPopup("NotePadConflict");
            _showConflictModal = false;
        }

        if (ImGui.BeginPopupModal("NotePadConflict", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped(_conflictMessage);
            UiTheme.DrawSectionSeparator();

            if (ImGui.Button("Reload"))
            {
                if (_conflictServerPage != null)
                {
                    LoadPageContent(_conflictServerPage);
                    _conflictServerVersion = null;
                }
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Overwrite"))
            {
                if (_conflictSectionId != null && _conflictPageId != null)
                {
                    _ = SavePageAsync(_conflictSectionId, _conflictPageId, force: true, overwrite: true);
                }
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        if (selectedPage != null && !_dirty && !_pendingReload && !string.Equals(selectedPage.Content, _editorContent, StringComparison.Ordinal))
        {
            LoadPageContent(selectedPage);
        }
    }

    private void DrawNewSectionPopup()
    {
        if (_showNewSectionPopup)
        {
            ImGui.OpenPopup("CreateSection");
            _showNewSectionPopup = false;
        }

        if (ImGui.BeginPopup("CreateSection"))
        {
            ImGui.InputText("Name", ref _newSectionName, 128);
            _newSectionName = ClampTitleLength(_newSectionName);
            if (ImGui.Button("Create"))
            {
                var name = _newSectionName.Trim();
                if (!string.IsNullOrEmpty(name))
                {
                    _ = Task.Run(async () =>
                    {
                        var section = await _service.CreateSectionAsync(name, NotePadColorHelper.DefaultHexColor, CancellationToken.None);
                        if (section != null)
                        {
                            SelectSection(section.Id);
                        }
                    });
                }
                _newSectionName = string.Empty;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                _newSectionName = string.Empty;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
    }

    private void HandleAutosave()
    {
        if (IsReadOnly || !_dirty)
        {
            return;
        }

        if (DateTime.UtcNow - _lastEditUtc < _autosaveDelay)
        {
            return;
        }

        if (_selectedSectionId == null || _selectedPageId == null)
        {
            return;
        }

        _ = SavePageAsync(_selectedSectionId, _selectedPageId, force: false);
    }

    private void HandleKeyboardShortcuts()
    {
        var io = ImGui.GetIO();
        if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
        {
            return;
        }

        if (!IsReadOnly && io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.N))
        {
            var section = _selectedSectionId;
            if (section != null)
            {
                var actualSection = _service.Sections.FirstOrDefault(s => string.Equals(s.Id, section, StringComparison.Ordinal));
                if (actualSection != null)
                {
                    CreateNewPage(actualSection);
                }
            }
            else
            {
                OpenNewSectionPopup();
            }
        }

        if (!IsReadOnly && io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.S))
        {
            if (_selectedSectionId != null && _selectedPageId != null)
            {
                _ = SavePageAsync(_selectedSectionId, _selectedPageId, force: true);
            }
        }
    }

    private void EnsureSectionSelection(IReadOnlyList<NotePadSection> sections)
    {
        if (_selectedSectionId != null && sections.Any(s => string.Equals(s.Id, _selectedSectionId, StringComparison.Ordinal)))
        {
            return;
        }

        _selectedSectionId = sections.FirstOrDefault()?.Id;
        _config.NotePadLastSectionId = _selectedSectionId;
        SaveConfig();
        _pendingReload = true;
    }

    private void EnsurePageSelection(NotePadSection? section)
    {
        if (section == null)
        {
            _selectedPageId = null;
            return;
        }

        if (_selectedPageId != null && section.Pages.Any(p => string.Equals(p.Id, _selectedPageId, StringComparison.Ordinal)))
        {
            return;
        }

        _selectedPageId = section.Pages.FirstOrDefault()?.Id;
        _config.NotePadLastPageId = _selectedPageId;
        SaveConfig();
        _pendingReload = true;
    }

    private void SelectSection(string sectionId)
    {
        if (string.Equals(_selectedSectionId, sectionId, StringComparison.Ordinal))
        {
            return;
        }

        _selectedSectionId = sectionId;
        _config.NotePadLastSectionId = sectionId;
        _config.NotePadLastPageId = null;
        SaveConfig();
        _selectedPageId = null;
        _pendingReload = true;
    }

    private void SelectPage(string sectionId, string pageId)
    {
        if (string.Equals(_selectedPageId, pageId, StringComparison.Ordinal))
        {
            return;
        }

        _selectedSectionId = sectionId;
        _selectedPageId = pageId;
        _config.NotePadLastSectionId = sectionId;
        _config.NotePadLastPageId = pageId;
        SaveConfig();
        _pendingReload = true;
    }

    private void LoadPageContent(NotePadPage page)
    {
        _editorContent = page.Content ?? string.Empty;
        _editorVersion = page.Version;
        _dirty = false;
        _focusEditorNextFrame = true;
    }

    private async Task SavePageAsync(string sectionId, string pageId, bool force, bool overwrite = false)
    {
        if (IsReadOnly)
        {
            return;
        }

        await _saveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_dirty && !force && !overwrite)
            {
                return;
            }

            var content = _editorContent;
            var version = _editorVersion;
            if (overwrite)
            {
                version = _conflictServerVersion ?? _editorVersion;
                if (_conflictServerVersion == null)
                {
                    var latest = _service.Sections
                        .SelectMany(s => s.Pages)
                        .FirstOrDefault(p => string.Equals(p.Id, pageId, StringComparison.Ordinal));
                    if (latest != null)
                    {
                        version = latest.Version;
                    }
                }
            }
            NotePadPage? result = null;
            try
            {
                result = await _service.SavePageAsync(sectionId, pageId, new NotePadPageUpdate
                {
                    Content = content,
                    Version = version
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (NotePadConflictException ex)
            {
                _conflictMessage = string.IsNullOrWhiteSpace(ex.Message)
                    ? "The page has been updated elsewhere. Reload or overwrite?"
                    : ex.Message;
                _conflictSectionId = sectionId;
                _conflictPageId = pageId;
                _conflictServerPage = _service.Sections
                    .SelectMany(s => s.Pages)
                    .FirstOrDefault(p => string.Equals(p.Id, pageId, StringComparison.Ordinal));
                _conflictServerVersion = _conflictServerPage?.Version;
                _showConflictModal = true;
                return;
            }

            if (result != null)
            {
                _editorVersion = result.Version;
                _dirty = false;
                _conflictServerVersion = null;
            }
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private async Task RenameSectionAsync(string sectionId, string name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        var clamped = ClampTitleLength(trimmed);
        if (string.IsNullOrEmpty(clamped))
        {
            PluginServices.Instance?.ToastGui.ShowError("Section name cannot be empty.");
            return;
        }

        if (!string.Equals(trimmed, clamped, StringComparison.Ordinal))
        {
            PluginServices.Instance?.ToastGui.ShowNormal($"Section name truncated to {MaxTitleLength} characters.");
        }

        var success = await _service.RenameSectionAsync(sectionId, clamped, CancellationToken.None).ConfigureAwait(false);
        if (success)
        {
            _config.NotePadLastSectionId = sectionId;
            SaveConfig();
        }
    }

    private async Task RenamePageAsync(string sectionId, string pageId, string title)
    {
        var trimmed = (title ?? string.Empty).Trim();
        var clamped = ClampTitleLength(trimmed);
        if (string.IsNullOrEmpty(clamped))
        {
            PluginServices.Instance?.ToastGui.ShowError("Page title cannot be empty.");
            return;
        }

        if (!string.Equals(trimmed, clamped, StringComparison.Ordinal))
        {
            PluginServices.Instance?.ToastGui.ShowNormal($"Page title truncated to {MaxTitleLength} characters.");
        }

        var success = await _service.RenamePageAsync(sectionId, pageId, clamped, CancellationToken.None).ConfigureAwait(false);
        if (success)
        {
            _config.NotePadLastPageId = pageId;
            SaveConfig();
        }
    }

    private static string ClampTitleLength(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= MaxTitleLength ? value : value[..MaxTitleLength];
    }

    private void ApplyFormatting(string prefix, string suffix)
    {
        if (IsReadOnly)
        {
            return;
        }

        var text = _editorContent;
        MarkdownSelectionHelper.WrapSelection(ref text, prefix, suffix, ref _selectionStart, ref _selectionEnd);
        _editorContent = text ?? string.Empty;
        _dirty = true;
        _lastEditUtc = DateTime.UtcNow;
    }

    private int OnEditorEdited(ref ImGuiInputTextCallbackData data)
    {
        _selectionStart = data.SelectionStart;
        _selectionEnd = data.SelectionEnd;
        return 0;
    }

    private void ReorderSections(string fromId, string toId)
    {
        var order = _service.Sections.Select(s => s.Id).ToList();
        var fromIndex = order.IndexOf(fromId);
        var toIndex = order.IndexOf(toId);
        if (fromIndex < 0 || toIndex < 0)
        {
            return;
        }

        var item = order[fromIndex];
        order.RemoveAt(fromIndex);
        order.Insert(toIndex, item);
        _sectionOrder.Clear();
        _sectionOrder.AddRange(order.Where(id => !NotePadService.IsBuiltInSectionId(id)));
        _sectionOrderDirty = true;
    }

    private void ReorderPages(NotePadSection section, string fromId, string toId)
    {
        var order = section.Pages.Select(p => p.Id).ToList();
        var fromIndex = order.IndexOf(fromId);
        var toIndex = order.IndexOf(toId);
        if (fromIndex < 0 || toIndex < 0)
        {
            return;
        }

        var item = order[fromIndex];
        order.RemoveAt(fromIndex);
        order.Insert(toIndex, item);
        section.Pages = section.Pages.OrderBy(p => order.IndexOf(p.Id)).ToList();
        _pageOrderDirty = true;
    }

    private void SaveConfig()
    {
        PluginServices.Instance?.PluginInterface.SavePluginConfig(_config);
    }

    private static Vector4 ParseColor(string? color)
    {
        if (!NotePadColorHelper.TryParseColorString(color, out var rgb))
        {
            rgb = NotePadColorHelper.DefaultColorValue;
        }

        var r = ((rgb >> 16) & 0xFF) / 255f;
        var g = ((rgb >> 8) & 0xFF) / 255f;
        var b = (rgb & 0xFF) / 255f;
        return new Vector4(r, g, b, 1f);
    }

    private void OpenNewSectionPopup()
    {
        if (IsReadOnly)
        {
            PluginServices.Instance?.ToastGui.ShowError("You do not have permission to create sections.");
            return;
        }

        _showNewSectionPopup = true;
        _newSectionName = string.Empty;
    }

    private void HandleServiceChanged()
    {
        var framework = PluginServices.Instance?.Framework;
        if (framework != null)
        {
            _ = framework.RunOnTick(ApplyServiceChange);
        }
        else
        {
            ApplyServiceChange();
        }
    }

    private void ApplyServiceChange()
    {
        var sections = _service.Sections;
        if (_selectedSectionId != null && !sections.Any(s => string.Equals(s.Id, _selectedSectionId, StringComparison.Ordinal)))
        {
            _selectedSectionId = null;
        }

        if (_selectedPageId != null)
        {
            var hasPage = sections.Any(s => s.Pages.Any(p => string.Equals(p.Id, _selectedPageId, StringComparison.Ordinal)));
            if (!hasPage)
            {
                _selectedPageId = null;
            }
        }

        _pendingReload = true;
        _sectionOrder.Clear();
        _sectionOrder.AddRange(sections.Select(s => s.Id).Where(id => !NotePadService.IsBuiltInSectionId(id)));
    }

    private static SectionTabMetrics CalculateSectionTabMetrics(string title)
    {
        var style = ImGui.GetStyle();
        var textSize = ImGui.CalcTextSize(title);
        var textLineHeight = ImGui.GetTextLineHeight();
        var padding = style.FramePadding.X + 6f;
        var chipGap = MathF.Min(style.FramePadding.X * 0.5f, 6f);
        var chipWidth = MathF.Max(0f, MathF.Min(textLineHeight, style.FramePadding.X - chipGap));
        var width = textSize.X + padding * 2f + chipWidth + chipGap;
        return new SectionTabMetrics(width, chipWidth, chipGap);
    }

    private void DrawSectionTabButton(NotePadSection section, string title, Vector2 size, bool selected, SectionTabMetrics metrics)
    {
        var drawList = ImGui.GetWindowDrawList();
        var style = ImGui.GetStyle();
        var min = ImGui.GetCursorScreenPos();
        var max = new Vector2(min.X + size.X, min.Y + size.Y);

        ImGui.InvisibleButton("##SectionTab", size);
        var hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        var active = ImGui.IsItemActive();

        var baseColor = ParseColor(section.Color);
        Vector4 background;
        if (selected)
        {
            background = AdjustTabColor(baseColor, active ? 0.95f : (hovered ? 1.1f : 1f));
        }
        else
        {
            background = AdjustTabColor(baseColor, hovered ? 0.85f : 0.7f);
        }

        drawList.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(background), style.FrameRounding);

        if (metrics.ChipWidth > 0f)
        {
            var chipHeight = MathF.Min(ImGui.GetTextLineHeight(), size.Y - style.FramePadding.Y * 2f);
            var chipMin = new Vector2(min.X + style.FramePadding.X, min.Y + (size.Y - chipHeight) * 0.5f);
            var chipMax = new Vector2(chipMin.X + metrics.ChipWidth, chipMin.Y + chipHeight);
            drawList.AddRectFilled(chipMin, chipMax, ImGui.ColorConvertFloat4ToU32(baseColor), style.FrameRounding / 2f);
        }

        var textX = min.X + style.FramePadding.X + metrics.ChipWidth + metrics.ChipGap;
        var textY = min.Y + MathF.Max(style.FramePadding.Y * 0.5f, (size.Y - ImGui.GetTextLineHeight()) / 2f);
        drawList.AddText(new Vector2(textX, textY), ImGui.GetColorU32(ImGuiCol.Text), title);

        drawList.AddRect(min, max, ImGui.GetColorU32(ImGuiCol.Border), style.FrameRounding);
    }

    private static float CalculatePlusButtonWidth()
    {
        var style = ImGui.GetStyle();
        var text = ImGui.CalcTextSize("+");
        return text.X + style.FramePadding.X * 2f;
    }

    private static Vector4 AdjustTabColor(Vector4 color, float multiplier)
    {
        return new Vector4(
            Math.Clamp(color.X * multiplier, 0f, 1f),
            Math.Clamp(color.Y * multiplier, 0f, 1f),
            Math.Clamp(color.Z * multiplier, 0f, 1f),
            1f);
    }

    private static ImGuiMouseCursor ResolveResizeEwCursor()
    {
        if (Enum.TryParse("ResizeEW", ignoreCase: true, out ImGuiMouseCursor cursor))
        {
            return cursor;
        }

        return ImGuiMouseCursor.ResizeAll;
    }

    private readonly struct SectionTabMetrics
    {
        public SectionTabMetrics(float width, float chipWidth, float chipGap)
        {
            Width = width;
            ChipWidth = chipWidth;
            ChipGap = chipGap;
        }

        public float Width { get; }
        public float ChipWidth { get; }
        public float ChipGap { get; }
    }
}

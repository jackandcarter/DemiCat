using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using ImGuiNET;
using Dalamud.Plugin.Services;

namespace DemiCatPlugin;

public sealed class NotePadWindow : IDisposable
{
    private const int MaxTitleLength = 25;
    private static readonly ImGuiMouseCursor ResizeEwCursor = ResolveResizeEwCursor();
    private static readonly ImGuiTabBarFlags SectionTabBarFlags = ResolveSectionTabBarFlags();

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
    private bool _pendingReload;
    private bool _sectionOrderDirty;
    private bool _pageOrderDirty;
    private bool _focusEditorNextFrame;
    private static NotePadWindow? _activeEditorCallbackOwner;
    private static unsafe readonly ImGuiInputTextCallback _editorEditedCallback = OnEditorEdited;
    private string _newSectionName = string.Empty;
    private string _newPageTitle = string.Empty;
    private bool _showNewSectionPopup;
    private bool _showNewPagePopup;
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
        if (ImGui.GetCurrentContext() == IntPtr.Zero)
        {
            return;
        }

        try
        {
        HandleKeyboardShortcuts();
        HandleAutosave();

        var sections = _service.Sections;
        EnsureSectionSelection(sections);

        DrawSectionTabs(sections);

        var selectedSection = sections.FirstOrDefault(s => string.Equals(s.Id, _selectedSectionId, StringComparison.Ordinal));
        EnsurePageSelection(selectedSection);
        var selectedPage = selectedSection?.Pages.FirstOrDefault(p => string.Equals(p.Id, _selectedPageId, StringComparison.Ordinal));

        DrawConflictModal(selectedPage);
        ImGui.Separator();

        var region = ImGui.GetContentRegionAvail();
        if (region.X <= 0f || region.Y <= 0f)
        {
            return;
        }

        var splitterWidth = 6f;
        var ratio = Math.Clamp(_config.NotePadPageListWidthRatio, 0.15f, 0.6f);
        var pageListWidth = Math.Clamp(region.X * ratio, 160f, Math.Max(160f, region.X - 200f));
        var editorWidth = Math.Max(200f, region.X - pageListWidth - splitterWidth);

        ImGui.BeginChild("NotePadPageList", new Vector2(pageListWidth, region.Y), true, ImGuiWindowFlags.None);
        try
        {
            DrawPageList(selectedSection, selectedPage);
        }
        finally
        {
            ImGui.EndChild();
        }

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
        ImGui.BeginChild("NotePadEditor", new Vector2(editorWidth, region.Y), false, ImGuiWindowFlags.None);
        try
        {
            DrawEditor(selectedSection, selectedPage);
        }
        finally
        {
            ImGui.EndChild();
        }

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
        catch (Exception ex)
        {
            try
            {
                PluginServices.Instance?.Log.Error(ex, "NotePadWindow.Draw()");
            }
            catch
            {
                // ignored
            }
        }
    }

    private void DrawSectionTabs(IReadOnlyList<NotePadSection> sections)
    {
        if (ImGui.BeginTabBar("NotePadSections", SectionTabBarFlags))
        {
            foreach (var section in sections)
            {
                ImGui.PushID(section.Id);
                var selected = string.Equals(section.Id, _selectedSectionId, StringComparison.Ordinal);
                var title = string.IsNullOrWhiteSpace(section.Name) ? "Untitled" : section.Name;
                var label = $"{title}##{section.Id}";
                var tabFlags = selected ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;

                var textSize = ImGui.CalcTextSize(title);
                var style = ImGui.GetStyle();
                var padding = style.FramePadding.X + 6f;
                var textLineHeight = ImGui.GetTextLineHeight();
                var chipGap = MathF.Min(style.FramePadding.X * 0.5f, 6f);
                var chipWidth = MathF.Max(0f, MathF.Min(textLineHeight, style.FramePadding.X - chipGap));
                var minWidth = textSize.X + padding * 2f + chipWidth + chipGap;

                var color = ParseColor(section.Color);
                var previousTabMinWidth = style.TabMinWidthForCloseButton;
                style.TabMinWidthForCloseButton = Math.Max(previousTabMinWidth, minWidth);
                ImGui.PushStyleColor(ImGuiCol.Tab, AdjustTabColor(color, 0.7f));
                ImGui.PushStyleColor(ImGuiCol.TabActive, AdjustTabColor(color, 1f));
                ImGui.PushStyleColor(ImGuiCol.TabHovered, AdjustTabColor(color, 1.2f));

                var tabVisible = true;
                var tabOpen = ImGui.BeginTabItem(label, ref tabVisible, tabFlags);
                var rectMin = ImGui.GetItemRectMin();
                var rectMax = ImGui.GetItemRectMax();
                var drawList = ImGui.GetWindowDrawList();
                var textStartX = rectMin.X + style.FramePadding.X;
                var chipRight = textStartX - chipGap;
                var chipLeft = chipRight - chipWidth;
                if (chipWidth > 0f && chipRight > rectMin.X)
                {
                    var chipMin = new Vector2(MathF.Max(chipLeft, rectMin.X + 1f), rectMin.Y + 4f);
                    var chipMax = new Vector2(chipRight, rectMax.Y - 4f);
                    drawList.AddRectFilled(chipMin, chipMax, ImGui.ColorConvertFloat4ToU32(color), 3f);
                }

                if (tabOpen)
                {
                    if (!selected)
                    {
                        SelectSection(section.Id);
                    }
                    ImGui.EndTabItem();
                }

                ImGui.PopStyleColor(3);
                style.TabMinWidthForCloseButton = previousTabMinWidth;

                if (!tabVisible && !IsReadOnly)
                {
                    _ = Task.Run(() => _service.DeleteSectionAsync(section.Id, CancellationToken.None));

                    if (string.Equals(_selectedSectionId, section.Id, StringComparison.Ordinal))
                    {
                        _selectedSectionId = null;
                    }
                }

                if (ImGui.BeginDragDropSource())
                {
                    _draggingSectionId = section.Id;
                    ImGui.SetDragDropPayload("NotePadSection", nint.Zero, 0, ImGuiCond.None);
                    ImGui.TextUnformatted(title);
                    ImGui.EndDragDropSource();
                }

                if (ImGui.BeginDragDropTarget())
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
            }

            if (ImGui.TabItemButton("+"))
            {
                OpenNewSectionPopup();
            }
            ImGui.EndTabBar();
        }

        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            _draggingSectionId = null;
        }

        DrawNewSectionPopup();
    }

    private void DrawSectionContext(NotePadSection section)
    {
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

        if (!IsReadOnly && ImGui.MenuItem("Delete"))
        {
            _ = Task.Run(() => _service.DeleteSectionAsync(section.Id, CancellationToken.None));
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

        DrawNewPagePopup(section);

        if (!IsReadOnly && ImGui.Button("New Page"))
        {
            OpenNewPagePopup(section);
        }

        ImGui.Separator();

        foreach (var page in section.Pages)
        {
            ImGui.PushID(page.Id);
            var label = string.IsNullOrWhiteSpace(page.Title) ? "Untitled Page" : page.Title;
            var isSelected = string.Equals(page.Id, _selectedPageId, StringComparison.Ordinal);
            if (ImGui.Selectable(label, isSelected))
            {
                SelectPage(section.Id, page.Id);
            }

            if (ImGui.BeginDragDropSource())
            {
                _draggingPageId = page.Id;
                ImGui.SetDragDropPayload("NotePadPage", nint.Zero, 0, ImGuiCond.None);
                ImGui.TextUnformatted(label);
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

            if (ImGui.BeginPopupContextItem("PageContext"))
            {
                DrawPageContext(section, page);
                ImGui.EndPopup();
            }

            ImGui.PopID();
        }

        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            _draggingPageId = null;
        }
    }

    private void DrawPageContext(NotePadSection section, NotePadPage page)
    {
        if (!IsReadOnly && ImGui.MenuItem("Rename"))
        {
            _pageRenameBuffers[page.Id] = page.Title;
            ImGui.OpenPopup($"RenamePage##{page.Id}");
        }

        if (!IsReadOnly && ImGui.BeginPopup($"RenamePage##{page.Id}"))
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

        if (!IsReadOnly && ImGui.MenuItem("Delete"))
        {
            _ = Task.Run(() => _service.DeletePageAsync(section.Id, page.Id, CancellationToken.None));
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

        DrawFormattingToolbar();

        if (IsReadOnly)
        {
            var content = page.Content ?? string.Empty;
            var capacity = (uint)Math.Max(1024, content.Length + 512);
            var readOnlyContent = content;
            ImGui.BeginDisabled();
            ImGui.InputTextMultiline(
                "##NotePadEditorReadOnly",
                ref readOnlyContent,
                capacity,
                new Vector2(-1, -1),
                ImGuiInputTextFlags.ReadOnly
            );
            ImGui.EndDisabled();
            _editorContent = content;
            _editorVersion = page.Version;
            return;
        }

        var editorCapacity = (uint)Math.Max(1024, _editorContent.Length + 512);
        if (_focusEditorNextFrame)
        {
            ImGui.SetKeyboardFocusHere();
            _focusEditorNextFrame = false;
        }

        ImGui.PushItemWidth(-1);
        var flags = ImGuiInputTextFlags.AllowTabInput | ImGuiInputTextFlags.CallbackAlways | ImGuiInputTextFlags.CallbackHistory;
        bool edited;
        _activeEditorCallbackOwner = this;
        try
        {
            edited = ImGui.InputTextMultiline(
                "##NotePadEditor",
                ref _editorContent,
                editorCapacity,
                new Vector2(-1, -1),
                flags,
                _editorEditedCallback,
                IntPtr.Zero
            );
        }
        finally
        {
            _activeEditorCallbackOwner = null;
        }
        ImGui.PopItemWidth();

        if (edited)
        {
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
            ImGui.Separator();

            if (ImGui.Button("Reload"))
            {
                if (_conflictServerPage != null)
                {
                    LoadPageContent(_conflictServerPage);
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

    private void DrawNewPagePopup(NotePadSection section)
    {
        if (_showNewPagePopup)
        {
            ImGui.OpenPopup("CreatePage");
            _showNewPagePopup = false;
        }

        if (ImGui.BeginPopup("CreatePage"))
        {
            ImGui.InputText("Title", ref _newPageTitle, 128);
            _newPageTitle = ClampTitleLength(_newPageTitle);
            if (ImGui.Button("Create"))
            {
                var title = _newPageTitle.Trim();
                if (!string.IsNullOrEmpty(title))
                {
                    _ = Task.Run(async () =>
                    {
                        var page = await _service.CreatePageAsync(section.Id, title, CancellationToken.None);
                        if (page != null)
                        {
                            SelectPage(section.Id, page.Id);
                        }
                    });
                }
                _newPageTitle = string.Empty;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                _newPageTitle = string.Empty;
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
                    OpenNewPagePopup(actualSection);
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
            var version = overwrite ? 0 : _editorVersion;
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
                _showConflictModal = true;
                return;
            }

            if (result != null)
            {
                _editorVersion = result.Version;
                _dirty = false;
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

    private static unsafe int OnEditorEdited(ImGuiInputTextCallbackData* data)
    {
        if (data == null)
        {
            return 0;
        }

        var owner = _activeEditorCallbackOwner;
        if (owner == null)
        {
            return 0;
        }

        owner._selectionStart = data->SelectionStart;
        owner._selectionEnd = data->SelectionEnd;
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
        _sectionOrder.AddRange(order);
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

    private void OpenNewPagePopup(NotePadSection section)
    {
        if (IsReadOnly)
        {
            PluginServices.Instance?.ToastGui.ShowError("You do not have permission to create pages.");
            return;
        }

        _showNewPagePopup = true;
        _newPageTitle = string.Empty;
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
        _sectionOrder.AddRange(sections.Select(s => s.Id));
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

    private static ImGuiTabBarFlags ResolveSectionTabBarFlags()
    {
        var flags = ImGuiTabBarFlags.Reorderable;
        if (Enum.TryParse("TabListPopupButton", ignoreCase: true, out ImGuiTabBarFlags extra))
        {
            flags |= extra;
        }

        return flags;
    }
}

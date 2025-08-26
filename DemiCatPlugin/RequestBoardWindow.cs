using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Lumina.Excel.GeneratedSheets;

namespace DemiCatPlugin;

public class RequestBoardWindow
{
    private readonly Config _config;
    private readonly List<Request> _requests = new();
    private readonly Dictionary<uint, ISharedImmediateTexture?> _iconCache = new();

    private string _filter = string.Empty;
    private bool _createOpen;
    private bool _editOpen;
    private int _editIndex = -1;
    private Request _working = new();

    private string _search = string.Empty;
    private readonly List<Suggestion> _suggestions = new();

    public RequestBoardWindow(Config config)
    {
        _config = config;
    }

    public void Draw()
    {
        ImGui.InputText("Filter", ref _filter, 64);
        ImGui.SameLine();
        if (ImGui.Button("New"))
        {
            _working = new Request();
            _createOpen = true;
        }

        foreach (var req in _requests.ToList())
        {
            if (!string.IsNullOrEmpty(_filter) && !req.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                continue;
            DrawRequest(req);
        }

        if (_createOpen)
            DrawEditor("Create Request", () => _requests.Add(_working));
        if (_editOpen)
            DrawEditor("Edit Request", () =>
            {
                if (_editIndex >= 0 && _editIndex < _requests.Count)
                    _requests[_editIndex] = _working;
            });
    }

    private void DrawRequest(Request req)
    {
        ImGui.PushID(req.Name);
        var icon = GetIcon(req.IconId);
        if (icon != null)
        {
            var wrap = icon.GetWrapOrEmpty();
            ImGui.Image(wrap.Handle, new Vector2(32, 32));
            ImGui.SameLine();
        }
        ImGui.TextUnformatted(req.Name);
        ImGui.SameLine();
        if (ImGui.Button("Edit"))
        {
            _working = req;
            _editIndex = _requests.IndexOf(req);
            _editOpen = true;
        }
        ImGui.SameLine();
        if (ImGui.Button("Delete"))
        {
            _requests.Remove(req);
            ImGui.PopID();
            return;
        }
        ImGui.PopID();
        ImGui.Separator();
    }

    private void DrawEditor(string title, Action onSave)
    {
        ImGui.OpenPopup(title);
        var open = _createOpen || _editOpen;
        if (ImGui.BeginPopupModal(title, ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (ImGui.InputText("Item/Duty", ref _search, 64))
            {
                UpdateSuggestions();
            }

            foreach (var s in _suggestions)
            {
                if (ImGui.Selectable(s.Name, _working.Name == s.Name))
                {
                    _working.Name = s.Name;
                    _working.IconId = s.IconId;
                    _search = s.Name;
                }
            }

            if (ImGui.Button("Save"))
            {
                onSave();
                _createOpen = _editOpen = false;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                _createOpen = _editOpen = false;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
        if (!open)
        {
            _createOpen = _editOpen = false;
        }
    }

    private void UpdateSuggestions()
    {
        _suggestions.Clear();
        if (string.IsNullOrWhiteSpace(_search) || _search.Length < 2) return;
        var dm = PluginServices.Instance?.DataManager;
        if (dm == null) return;

        var items = dm.GetExcelSheet<Item>();
        if (items != null)
        {
            foreach (var item in items)
            {
                var name = item.Name.ToString();
                if (!string.IsNullOrEmpty(name) && name.Contains(_search, StringComparison.OrdinalIgnoreCase))
                {
                    _suggestions.Add(new Suggestion { Name = name, IconId = (uint)item.Icon });
                    if (_suggestions.Count >= 10) break;
                }
            }
        }

        if (_suggestions.Count < 10)
        {
            var duties = dm.GetExcelSheet<ContentFinderCondition>();
            if (duties != null)
            {
                foreach (var duty in duties)
                {
                    var name = duty.Name.ToString();
                    if (!string.IsNullOrEmpty(name) && name.Contains(_search, StringComparison.OrdinalIgnoreCase))
                    {
                        _suggestions.Add(new Suggestion { Name = name, IconId = (uint)duty.Icon });
                        if (_suggestions.Count >= 10) break;
                    }
                }
            }
        }
    }

    private ISharedImmediateTexture? GetIcon(uint iconId)
    {
        if (iconId == 0) return null;
        if (!_iconCache.TryGetValue(iconId, out var tex))
        {
            tex = PluginServices.Instance!.TextureProvider.GetIcon(iconId);
            _iconCache[iconId] = tex;
        }
        return tex;
    }

    private struct Request
    {
        public string Name;
        public uint IconId;
    }

    private struct Suggestion
    {
        public string Name;
        public uint IconId;
    }
}


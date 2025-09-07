using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using System.Numerics;

namespace DemiCatPlugin;

public class RequestBoardWindow
{
    private readonly Config _config;
    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, bool> _conflicts = new();
    private readonly GameDataCache _gameData;
    private readonly HashSet<string> _itemLoads = new();
    private readonly HashSet<string> _dutyLoads = new();

    private string _newTitle = string.Empty;
    private string _newDescription = string.Empty;
    private RequestType _newType = RequestType.Item;
    private RequestUrgency _newUrgency = RequestUrgency.Low;
    private string _createStatus = string.Empty;

    private enum SortMode
    {
        Type,
        Name,
        MostRecent
    }

    private SortMode _sortMode = SortMode.MostRecent;
    private static readonly string[] SortLabels = { "Type", "Name", "Most Recent" };

    public RequestBoardWindow(Config config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
        _gameData = new GameDataCache(httpClient);
    }

    public void Draw()
    {
        if (!_config.Requests)
        {
            ImGui.TextUnformatted("Feature disabled");
            return;
        }
        if (TokenManager.Instance?.IsReady() != true)
        {
            ImGui.TextUnformatted("Link DemiCat to view requests");
            return;
        }
        if (ImGui.Button("Create Request"))
            ImGui.OpenPopup("createRequest");

        var mode = (int)_sortMode;
        if (ImGui.Combo("Sort By", ref mode, SortLabels, SortLabels.Length))
            _sortMode = (SortMode)mode;

        var requests = RequestStateService.All;
        switch (_sortMode)
        {
            case SortMode.Type:
                requests = requests.OrderBy(r => r.Type);
                break;
            case SortMode.Name:
                requests = requests.OrderBy(r => r.Title);
                break;
            case SortMode.MostRecent:
                requests = requests.OrderByDescending(r => r.CreatedAt);
                break;
        }

        ImGui.BeginChild("##requestList", new Vector2(0, 0), true);
        foreach (var req in requests)
        {
            ImGui.PushID(req.Id);
            ImGui.TextWrapped(string.IsNullOrEmpty(req.Description) ? req.Title : req.Description);
            ImGui.TextUnformatted($"Created By: {req.CreatedBy}");
            ImGui.Separator();
            ImGui.PopID();
        }
        ImGui.EndChild();

        if (ImGui.BeginPopup("createRequest"))
        {
            ImGui.InputText("Title", ref _newTitle, 100);
            ImGui.InputTextMultiline("Description", ref _newDescription, 1000, new Vector2(300, 80));
            var typeIdx = (int)_newType;
            var typeLabels = Enum.GetNames<RequestType>();
            if (ImGui.Combo("Type", ref typeIdx, typeLabels, typeLabels.Length))
                _newType = (RequestType)typeIdx;
            var urgIdx = (int)_newUrgency;
            var urgLabels = Enum.GetNames<RequestUrgency>();
            if (ImGui.Combo("Urgency", ref urgIdx, urgLabels, urgLabels.Length))
                _newUrgency = (RequestUrgency)urgIdx;
            if (!string.IsNullOrEmpty(_createStatus))
                ImGui.TextUnformatted(_createStatus);
            if (ImGui.Button("Create"))
            {
                if (ValidateNewRequest())
                {
                    _ = CreateRequest();
                    _createStatus = string.Empty;
                    ImGui.CloseCurrentPopup();
                }
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
    }

    private async Task LoadItem(RequestState req)
    {
        try
        {
            var data = await _gameData.GetItem(req.ItemId!.Value);
            if (data != null)
                req.ItemData = data;
        }
        finally
        {
            _itemLoads.Remove(req.Id);
        }
    }

    private async Task LoadDuty(RequestState req)
    {
        try
        {
            var data = await _gameData.GetDuty(req.DutyId!.Value);
            if (data != null)
                req.DutyData = data;
        }
        finally
        {
            _dutyLoads.Remove(req.Id);
        }
    }

    private async Task Update(RequestState req, RequestStatus newStatus)
    {
        var action = newStatus switch
        {
            RequestStatus.Claimed => "accept",
            RequestStatus.InProgress => "start",
            RequestStatus.AwaitingConfirm => "complete",
            RequestStatus.Completed => "confirm",
            _ => null
        };
        if (action == null) return;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/requests/{req.Id}/{action}";
                var json = JsonSerializer.Serialize(new { version = req.Version });
                var msg = new HttpRequestMessage(HttpMethod.Post, url);
                ApiHelpers.AddAuthHeader(msg, TokenManager.Instance!);
                msg.Content = new StringContent(json, Encoding.UTF8, "application/json");
                var resp = await _httpClient.SendAsync(msg);
                if (resp.IsSuccessStatusCode)
                {
                    var respJson = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(respJson);
                    var payload = doc.RootElement;
                    if (!payload.TryGetProperty("version", out var vEl))
                    {
                        _conflicts[req.Id] = true;
                        return;
                    }
                    var version = vEl.GetInt32();
                    if (version != req.Version + 1)
                    {
                        _conflicts[req.Id] = true;
                        return;
                    }
                    var id = payload.GetProperty("id").GetString() ?? req.Id;
                    var title = payload.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? req.Title : req.Title;
                    var statusStr = payload.GetProperty("status").GetString() ?? StatusToString(newStatus);
                    var typeStr = payload.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? TypeToString(req.Type) : TypeToString(req.Type);
                    var urgencyStr = payload.TryGetProperty("urgency", out var uEl) ? uEl.GetString() ?? UrgencyToString(req.Urgency) : UrgencyToString(req.Urgency);
                    var itemId = payload.TryGetProperty("item_id", out var iEl) ? iEl.GetUInt32() : (uint?)req.ItemId;
                    var dutyId = payload.TryGetProperty("duty_id", out var dEl) ? dEl.GetUInt32() : (uint?)req.DutyId;
                    var hq = payload.TryGetProperty("hq", out var hEl) ? hEl.GetBoolean() : req.Hq;
                    var quantity = payload.TryGetProperty("quantity", out var qEl) ? qEl.GetInt32() : req.Quantity;
                    var assigneeId = payload.TryGetProperty("assignee_id", out var aEl) ? aEl.GetUInt32() : (uint?)req.AssigneeId;
                    var description = payload.TryGetProperty("description", out var descEl) ? descEl.GetString() ?? req.Description : req.Description;
                    var createdBy = payload.TryGetProperty("created_by", out var cbEl) ? cbEl.GetString() ?? req.CreatedBy : req.CreatedBy;
                    DateTime createdAt = req.CreatedAt;
                    if (payload.TryGetProperty("created", out var cEl))
                        cEl.TryGetDateTime(out createdAt);
                    RequestStateService.Upsert(new RequestState
                    {
                        Id = id,
                        Title = title,
                        Status = ParseStatus(statusStr),
                        Type = ParseType(typeStr),
                        Urgency = ParseUrgency(urgencyStr),
                        Version = version,
                        ItemId = itemId,
                        DutyId = dutyId,
                        Hq = hq,
                        Quantity = quantity,
                        AssigneeId = assigneeId,
                        Description = description,
                        CreatedBy = createdBy,
                        CreatedAt = createdAt
                    });
                    return;
                }
                if ((int)resp.StatusCode == 409)
                {
                    await Refresh(req.Id);
                    if (RequestStateService.TryGet(req.Id, out var refreshed))
                    {
                        req = refreshed;
                        continue;
                    }
                }
            }
            catch
            {
                // ignore and mark conflict below
            }
            break;
        }
        _conflicts[req.Id] = true;
    }

    private async Task Refresh(string id)
    {
        try
        {
            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/requests/{id}";
            var msg = new HttpRequestMessage(HttpMethod.Get, url);
            ApiHelpers.AddAuthHeader(msg, TokenManager.Instance!);
            var resp = await _httpClient.SendAsync(msg);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var payload = doc.RootElement;
                var title = payload.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? "Request" : "Request";
                var statusStr = payload.TryGetProperty("status", out var sEl) ? sEl.GetString() ?? "open" : "open";
                var typeStr = payload.TryGetProperty("type", out var tType) ? tType.GetString() ?? "item" : "item";
                var urgencyStr = payload.TryGetProperty("urgency", out var uEl) ? uEl.GetString() ?? "low" : "low";
                var version = payload.TryGetProperty("version", out var vEl) ? vEl.GetInt32() : 0;
                var itemId = payload.TryGetProperty("item_id", out var iEl) ? iEl.GetUInt32() : (uint?)null;
                var dutyId = payload.TryGetProperty("duty_id", out var dEl) ? dEl.GetUInt32() : (uint?)null;
                var hq = payload.TryGetProperty("hq", out var hEl) && hEl.GetBoolean();
                var quantity = payload.TryGetProperty("quantity", out var qEl) ? qEl.GetInt32() : 0;
                var assigneeId = payload.TryGetProperty("assignee_id", out var aEl) ? aEl.GetUInt32() : (uint?)null;
                var description = payload.TryGetProperty("description", out var descEl) ? descEl.GetString() ?? string.Empty : string.Empty;
                var createdBy = payload.TryGetProperty("created_by", out var cbEl) ? cbEl.GetString() ?? string.Empty : string.Empty;
                DateTime createdAt = DateTime.MinValue;
                if (payload.TryGetProperty("created", out var cEl))
                    cEl.TryGetDateTime(out createdAt);
                RequestStateService.Upsert(new RequestState
                {
                    Id = id,
                    Title = title,
                    Status = ParseStatus(statusStr),
                    Type = ParseType(typeStr),
                    Urgency = ParseUrgency(urgencyStr),
                    Version = version,
                    ItemId = itemId,
                    DutyId = dutyId,
                    Hq = hq,
                    Quantity = quantity,
                    AssigneeId = assigneeId,
                    Description = description,
                    CreatedBy = createdBy,
                    CreatedAt = createdAt
                });
            }
        }
        catch
        {
            // ignore
        }
    }

    private static string StatusToString(RequestStatus status) => status switch
    {
        RequestStatus.Open => "open",
        RequestStatus.Claimed => "claimed",
        RequestStatus.InProgress => "in_progress",
        RequestStatus.AwaitingConfirm => "awaiting_confirm",
        RequestStatus.Completed => "completed",
        RequestStatus.Cancelled => "cancelled",
        _ => "open"
    };

    private static RequestStatus ParseStatus(string status) => status switch
    {
        "open" => RequestStatus.Open,
        "claimed" => RequestStatus.Claimed,
        "in_progress" => RequestStatus.InProgress,
        "awaiting_confirm" => RequestStatus.AwaitingConfirm,
        "completed" => RequestStatus.Completed,
        "cancelled" => RequestStatus.Cancelled,
        _ => RequestStatus.Open
    };

    private static string TypeToString(RequestType type) => type switch
    {
        RequestType.Item => "item",
        RequestType.Run => "run",
        RequestType.Event => "event",
        _ => "item"
    };

    private static RequestType ParseType(string type) => type switch
    {
        "item" => RequestType.Item,
        "run" => RequestType.Run,
        "event" => RequestType.Event,
        _ => RequestType.Item
    };

    private static string UrgencyToString(RequestUrgency urgency) => urgency switch
    {
        RequestUrgency.Low => "low",
        RequestUrgency.Medium => "medium",
        RequestUrgency.High => "high",
        _ => "low"
    };

    private static RequestUrgency ParseUrgency(string urgency) => urgency switch
    {
        "low" => RequestUrgency.Low,
        "medium" => RequestUrgency.Medium,
        "high" => RequestUrgency.High,
        _ => RequestUrgency.Low
    };

    private bool ValidateNewRequest()
    {
        if (_newTitle.Length > 2000 || _newDescription.Length > 2000)
        {
            _createStatus = "Title or description exceeds 2000 characters";
            return false;
        }
        _createStatus = string.Empty;
        return true;
    }

    private async Task CreateRequest()
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config) || TokenManager.Instance == null)
            return;
        try
        {
            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/requests";
            var body = new
            {
                title = _newTitle,
                description = string.IsNullOrWhiteSpace(_newDescription) ? null : _newDescription,
                type = TypeToString(_newType),
                urgency = UrgencyToString(_newUrgency)
            };
            var json = JsonSerializer.Serialize(body);
            var msg = new HttpRequestMessage(HttpMethod.Post, url);
            ApiHelpers.AddAuthHeader(msg, TokenManager.Instance!);
            msg.Content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await _httpClient.SendAsync(msg);
            if (resp.IsSuccessStatusCode)
            {
                var stream = await resp.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);
                var id = doc.RootElement.GetProperty("id").GetString();
                if (!string.IsNullOrEmpty(id))
                    await Refresh(id!);
            }
        }
        catch
        {
            // ignored
        }
        finally
        {
            _newTitle = string.Empty;
            _newDescription = string.Empty;
            _newType = RequestType.Item;
            _newUrgency = RequestUrgency.Low;
        }
    }
}

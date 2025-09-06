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

    private enum SortMode
    {
        Type,
        Name,
        MostRecent
    }

    private SortMode _sortMode = SortMode.MostRecent;
    private static readonly string[] SortLabels = { "Type", "Name", "Most Recent" };
    private bool _createOpen;

    public RequestBoardWindow(Config config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
        _gameData = new GameDataCache(httpClient);
        if (config.Requests)
        {
            _ = RequestStateService.RefreshAll(httpClient, config);
        }
    }

    public void Draw()
    {
        if (!_config.Requests)
        {
            ImGui.TextUnformatted("Feature disabled");
            return;
        }
        var mode = (int)_sortMode;
        if (ImGui.Combo("Sort By", ref mode, SortLabels, SortLabels.Length))
            _sortMode = (SortMode)mode;

        var requests = RequestStateService.All;
        switch (_sortMode)
        {
            case SortMode.Type:
                requests = requests.OrderBy(r => r.ItemId.HasValue ? 0 : r.DutyId.HasValue ? 1 : 2);
                break;
            case SortMode.Name:
                requests = requests.OrderBy(r => r.Title);
                break;
            case SortMode.MostRecent:
                requests = requests.OrderByDescending(r => r.CreatedAt);
                break;
        }

        ImGui.BeginChild("##requestList", new Vector2(0, -ImGui.GetFrameHeightWithSpacing() * 2), true);
        foreach (var req in requests)
        {
            ImGui.PushID(req.Id);
            ImGui.TextWrapped(string.IsNullOrEmpty(req.Description) ? req.Title : req.Description);
            if (ImGui.Button("Message"))
            {
                // stub
            }
            ImGui.SameLine();
            if (ImGui.Button("Requirements"))
            {
                // stub
            }
            ImGui.TextUnformatted($"Created By: {req.CreatedBy}");
            ImGui.Separator();
            ImGui.PopID();
        }
        ImGui.EndChild();

        var padding = ImGui.GetStyle().FramePadding;
        var textSize = ImGui.CalcTextSize("Create a Request");
        var buttonSize = textSize + padding * 2;
        var bottomRight = ImGui.GetWindowContentRegionMax();
        ImGui.SetCursorPos(new Vector2(bottomRight.X - buttonSize.X, bottomRight.Y - buttonSize.Y));
        if (ImGui.Button("Create a Request"))
        {
            _createOpen = true;
        }

        if (_createOpen)
        {
            if (ImGui.Begin("New Request", ref _createOpen))
            {
                ImGui.TextUnformatted("Request creation window stub.");
                ImGui.End();
            }
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
}

using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;

namespace DemiCatPlugin;

public class RequestBoardWindow
{
    private readonly Config _config;
    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, bool> _conflicts = new();

    public RequestBoardWindow(Config config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
    }

    public void Draw()
    {
        foreach (var req in RequestStateService.All)
        {
            ImGui.PushID(req.Id);
            ImGui.TextUnformatted($"{req.Title} [{req.Status}]");
            ImGui.SameLine();
            if (_conflicts.ContainsKey(req.Id))
            {
                if (ImGui.Button("Retry"))
                {
                    _ = Refresh(req.Id);
                    _conflicts.Remove(req.Id);
                }
            }
            else
            {
                switch (req.Status)
                {
                    case RequestStatus.Open:
                        if (ImGui.Button("Accept"))
                            _ = Update(req, RequestStatus.Claimed);
                        break;
                    case RequestStatus.Claimed:
                        if (ImGui.Button("In-Progress"))
                            _ = Update(req, RequestStatus.InProgress);
                        break;
                    case RequestStatus.InProgress:
                        if (ImGui.Button("Deliver"))
                            _ = Update(req, RequestStatus.AwaitingConfirm);
                        break;
                    case RequestStatus.AwaitingConfirm:
                        if (ImGui.Button("Confirm"))
                            _ = Update(req, RequestStatus.Completed);
                        break;
                }
            }
            ImGui.PopID();
            ImGui.Separator();
        }
    }

    private async Task Update(RequestState req, RequestStatus newStatus)
    {
        try
        {
            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/requests/{req.Id}/status";
            var json = JsonSerializer.Serialize(new { status = StatusToString(newStatus), version = req.Version });
            var msg = new HttpRequestMessage(HttpMethod.Post, url);
            msg.Headers.Add("X-Api-Key", _config.AuthToken);
            msg.Content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await _httpClient.SendAsync(msg);
            if (resp.IsSuccessStatusCode)
            {
                var respJson = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(respJson);
                var payload = doc.RootElement;
                var id = payload.GetProperty("id").GetString() ?? req.Id;
                var title = payload.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? req.Title : req.Title;
                var statusStr = payload.GetProperty("status").GetString() ?? StatusToString(newStatus);
                var version = payload.TryGetProperty("version", out var vEl) ? vEl.GetInt32() : req.Version + 1;
                RequestStateService.Upsert(new RequestState
                {
                    Id = id,
                    Title = title,
                    Status = ParseStatus(statusStr),
                    Version = version
                });
            }
            else if ((int)resp.StatusCode == 409)
            {
                _conflicts[req.Id] = true;
            }
        }
        catch
        {
            _conflicts[req.Id] = true;
        }
    }

    private async Task Refresh(string id)
    {
        try
        {
            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/requests/{id}";
            var msg = new HttpRequestMessage(HttpMethod.Get, url);
            msg.Headers.Add("X-Api-Key", _config.AuthToken);
            var resp = await _httpClient.SendAsync(msg);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var payload = doc.RootElement;
                var title = payload.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? "Request" : "Request";
                var statusStr = payload.TryGetProperty("status", out var sEl) ? sEl.GetString() ?? "open" : "open";
                var version = payload.TryGetProperty("version", out var vEl) ? vEl.GetInt32() : 0;
                RequestStateService.Upsert(new RequestState
                {
                    Id = id,
                    Title = title,
                    Status = ParseStatus(statusStr),
                    Version = version
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
        _ => "open"
    };

    private static RequestStatus ParseStatus(string status) => status switch
    {
        "open" => RequestStatus.Open,
        "claimed" => RequestStatus.Claimed,
        "in_progress" => RequestStatus.InProgress,
        "awaiting_confirm" => RequestStatus.AwaitingConfirm,
        "completed" => RequestStatus.Completed,
        _ => RequestStatus.Open
    };
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace DemiCatPlugin;

internal static class RequestStateService
{
    private static readonly Dictionary<string, RequestState> RequestsMap = new();
    private static readonly object LockObj = new();

    public static IEnumerable<RequestState> All
    {
        get
        {
            lock (LockObj)
                return RequestsMap.Values.ToList();
        }
    }

    public static void Upsert(RequestState state)
    {
        lock (LockObj)
        {
            if (RequestsMap.TryGetValue(state.Id, out var existing))
            {
                if (state.Version > existing.Version)
                {
                    state.ItemData ??= existing.ItemData;
                    state.DutyData ??= existing.DutyData;
                    RequestsMap[state.Id] = state;
                }
            }
            else
            {
                RequestsMap[state.Id] = state;
            }
        }
    }

    public static void Remove(string id)
    {
        lock (LockObj)
        {
            RequestsMap.Remove(id);
        }
    }

    public static bool TryGet(string id, out RequestState state)
    {
        lock (LockObj)
        {
            return RequestsMap.TryGetValue(id, out state!);
        }
    }

    public static async Task RefreshAll(HttpClient httpClient, Config config)
    {
        if (!ApiHelpers.ValidateApiBaseUrl(config)) return;
        try
        {
            var url = $"{config.ApiBaseUrl.TrimEnd('/')}/api/requests";
            var msg = new HttpRequestMessage(HttpMethod.Get, url);
            ApiHelpers.AddAuthHeader(msg, config);
            var resp = await httpClient.SendAsync(msg);
            if (!resp.IsSuccessStatusCode) return;
            var stream = await resp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            IEnumerable<JsonElement> list;
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                list = doc.RootElement.EnumerateArray();
            else if (doc.RootElement.TryGetProperty("requests", out var arr) && arr.ValueKind == JsonValueKind.Array)
                list = arr.EnumerateArray();
            else
                return;
            foreach (var payload in list)
            {
                var id = payload.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                var title = payload.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? "Request" : "Request";
                var statusString = payload.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
                var version = payload.TryGetProperty("version", out var verEl) ? verEl.GetInt32() : 0;
                var itemId = payload.TryGetProperty("itemId", out var itemEl) ? itemEl.GetUInt32() : (uint?)null;
                var dutyId = payload.TryGetProperty("dutyId", out var dutyEl) ? dutyEl.GetUInt32() : (uint?)null;
                var hq = payload.TryGetProperty("hq", out var hqEl) && hqEl.GetBoolean();
                var quantity = payload.TryGetProperty("quantity", out var qtyEl) ? qtyEl.GetInt32() : 0;
                var assigneeId = payload.TryGetProperty("assigneeId", out var aEl) ? aEl.GetUInt32() : (uint?)null;
                var description = payload.TryGetProperty("description", out var descEl) ? descEl.GetString() ?? string.Empty : string.Empty;
                var createdBy = payload.TryGetProperty("createdBy", out var cbEl) ? cbEl.GetString() ?? string.Empty : string.Empty;
                DateTime createdAt = DateTime.MinValue;
                if (payload.TryGetProperty("created", out var cEl))
                    cEl.TryGetDateTime(out createdAt);
                if (id == null || statusString == null) continue;
                Upsert(new RequestState
                {
                    Id = id,
                    Title = title,
                    Status = ParseStatus(statusString),
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
            // ignored
        }
    }

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

using System.Collections.Generic;
using System.Linq;

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
                    RequestsMap[state.Id] = state;
            }
            else
            {
                RequestsMap[state.Id] = state;
            }
        }
    }

    public static bool TryGet(string id, out RequestState state)
    {
        lock (LockObj)
        {
            return RequestsMap.TryGetValue(id, out state!);
        }
    }
}

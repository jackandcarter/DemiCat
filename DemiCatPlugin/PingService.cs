using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DemiCatPlugin;

internal class PingService
{
    private readonly HttpClient _httpClient;
    private readonly Config _config;
    private readonly TokenManager _tokenManager;
    private readonly object _lock = new();
    private Task<HttpResponseMessage?>? _pingTask;

    internal static PingService? Instance { get; set; }

    internal PingService(HttpClient httpClient, Config config, TokenManager tokenManager)
    {
        _httpClient = httpClient;
        _config = config;
        _tokenManager = tokenManager;
    }

    internal Task<HttpResponseMessage?> PingAsync(CancellationToken token)
    {
        lock (_lock)
        {
            if (_pingTask == null || _pingTask.IsCompleted)
            {
                _pingTask = ApiHelpers.PingAsync(_httpClient, _config, _tokenManager, token);
            }
            return _pingTask;
        }
    }
}


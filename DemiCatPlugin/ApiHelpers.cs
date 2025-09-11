using System;
using System.Net.Http;
using System.Net.WebSockets;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace DemiCatPlugin;

internal static class ApiHelpers
{
    internal static bool ValidateApiBaseUrl(Config config)
    {
        if (!Uri.TryCreate(config.ApiBaseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            PluginServices.Instance!.Log.Error($"Invalid API base URL: {config.ApiBaseUrl}");
            return false;
        }
        return true;
    }

    internal static void AddAuthHeader(HttpRequestMessage request, TokenManager tokenManager)
        => AddAuthHeader(request, tokenManager.Token);

    internal static void AddAuthHeader(HttpRequestMessage request, string? token)
    {
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Add("X-Api-Key", token);
        }
    }

    internal static void AddAuthHeader(ClientWebSocket socket, TokenManager tokenManager)
        => AddAuthHeader(socket, tokenManager.Token);

    internal static void AddAuthHeader(ClientWebSocket socket, string? token)
    {
        if (!string.IsNullOrEmpty(token))
        {
            socket.Options.SetRequestHeader("X-Api-Key", token);
        }
    }

    internal static async Task<HttpResponseMessage?> SendWithRetries(HttpRequestMessage request, HttpClient httpClient)
    {
        const int maxAttempts = 3;
        const double baseDelaySeconds = 0.5;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                PluginServices.Instance!.Log.Debug($"HTTP {request.Method} {request.RequestUri} attempt {attempt}/{maxAttempts}");
                var response = await httpClient.SendAsync(request);
                PluginServices.Instance!.Log.Debug($"HTTP {request.Method} {request.RequestUri} responded {(int)response.StatusCode}");
                return response;
            }
            catch (Exception ex)
            {
                PluginServices.Instance!.Log.Warning(ex, $"Attempt {attempt}/{maxAttempts} failed for {request.Method} {request.RequestUri}");
                if (attempt == maxAttempts)
                {
                    PluginServices.Instance!.Log.Error($"HTTP {request.Method} {request.RequestUri} failed after {maxAttempts} attempts");
                    return null;
                }

                var delay = TimeSpan.FromSeconds(baseDelaySeconds * Math.Pow(2, attempt - 1));
                PluginServices.Instance!.Log.Debug($"Retrying in {delay.TotalSeconds:0.##}s");
                await Task.Delay(delay);
            }
        }

        return null;
    }

    internal static async Task<HttpResponseMessage?> PingAsync(HttpClient httpClient, Config config, TokenManager tokenManager, CancellationToken token)
    {
        try
        {
            var baseUrl = config.ApiBaseUrl.TrimEnd('/');
            var ping = new HttpRequestMessage(HttpMethod.Head, $"{baseUrl}/api/ping");
            AddAuthHeader(ping, tokenManager);
            var response = await httpClient.SendAsync(ping, token);
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                return response;
            }

            var health = new HttpRequestMessage(HttpMethod.Head, $"{baseUrl}/health");
            AddAuthHeader(health, tokenManager);
            var healthResponse = await httpClient.SendAsync(health, token);
            if (healthResponse.StatusCode == HttpStatusCode.NotFound)
            {
                PluginServices.Instance?.Log.Error("Backend missing /api/ping and /health endpoints. Please update or restart the backend.");
            }
            return healthResponse;
        }
        catch
        {
            return null;
        }
    }
}


using System;
using System.Net.Http;
using System.Net.WebSockets;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DemiCatPlugin;

internal static class ApiHelpers
{
    private static readonly Regex StatusCodeRegex =
        new("status code '?([0-9]{3})'?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

    internal static HttpStatusCode? ExtractStatusCode(Exception ex)
    {
        Exception? current = ex;
        while (current != null)
        {
            if (current is HttpRequestException http && http.StatusCode.HasValue)
            {
                return http.StatusCode.Value;
            }

            if (current is WebSocketException ws)
            {
                if (ws.InnerException != null)
                {
                    var inner = ExtractStatusCode(ws.InnerException);
                    if (inner.HasValue)
                    {
                        return inner.Value;
                    }
                }

                var message = ws.Message;
                if (!string.IsNullOrEmpty(message))
                {
                    var match = StatusCodeRegex.Match(message);
                    if (match.Success && int.TryParse(match.Groups[1].Value, out var numeric) &&
                        Enum.IsDefined(typeof(HttpStatusCode), numeric))
                    {
                        return (HttpStatusCode)numeric;
                    }
                }
            }

            current = current.InnerException;
        }

        return null;
    }

    internal static bool IsUnauthorized(Exception ex)
        => ExtractStatusCode(ex) == HttpStatusCode.Unauthorized;

    internal static bool IsForbidden(Exception ex)
        => ExtractStatusCode(ex) == HttpStatusCode.Forbidden;

    internal static void AddAuthHeader(ClientWebSocket socket, TokenManager tokenManager)
        => AddAuthHeader(socket, tokenManager.Token);

    internal static void AddAuthHeader(ClientWebSocket socket, string? token)
    {
        if (!string.IsNullOrEmpty(token))
        {
            socket.Options.SetRequestHeader("X-Api-Key", token);
        }
    }

    internal static async Task<HttpResponseMessage?> SendWithRetries(Func<HttpRequestMessage> requestFactory, HttpClient httpClient)
    {
        const int maxAttempts = 3;
        const double baseDelaySeconds = 0.5;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            HttpRequestMessage? request = null;
            var methodName = "UNKNOWN";
            var requestUri = "(null)";
            try
            {
                request = requestFactory() ?? throw new InvalidOperationException("Request factory returned null.");
                methodName = request.Method.Method;
                requestUri = request.RequestUri?.ToString() ?? "(null)";

                PluginServices.Instance!.Log.Debug($"HTTP {methodName} {requestUri} attempt {attempt}/{maxAttempts}");
                var response = await httpClient.SendAsync(request);
                PluginServices.Instance!.Log.Debug($"HTTP {methodName} {requestUri} responded {(int)response.StatusCode}");

                if (response.IsSuccessStatusCode)
                {
                    // Successful communications are logged at info so they are visible in Dalamud's log file.
                    PluginServices.Instance!.Log.Information($"HTTP {methodName} {requestUri} succeeded");
                }
                else
                {
                    // If the server returns an error status code, capture the body so the reason is clear to the user.
                    var body = await response.Content.ReadAsStringAsync();
                    PluginServices.Instance!.Log.Warning($"HTTP {methodName} {requestUri} failed with {(int)response.StatusCode} {response.ReasonPhrase}. Body: {body}");
                }

                return response;
            }
            catch (Exception ex)
            {
                PluginServices.Instance!.Log.Warning(ex, $"Attempt {attempt}/{maxAttempts} failed for {methodName} {requestUri}");
                if (attempt == maxAttempts)
                {
                    PluginServices.Instance!.Log.Error($"HTTP {methodName} {requestUri} failed after {maxAttempts} attempts");
                    return null;
                }

                var delay = TimeSpan.FromSeconds(baseDelaySeconds * Math.Pow(2, attempt - 1));
                PluginServices.Instance!.Log.Debug($"Retrying in {delay.TotalSeconds:0.##}s");
                await Task.Delay(delay);
            }
            finally
            {
                request?.Dispose();
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


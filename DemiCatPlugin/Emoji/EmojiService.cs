using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace DemiCatPlugin.Emoji
{
    public sealed class EmojiService
    {
        private readonly HttpClient _http;
        private readonly TokenManager _tokens;
        private readonly Config _config;
        private readonly object _refreshLock = new();
        private CancellationTokenSource? _refreshCancellation;
        private string? _lastLoopErrorSignature;
        private DateTime _lastLoopErrorLoggedAt;
        private static readonly TimeSpan LoopErrorThrottle = TimeSpan.FromSeconds(30);
        private const bool EmojisEnabled = false; // Temporarily disable custom emoji syncing until feature is redesigned.

        public List<CustomEmoji> Custom { get; private set; } = new();

        public event Action? Updated;

        public EmojiService(HttpClient http, TokenManager tokens, Config config)
        { _http = http; _tokens = tokens; _config = config; }

        public Task RefreshAsync(CancellationToken ct = default)
        {
            if (!EmojisEnabled)
            {
                lock (_refreshLock)
                {
                    _refreshCancellation?.Cancel();
                    _refreshCancellation?.Dispose();
                    _refreshCancellation = null;
                }

                Custom = new();
                PublishUpdate();
                return Task.CompletedTask;
            }

            CancellationTokenSource linked;
            lock (_refreshLock)
            {
                _refreshCancellation?.Cancel();
                _refreshCancellation?.Dispose();
                linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _refreshCancellation = linked;
            }

            var task = RefreshLoopAsync(linked.Token);
            _ = task.ContinueWith(_ =>
            {
                linked.Dispose();
                lock (_refreshLock)
                {
                    if (_refreshCancellation == linked)
                    {
                        _refreshCancellation = null;
                    }
                }
            }, TaskScheduler.Default);

            return task;
        }

        public async Task<(bool ok, List<CustomEmoji> list, HttpStatusCode status, int? retryAfterSeconds)> TryGetEmojisAsync(CancellationToken ct = default)
        {
            var emojis = new List<CustomEmoji>();

            if (!_tokens.IsReady() || string.IsNullOrEmpty(_tokens.Token))
            {
                return (false, emojis, HttpStatusCode.Unauthorized, null);
            }

            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/discord/emojis";

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                ApiHelpers.AddAuthHeader(req, _tokens);

                using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
                var retryAfter = GetRetryAfterSeconds(res.Headers.RetryAfter);

                if (res.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return (false, emojis, res.StatusCode, retryAfter);
                }

                if (!res.IsSuccessStatusCode)
                {
                    return (false, emojis, res.StatusCode, retryAfter);
                }

                await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                if (doc.RootElement.TryGetProperty("emojis", out var arr))
                {
                    foreach (var e in arr.EnumerateArray())
                    {
                        var id = e.GetProperty("id").GetString();
                        var name = e.GetProperty("name").GetString();
                        if (id == null || name == null)
                            continue;
                        var animated = e.GetProperty("animated").GetBoolean();
                        var available = e.TryGetProperty("available", out var a) ? a.GetBoolean() : true;
                        if (available)
                        {
                            emojis.Add(new CustomEmoji(id, name, animated));
                        }
                    }
                }

                return (true, emojis, res.StatusCode, retryAfter);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                return (false, emojis, ex.StatusCode ?? 0, null);
            }
            catch (JsonException)
            {
                return (false, emojis, HttpStatusCode.InternalServerError, null);
            }
            catch
            {
                return (false, emojis, 0, null);
            }
        }

        private async Task RefreshLoopAsync(CancellationToken ct)
        {
            if (!_tokens.IsReady() || string.IsNullOrEmpty(_tokens.Token))
            {
                PluginServices.Instance?.ToastGui.ShowError("Emoji auth failed");
                Custom = new();
                PublishUpdate();
                return;
            }

            var attempt = 0;

            while (!ct.IsCancellationRequested)
            {
                attempt++;

                (bool ok, List<CustomEmoji> list, HttpStatusCode status, int? retryAfterSeconds) result;
                try
                {
                    result = await TryGetEmojisAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogLoopFailure(ex);
                    break;
                }

                var shouldRetry = ShouldRetry(result.ok, result.list, result.status, result.retryAfterSeconds);
                var nextDelay = shouldRetry && !ct.IsCancellationRequested
                    ? GetNextDelay(attempt, result.retryAfterSeconds)
                    : (TimeSpan?)null;

                LogAttempt(
                    attempt,
                    result.status,
                    result.ok,
                    result.list.Count,
                    nextDelay,
                    result.retryAfterSeconds
                );

                if (result.status == HttpStatusCode.Unauthorized || result.status == HttpStatusCode.Forbidden)
                {
                    PluginServices.Instance?.ToastGui.ShowError("Emoji auth failed");
                    _tokens.Clear("Authentication failed");
                    Custom = new();
                    PublishUpdate();
                    return;
                }

                if (result.ok)
                {
                    Custom = result.list;
                    PublishUpdate();

                    if (result.list.Count > 0)
                    {
                        return;
                    }
                }

                if (!shouldRetry || nextDelay == null)
                {
                    return;
                }

                try
                {
                    await Task.Delay(nextDelay.Value, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private static TimeSpan GetNextDelay(int attempt, int? retryAfterSeconds)
        {
            if (retryAfterSeconds.HasValue && retryAfterSeconds.Value >= 0)
            {
                return TimeSpan.FromSeconds(retryAfterSeconds.Value);
            }

            var cappedAttempt = Math.Max(1, attempt);
            var baseDelay = Math.Min(15d, Math.Pow(2, cappedAttempt - 1));
            var min = Math.Max(0.5d, baseDelay / 2d);
            var max = Math.Max(min, baseDelay);
            var jitter = min + (max - min) * Random.Shared.NextDouble();
            return TimeSpan.FromSeconds(jitter);
        }

        private static bool ShouldRetry(bool ok, List<CustomEmoji> list, HttpStatusCode status, int? retryAfterSeconds)
        {
            if (ok)
            {
                if (list.Count == 0)
                {
                    return retryAfterSeconds.HasValue;
                }

                return false;
            }

            if (status == HttpStatusCode.Unauthorized || status == HttpStatusCode.Forbidden)
            {
                return false;
            }

            if (status == HttpStatusCode.BadRequest || status == HttpStatusCode.NotFound)
            {
                return false;
            }

            if (status == HttpStatusCode.TooManyRequests || status == HttpStatusCode.RequestTimeout ||
                status == HttpStatusCode.BadGateway || status == HttpStatusCode.ServiceUnavailable ||
                status == HttpStatusCode.GatewayTimeout)
            {
                return true;
            }

            var statusCode = (int)status;
            if (statusCode == 0)
            {
                return true;
            }

            if (statusCode >= 500 && statusCode <= 599)
            {
                return true;
            }

            return false;
        }

        private static void LogAttempt(int attempt, HttpStatusCode status, bool ok, int count, TimeSpan? nextDelay, int? retryAfter)
        {
            var log = PluginServices.Instance?.Log;
            if (log == null) return;

            var outcome = ok ? (count > 0 ? "success" : "warm-up") : "failed";
            var statusCode = status == 0 ? 0 : (int)status;
            var statusName = status == 0 ? "n/a" : status.ToString();
            var delayDescription = nextDelay.HasValue ? $"{nextDelay.Value.TotalSeconds:0.###}s" : "none";
            var willRetry = nextDelay.HasValue;

            if (willRetry)
            {
                if (retryAfter.HasValue)
                {
                    log.Warning(
                        "Emoji refresh retry {Attempt}. Outcome {Outcome}. Status {StatusCode} ({StatusName}). Next retry in {Delay} (Retry-After {RetryAfter}s)",
                        attempt,
                        outcome,
                        statusCode,
                        statusName,
                        delayDescription,
                        retryAfter.Value
                    );
                }
                else
                {
                    log.Warning(
                        "Emoji refresh retry {Attempt}. Outcome {Outcome}. Status {StatusCode} ({StatusName}). Next retry in {Delay}",
                        attempt,
                        outcome,
                        statusCode,
                        statusName,
                        delayDescription
                    );
                }
            }
            else
            {
                if (retryAfter.HasValue)
                {
                    log.Information(
                        "Emoji refresh attempt {Attempt} {Outcome}. Status {StatusCode} ({StatusName}). Next retry {Delay} (Retry-After {RetryAfter}s)",
                        attempt,
                        outcome,
                        statusCode,
                        statusName,
                        delayDescription,
                        retryAfter.Value
                    );
                }
                else
                {
                    log.Information(
                        "Emoji refresh attempt {Attempt} {Outcome}. Status {StatusCode} ({StatusName}). Next retry {Delay}",
                        attempt,
                        outcome,
                        statusCode,
                        statusName,
                        delayDescription
                    );
                }
            }
        }

        private void LogLoopFailure(Exception ex)
        {
            var log = PluginServices.Instance?.Log;
            if (log == null)
            {
                return;
            }

            var signature = $"{ex.GetType().FullName}:{ex.Message}";
            var now = DateTime.UtcNow;
            if (_lastLoopErrorSignature == signature && (now - _lastLoopErrorLoggedAt) < LoopErrorThrottle)
            {
                return;
            }

            _lastLoopErrorSignature = signature;
            _lastLoopErrorLoggedAt = now;
            log.Error(ex, "Emoji refresh loop failed");
        }

        private void PublishUpdate()
        {
            var handlers = Updated;
            if (handlers == null)
                return;

            var framework = PluginServices.Instance?.Framework;
            if (framework != null)
            {
                _ = framework.RunOnTick(() => handlers.Invoke());
            }
            else
            {
                handlers.Invoke();
            }
        }

        private static int? GetRetryAfterSeconds(RetryConditionHeaderValue? retryAfter)
        {
            if (retryAfter == null)
                return null;

            if (retryAfter.Delta.HasValue)
            {
                return (int)Math.Ceiling(Math.Max(0, retryAfter.Delta.Value.TotalSeconds));
            }

            if (retryAfter.Date.HasValue)
            {
                var delta = retryAfter.Date.Value - DateTimeOffset.UtcNow;
                if (delta > TimeSpan.Zero)
                {
                    return (int)Math.Ceiling(delta.TotalSeconds);
                }
            }

            return null;
        }
    }
}

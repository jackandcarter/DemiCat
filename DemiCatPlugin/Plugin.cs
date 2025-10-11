using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DiscordHelper;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.Toast;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Textures;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DemiCatPlugin.Avatars;
using DemiCatPlugin.Emoji;
using StbImageSharp;

namespace DemiCatPlugin;

public class Plugin : IDalamudPlugin
{
    public string Name => "DemiCat";

    [PluginService] internal IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal ITextureProvider TextureProvider { get; private set; } = null!;

    private const uint InvalidTokenLinkCommandId = 0x44434B49;
    private const string MewCommand = "/mew";
    private const string MewCommandHelpMessage = "Open the DemiCat main window.";

    private readonly PluginServices _services;
    private readonly UiRenderer _ui;
    private readonly SettingsWindow _settings;
    private readonly AvatarCache _avatarCache;
    private readonly ChatWindow _chatWindow;
    private readonly OfficerChatWindow _officerChatWindow;
    private readonly DiscordPresenceService? _presenceService;
    private readonly MainWindow _mainWindow;
    private readonly ChannelWatcher _channelWatcher;
    private readonly RequestWatcher _requestWatcher;
    private readonly NotePadService _notePadService;
    private readonly NotePadWindow _notePadWindow;
    private readonly ChannelSelectionService _channelSelection;
    private readonly EmojiManager _emojiManager;
    private readonly RefCountedChatBridge _sharedChatBridge;
    private readonly MessageCache _messageCache;
    private readonly ChatBridge _baseChatBridge;
    private readonly Timer _queueMetricsTimer;
    private bool _disposed;

    private Config _config = null!;
    private readonly HttpClient _httpClient;
    private readonly ChannelService _channelService;
    private readonly Action _openMainUi;
    private readonly Action _openConfigUi;
    private readonly TokenManager _tokenManager;
    private bool _officerWatcherRunning;
    private bool _invalidTokenToastShown;
    private readonly SemaphoreSlim _watcherRestartLock = new(1, 1);
    private readonly IFontHandle? _emojiFontHandle;
    private readonly CommandInfo _mewCommandInfo;

    public Plugin()
    {
        _services = PluginInterface.Create<PluginServices>()!;
        if (_services.PluginInterface == null || _services.Log == null)
            throw new InvalidOperationException("Failed to initialize plugin services.");

        _config = _services.PluginInterface.GetPluginConfig() as Config ?? new Config();
        _tokenManager = new TokenManager(_services.PluginInterface);

        var oldVersion = _config.Version;
        _config.Migrate();
        var rolesRemoved = _config.Roles.RemoveAll(r => r == "chat") > 0;
        if (rolesRemoved || _config.Version != oldVersion)
            _services.PluginInterface.SavePluginConfig(_config);

        RequestStateService.Load(_config);

        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 10,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All
        };
        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

        WebTextureCache.FetchOverride = FetchWebTexture;

        PingService.Instance = new PingService(_httpClient, _config, _tokenManager);

        _channelSelection = new ChannelSelectionService(_config);
        _emojiManager = new EmojiManager(_httpClient, _tokenManager, _config);
        _emojiFontHandle = InitializeEmojiFont();
        _emojiManager.EmojiFontHandle = _emojiFontHandle;
        _messageCache = new MessageCache();
        var baseChatBridge = new ChatBridge(
            _config,
            _httpClient,
            _tokenManager,
            () =>
            {
                var baseUri = _config.ApiBaseUrl.TrimEnd('/') + "/ws/chat";
                var builder = new UriBuilder(baseUri);
                if (builder.Scheme == "https")
                {
                    builder.Scheme = "wss";
                }
                else if (builder.Scheme == "http")
                {
                    builder.Scheme = "ws";
                }

                return builder.Uri;
            },
            _channelSelection);
        _baseChatBridge = baseChatBridge;
        _sharedChatBridge = new RefCountedChatBridge(baseChatBridge);
        _ui = new UiRenderer(_config, _httpClient, _channelSelection, _emojiManager);
        _settings = new SettingsWindow(_config, _tokenManager, _httpClient, () => RefreshRoles(_services.Log), _ui.StartNetworking, _services.Log, _services.PluginInterface);

        _presenceService = _config.SyncedChat && _config.EnableFcChat
            ? new DiscordPresenceService(_config, _httpClient)
            : null;

        _channelService = new ChannelService(_config, _httpClient, _tokenManager);
        _avatarCache = new AvatarCache(TextureProvider, _httpClient);
        _chatWindow = new FcChatWindow(_config, _httpClient, _presenceService, _tokenManager, _channelService, _channelSelection, _messageCache, _avatarCache, _emojiManager, _sharedChatBridge);
        _officerChatWindow = new OfficerChatWindow(_config, _httpClient, _presenceService, _tokenManager, _channelService, _channelSelection, _messageCache, _avatarCache, _emojiManager, _sharedChatBridge);

        _presenceService?.Reset();

        _notePadService = new NotePadService(_config, _httpClient, _tokenManager);
        _notePadWindow = new NotePadWindow(_config, _notePadService);

        _mainWindow = new MainWindow(
            _config,
            _ui,
            _chatWindow,
            _officerChatWindow,
            _settings,
            _httpClient,
            _channelService,
            _channelSelection,
            _emojiManager,
            _notePadWindow
        );

        _channelWatcher = new ChannelWatcher(_config, _ui, _mainWindow.EventCreateWindow, _mainWindow.TemplatesWindow, _chatWindow, _officerChatWindow, _tokenManager, _httpClient);
        _requestWatcher = new RequestWatcher(_config, _httpClient, _tokenManager);

        _channelSelection.ChannelChanged += HandleChannelSelectionValidation;

        _settings.MainWindow = _mainWindow;
        _settings.ChatWindow = _chatWindow;
        _settings.OfficerChatWindow = _officerChatWindow;
        _settings.ChannelWatcher = _channelWatcher;
        _settings.RequestWatcher = _requestWatcher;
        _settings.NotePadService = _notePadService;

        _mainWindow.HasOfficerAccess = OfficerPermissions.HasAccess(_config);
        _mainWindow.SetNotePadReadOnly(!_tokenManager.IsReady());

        _ = RoleCache.EnsureLoaded(_httpClient, _config);

        _services.PluginInterface.UiBuilder.Draw += _mainWindow.Draw;
        _services.PluginInterface.UiBuilder.Draw += _settings.Draw;
        _services.PluginInterface.UiBuilder.Draw += DrawOverlay;

        _openMainUi = () => _mainWindow.IsOpen = true;
        _services.PluginInterface.UiBuilder.OpenMainUi += _openMainUi;

        _openConfigUi = () => _settings.IsOpen = true;
        _services.PluginInterface.UiBuilder.OpenConfigUi += _openConfigUi;

        _mewCommandInfo = new CommandInfo(OnMewCommand)
        {
            HelpMessage = MewCommandHelpMessage
        };
        _services.CommandManager.AddHandler(MewCommand, _mewCommandInfo);

        _tokenManager.OnLinked += HandleTokenLinked;
        _tokenManager.OnUnlinked += HandleTokenUnlinked;

        if (_tokenManager.IsReady())
        {
            HandleTokenLinked();
        }
        else
        {
            _mainWindow.NotifyLinkRequired();
            _settings.IsOpen = true;
        }

        _services.Log.Info("DemiCat loaded.");
        _queueMetricsTimer = new Timer(LogQueueStats, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public void Dispose()
    {
        _disposed = true;
        _queueMetricsTimer.Dispose();
        WebTextureCache.FetchOverride = null;
        try
        {
            RunOnFrameworkAsync(WebTextureCache.Clear).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _services.Log.Warning(ex, "Failed to clear web texture cache on framework thread.");
            WebTextureCache.Clear();
        }

        _emojiManager.EmojiFontHandle = null;
        _emojiFontHandle?.Dispose();

        // Unsubscribe UI draw handlers
        _services.PluginInterface.UiBuilder.Draw -= _mainWindow.Draw;
        _services.PluginInterface.UiBuilder.Draw -= _settings.Draw;
        _services.PluginInterface.UiBuilder.Draw -= DrawOverlay;

        // Unsubscribe UI open handlers
        _services.PluginInterface.UiBuilder.OpenMainUi -= _openMainUi;
        _services.PluginInterface.UiBuilder.OpenConfigUi -= _openConfigUi;

        _services.CommandManager.RemoveHandler(MewCommand);

        _tokenManager.OnLinked -= HandleTokenLinked;
        _tokenManager.OnUnlinked -= HandleTokenUnlinked;

        try { _services.ChatGui.RemoveChatLinkHandler(InvalidTokenLinkCommandId); } catch { }

        _channelSelection.ChannelChanged -= HandleChannelSelectionValidation;

        _channelWatcher.Dispose();
        _requestWatcher.Dispose();
        _notePadWindow.Dispose();
        _notePadService.Dispose();
        _presenceService?.Dispose();
        _chatWindow.Dispose();
        _officerChatWindow.Dispose();
        _sharedChatBridge.Dispose();
        _mainWindow.Dispose();
        _ui.DisposeAsync().GetAwaiter().GetResult();
        _settings.Dispose();
        _avatarCache.Dispose();
        _emojiManager.Dispose();
        _httpClient.Dispose();
        if (PluginServices.Instance != null)
        {
            PluginServices.Instance.ProgressOverlay = null;
        }
    }

    private void LogQueueStats(object? state)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var stats = _baseChatBridge.GetQueueStats();
            _services.Log?.Info(
                $"chat.q hiText={stats.HighText} hiDel={stats.HighDelete} midTy={stats.MediumTyping} lowAsset={stats.LowAssets} shedMid={stats.ShedMedium} shedLow={stats.ShedLow}");
        }
        catch (Exception ex)
        {
            _services.Log?.Warning(ex, "Failed to log chat queue stats.");
        }
    }

    private void OnMewCommand(string command, string arguments)
    {
        _mainWindow.IsOpen = true;
    }

    private void DrawOverlay()
    {
        SyncshellWindow.Instance?.PumpClientEvents();
        var overlay = PluginServices.Instance?.ProgressOverlay;
        if (overlay == null)
            return;

        overlay.IsVisible = _config.FCSyncShell && _config.ShowSyncshellProgressOverlay;
        overlay.Draw();
    }

    private object? FetchWebTexture(string url, Action<ISharedImmediateTexture?> onReady)
    {
        if (string.IsNullOrEmpty(url))
            return RunOnFrameworkAsync(() => onReady(null));

        if (WebTextureCache.TryGetTexture(url, out var cached))
            return RunOnFrameworkAsync(() => onReady(cached));

        return FetchWebTextureAsync(url, onReady);
    }

    private async Task FetchWebTextureAsync(string url, Action<ISharedImmediateTexture?> onReady)
    {
        HttpResponseMessage? response = null;
        byte[]? payload = null;

        try
        {
            response = await _httpClient
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _services.Log.Warning(
                    $"Failed to download texture {url}: {(int)response.StatusCode} {response.StatusCode}. Falling back to text label.");
                await RunOnFrameworkAsync(() => onReady(null)).ConfigureAwait(false);
                return;
            }

            payload = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }
        catch (TaskCanceledException ex)
        {
            _services.Log.Warning(ex, $"Texture download timed out for {url}. Falling back to text label.");
            await RunOnFrameworkAsync(() => onReady(null)).ConfigureAwait(false);
            return;
        }
        catch (HttpRequestException ex)
        {
            _services.Log.Warning(ex, $"Failed to download texture {url}. Falling back to text label.");
            await RunOnFrameworkAsync(() => onReady(null)).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            _services.Log.Error(ex, $"Unexpected error downloading texture {url}. Falling back to text label.");
            await RunOnFrameworkAsync(() => onReady(null)).ConfigureAwait(false);
            return;
        }
        finally
        {
            response?.Dispose();
        }

        if (payload == null || payload.Length == 0)
        {
            _services.Log.Warning($"Texture download for {url} returned no data. Falling back to text label.");
            await RunOnFrameworkAsync(() => onReady(null)).ConfigureAwait(false);
            return;
        }

        ImageResult image;
        try
        {
            image = ImageResult.FromMemory(payload, ColorComponents.RedGreenBlueAlpha);
        }
        catch (Exception ex)
        {
            _services.Log.Error(ex, $"Failed to decode texture {url}. Falling back to text label.");
            await RunOnFrameworkAsync(() => onReady(null)).ConfigureAwait(false);
            return;
        }

        try
        {
            await RunOnFrameworkAsync(() =>
            {
                try
                {
                    var wrap = TextureProvider.CreateFromRaw(
                        RawImageSpecification.Rgba32(image.Width, image.Height),
                        image.Data);
                    var texture = new ForwardingSharedImmediateTexture(wrap);
                    WebTextureCache.Set(url, texture);
                    onReady(texture);
                }
                catch (Exception ex)
                {
                    _services.Log.Error(ex, $"Failed to create texture for {url}. Falling back to text label.");
                    WebTextureCache.Set(url, null);
                    onReady(null);
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _services.Log.Error(ex, $"Failed to finalize texture load for {url}. Falling back to text label.");
            await RunOnFrameworkAsync(() => onReady(null)).ConfigureAwait(false);
        }
    }

    private Task RunOnFrameworkAsync(Action action)
    {
        var framework = PluginServices.Instance?.Framework ?? _services.Framework;
        if (framework != null)
            return framework.RunOnTick(action);

        action();
        return Task.CompletedTask;
    }

    private IFontHandle? InitializeEmojiFont()
    {
        try
        {
            var directory = _services.PluginInterface.AssemblyLocation.Directory;
            if (directory == null)
            {
                return null;
            }

            string? fontPath = null;
            var basePath = directory.FullName;
            var candidates = new[]
            {
                Path.Combine(basePath, "Emoji", "NotoColorEmoji.ttf"),
                Path.Combine(basePath, "NotoColorEmoji.ttf"),
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    fontPath = candidate;
                    break;
                }
            }

            if (fontPath == null)
            {
                _services.Log.Info(
                    "Emoji font not found at any known path ({Paths}). Unicode emoji will use fallback glyphs.",
                    string.Join(", ", candidates)
                );
                return null;
            }

            var atlas = _services.PluginInterface.UiBuilder.FontAtlas;
            var fontSize = _services.PluginInterface.UiBuilder.FontDefaultSizePx;

            var handle = atlas.NewDelegateFontHandle(toolkit =>
                toolkit.OnPreBuild(pre =>
                {
                    var baseFont = pre.AddDalamudDefaultFont(fontSize);
                    var glyphRanges = default(FluentGlyphRangeBuilder)
                        .With(0x2000, 0x27FF) // general punctuation, arrows, dingbats, enclosed & geometric symbols
                        .With(0x2B00, 0x2BFF) // misc symbols and arrows (e.g. stars, triangles)
                        .With(0xD83C, 0xD83E) // high-surrogates covering U+1F000-U+1FAFF
                        .With(0xDC00, 0xDFFF) // low-surrogates for the same range
                        .With(0x200D, 0x200D) // zero width joiner for multi-glyph emoji sequences
                        .With(0x20E3, 0x20E3) // combining enclosing keycap used by keycap emoji
                        .With(0xFE0E, 0xFE0F) // text & emoji presentation selectors
                        .Build();
                    var config = new SafeFontConfig
                    {
                        SizePx = fontSize,
                        MergeFont = baseFont,
                        PixelSnapH = true,
                        GlyphRanges = glyphRanges,
                    };
                    config.Raw.FontBuilderFlags |= 1u << 8; // ImGuiFreeTypeBuilderFlags_LoadColor
                    pre.AddFontFromFile(fontPath, config);
                    pre.Font = baseFont;
                }));

            _services.Log.Info("Loaded emoji font from {Path}.", fontPath);
            return handle;
        }
        catch (Exception ex)
        {
            _services.Log.Warning(ex, "Failed to load emoji font. Unicode emoji will use fallback glyphs.");
            return null;
        }
    }

    private async void HandleChannelSelectionValidation(string kind, string guildId, string oldId, string newId)
    {
        await ValidateChannelSelectionAsync(kind, guildId, newId, oldId);
    }

    private async Task ValidateChannelSelectionAsync(string kind, string guildId, string channelId, string? previousChannelId = null)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            return;
        }

        var normalizedRequestedGuild = ChannelKeyHelper.NormalizeGuildId(guildId);
        var normalizedStoredGuild = ChannelKeyHelper.NormalizeGuildId(_config.GuildId);

        if (!string.Equals(normalizedRequestedGuild, normalizedStoredGuild, StringComparison.Ordinal))
        {
            return;
        }

        if (!_tokenManager.IsReady())
        {
            return;
        }

        try
        {
            var response = await _channelService.ValidateAsync(kind, channelId, CancellationToken.None);
            if (response == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(response.GuildId))
            {
                var normalizedResponseGuild = ChannelKeyHelper.NormalizeGuildId(response.GuildId);
                var hasStoredGuild = !ChannelKeyHelper.IsDefaultGuild(_config.GuildId);

                if (hasStoredGuild &&
                    !string.Equals(normalizedStoredGuild, normalizedResponseGuild, StringComparison.Ordinal))
                {
                    void RejectSelection()
                    {
                        var restoreChannelId = string.IsNullOrWhiteSpace(previousChannelId) ? string.Empty : previousChannelId;
                        _channelSelection.SetChannel(kind, _config.GuildId, restoreChannelId);
                        PluginServices.Instance?.ToastGui.ShowError(
                            "Cannot select a channel from a different Discord server without unlinking first."
                        );
                    }

                    var framework = PluginServices.Instance?.Framework;
                    if (framework != null)
                    {
                        _ = framework.RunOnTick(RejectSelection);
                    }
                    else
                    {
                        RejectSelection();
                    }

                    return;
                }

                if (!hasStoredGuild)
                {
                    var guildChanged = !string.Equals(normalizedStoredGuild, normalizedResponseGuild, StringComparison.Ordinal);
                    _config.GuildId = normalizedResponseGuild;
                    _services.PluginInterface.SavePluginConfig(_config);

                    _chatWindow.OnGuildUpdated();
                    _officerChatWindow.OnGuildUpdated();

                    await HandleGuildChangedAsync(normalizedResponseGuild, kind).ConfigureAwait(false);
                }
            }

            if (response.Ok)
            {
                return;
            }

            var reason = response.Reason ?? string.Empty;

            if (string.Equals(reason, "WRONG_KIND", StringComparison.OrdinalIgnoreCase))
            {
                PluginServices.Instance?.Framework.RunOnTick(() =>
                {
                    _channelWatcher.TriggerRefresh(true);
                });
            }
            else if (string.Equals(reason, "FORBIDDEN", StringComparison.OrdinalIgnoreCase))
            {
                PluginServices.Instance?.Framework.RunOnTick(() =>
                {
                    PluginServices.Instance?.ToastGui.ShowError(
                        "You do not have permission to use that channel."
                    );
                });
            }
            else if (string.Equals(reason, "NOT_FOUND", StringComparison.OrdinalIgnoreCase))
            {
                PluginServices.Instance?.Framework.RunOnTick(() =>
                {
                    PluginServices.Instance?.ToastGui.ShowError(
                        "Selected channel is no longer configured."
                    );
                });
            }
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Warning(
                ex,
                "Failed to validate channel selection {ChannelId} ({Kind})",
                channelId,
                kind
            );
        }
    }

    private async Task HandleGuildChangedAsync(string normalizedGuildId, string changedKind)
    {
        try
        {
            await RevalidateChannelSelectionsAsync(normalizedGuildId, changedKind).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _services.Log.Warning(
                ex,
                $"Failed to revalidate channel selections after guild change to {normalizedGuildId}"
            );
        }

        try
        {
            await RestartWatchersAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _services.Log.Warning(ex, "Failed to restart watchers after guild change");
        }

        try
        {
            await _emojiManager.RefreshCustomAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _services.Log.Warning(ex, "Failed to refresh custom emoji after guild change");
        }
    }

    private async Task RevalidateChannelSelectionsAsync(string normalizedGuildId, string changedKind)
    {
        var normalizedChangedKind = ChannelKeyHelper.NormalizeKind(changedKind);
        var kindsToValidate = new[]
        {
            ChannelKind.Chat,
            ChannelKind.Event,
            ChannelKind.FcChat,
            ChannelKind.OfficerChat
        };

        foreach (var targetKind in kindsToValidate)
        {
            var normalizedTargetKind = ChannelKeyHelper.NormalizeKind(targetKind);
            if (string.Equals(normalizedTargetKind, normalizedChangedKind, StringComparison.Ordinal))
            {
                continue;
            }

            var selectedChannel = _channelSelection.GetChannel(targetKind, normalizedGuildId);
            if (string.IsNullOrWhiteSpace(selectedChannel))
            {
                continue;
            }

            await ValidateChannelSelectionAsync(targetKind, normalizedGuildId, selectedChannel, string.Empty)
                .ConfigureAwait(false);
        }
    }

    private async Task RestartWatchersAsync()
    {
        await _watcherRestartLock.WaitAsync().ConfigureAwait(false);
        try
        {
            StopWatchers();
            await StartWatchersAsync().ConfigureAwait(false);
        }
        finally
        {
            _watcherRestartLock.Release();
        }
    }

    private void StartWatchers()
    {
        _services.Log.Info("Starting watchers");
        _ = _settings.HardReloadIdentityAndStartAsync();
    }

    private void HandleTokenLinked()
    {
        _invalidTokenToastShown = false;
        try { _services.ChatGui.RemoveChatLinkHandler(InvalidTokenLinkCommandId); } catch { }
        _mainWindow.ResetLinkNotification();
        _mainWindow.SetNotePadReadOnly(false);
        StartWatchers();
    }

    private void HandleTokenUnlinked(string? reason)
    {
        void Execute()
        {
            StopWatchers();
            _mainWindow.IsOpen = false;
            _mainWindow.SetNotePadReadOnly(true);
            _mainWindow.NotifyLinkRequired();
            _settings.IsOpen = true;

            var hadOfficerAccess = _config.IsOfficerToken;
            _config.IsOfficerToken = false;
            _mainWindow.HasOfficerAccess = OfficerPermissions.HasAccess(_config);
            if (hadOfficerAccess)
            {
                _services.PluginInterface?.SavePluginConfig(_config);
            }

            if (IsAuthenticationFailure(reason))
            {
                ShowInvalidTokenToast();
            }
        }

        var framework = PluginServices.Instance?.Framework ?? _services.Framework;

        if (framework != null)
        {
            try
            {
                framework.RunOnTick(Execute);
                return;
            }
            catch (Exception ex)
            {
                var log = PluginServices.Instance?.Log ?? _services.Log;
                log?.Warning(ex, "Failed to marshal token unlink handling to framework thread");
            }
        }

        Execute();
    }

    private async Task StartWatchersAsync()
    {
        if (!_tokenManager.IsReady() || !ApiHelpers.ValidateApiBaseUrl(_config))
            return;

        var pingService = PingService.Instance ?? new PingService(_httpClient, _config, _tokenManager);
        HttpResponseMessage? response = null;
        try
        {
            response = await pingService.PingAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _services.Log.Warning(ex, "Failed to ping DemiCat backend while starting watchers");
        }

        if (response == null)
        {
            HandleWatcherStartupFailure(string.Empty);
            return;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
        {
            _services.Log.Warning($"Ping returned {(int)response.StatusCode} {response.StatusCode}; clearing token");
            _tokenManager.Clear("Invalid API key");
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            var statusText = $" ({(int)response.StatusCode} {response.StatusCode})";
            HandleWatcherStartupFailure(statusText);
            return;
        }

        if (_config.Requests)
        {
            _services.Log.Info("Starting request watcher");
            _requestWatcher.Start();
        }

        var hasOfficerAccess = OfficerPermissions.HasAccess(_config);
        if (_config.Events || _config.SyncedChat || hasOfficerAccess)
        {
            _services.Log.Info("Starting channel watcher");
            _ = _channelWatcher.Start();
        }

        if (_config.NotePadEnabled)
        {
            _services.Log.Info("Starting notepad service");
            _notePadService.Start();
        }

        if (_config.SyncedChat && _config.EnableFcChat)
        {
            _services.Log.Info("Starting chat window networking");
            _chatWindow.StartNetworking();
        }

        if (hasOfficerAccess)
        {
            if (!_officerWatcherRunning)
            {
                _services.Log.Info("Starting officer chat window networking");
                _officerChatWindow.StartNetworking();
                _officerWatcherRunning = true;
            }
        }
        else
        {
            _officerWatcherRunning = false;
        }

        if (_config.Events)
        {
            _services.Log.Info("Starting event watchers");
            _ = _ui.StartNetworking();
            _mainWindow.EventCreateWindow.StartNetworking();
            _mainWindow.TemplatesWindow.StartNetworking();
        }

        _services.Log.Info("Watchers started");
    }

    private static bool IsAuthenticationFailure(string? reason)
        => string.Equals(reason, "Invalid API key", StringComparison.Ordinal)
            || string.Equals(reason, "Authentication failed", StringComparison.Ordinal);

    private void ShowInvalidTokenToast()
    {
        if (_invalidTokenToastShown)
            return;

        var toastGui = _services.ToastGui;
        var chatGui = _services.ChatGui;

        if (toastGui == null || chatGui == null)
            return;

        if (_openConfigUi == null)
            return;

        _invalidTokenToastShown = true;

        try { chatGui.RemoveChatLinkHandler(InvalidTokenLinkCommandId); } catch { }

        DalamudLinkPayload linkPayload;
        try
        {
            linkPayload = chatGui.AddChatLinkHandler(
                InvalidTokenLinkCommandId,
                (_, _) =>
                {
                    try
                    {
                        var framework = _services.Framework;
                        if (framework != null)
                        {
                            _ = framework.RunOnTick(() => _openConfigUi());
                        }
                        else
                        {
                            _openConfigUi();
                        }
                    }
                    catch
                    {
                    }
                });
        }
        catch
        {
            return;
        }

        var message = new SeStringBuilder()
            .AddText("DemiCat sync is disabled because the stored API key is invalid.")
            .Add(new NewLinePayload())
            .Add(linkPayload)
            .AddUiForeground("Open settings", 45)
            .Add(RawPayload.LinkTerminator)
            .AddText(" to update your sync key.")
            .Build();

        var options = new QuestToastOptions
        {
            Position = QuestToastPosition.Centre,
            DisplayCheckmark = true,
            PlaySound = true
        };

        toastGui.ShowQuest(message, options);
    }

    private void HandleWatcherStartupFailure(string statusDetails)
    {
        var suffix = string.IsNullOrEmpty(statusDetails) ? string.Empty : statusDetails;
        var message = $"Unable to reach the DemiCat backend{suffix}. Watchers were not started.";
        _services.Log.Warning(message);
        PluginServices.Instance?.ToastGui.ShowError(message);
    }

    private void StopWatchers()
    {
        _services.Log.Info("Stopping watchers");
        _requestWatcher.Stop();
        _channelWatcher.Stop();
        _notePadService.Stop();
        _chatWindow.StopNetworking();
        _officerChatWindow.StopNetworking();
        _officerWatcherRunning = false;
        _presenceService?.SetPresenceReady(false);
        _presenceService?.Stop();
        _ui.StopNetworking();
        _mainWindow.TemplatesWindow.StopNetworking();
        MembershipCache.Reset();
        _services.Log.Info("Watchers stopped");
    }

    private async Task<bool> RefreshRoles(IPluginLog log)
    {
        if (!_tokenManager.IsReady() || !ApiHelpers.ValidateApiBaseUrl(_config))
            return false;

        try
        {
            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/roles";
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            ApiHelpers.AddAuthHeader(request, _tokenManager);
            log.Info($"Requesting roles from {url}");
            var response = await _httpClient.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                log.Error($"Failed to fetch roles: {response.StatusCode}. Response Body: {responseBody}");
                _tokenManager.Clear("Authentication failed");
                return false;
            }
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                log.Error($"Failed to fetch roles: {response.StatusCode}. Response Body: {responseBody}");
                PluginServices.Instance?.ToastGui.ShowError("Forbidden – check API key/roles");
                return false;
            }
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                log.Error($"Failed to fetch roles: {response.StatusCode}. Response Body: {responseBody}");
                return false;
            }

            var stream = await response.Content.ReadAsStreamAsync();
            var dto = await JsonSerializer.DeserializeAsync<RolesDto>(stream) ?? new RolesDto();
            log.Info($"Roles received: {string.Join(", ", dto.Roles)}");

            var channelUrl = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/channels";
            var channelRequest = new HttpRequestMessage(HttpMethod.Get, channelUrl);
            ApiHelpers.AddAuthHeader(channelRequest, _tokenManager);
            List<ChannelDto> chatChannels = new();
            var channelsFetched = false;

            try
            {
                var channelResponse = await _httpClient.SendAsync(channelRequest);
                if (channelResponse.StatusCode == HttpStatusCode.Unauthorized)
                {
                    var responseBody = await channelResponse.Content.ReadAsStringAsync();
                    log.Error($"Failed to fetch channels: {channelResponse.StatusCode}. Response Body: {responseBody}");
                    _tokenManager.Clear("Authentication failed");
                    return false;
                }
                if (channelResponse.StatusCode == HttpStatusCode.Forbidden)
                {
                    var responseBody = await channelResponse.Content.ReadAsStringAsync();
                    log.Error($"Failed to fetch channels: {channelResponse.StatusCode}. Response Body: {responseBody}");
                    PluginServices.Instance?.ToastGui.ShowError("Forbidden – check API key/roles");
                    return false;
                }

                if (channelResponse.IsSuccessStatusCode)
                {
                    var channelStream = await channelResponse.Content.ReadAsStreamAsync();
                    var channelsDto = await JsonSerializer.DeserializeAsync<ChannelListDto>(channelStream);
                    if (channelsDto == null)
                    {
                        log.Error("Failed to deserialize channels response.");
                    }
                    else
                    {
                        chatChannels = channelsDto.Chat ?? new List<ChannelDto>();
                        foreach (var channel in chatChannels)
                        {
                            channel.EnsureKind(ChannelKind.FcChat);
                        }

                        channelsFetched = true;
                    }
                }
                else
                {
                    var responseBody = await channelResponse.Content.ReadAsStringAsync();
                    log.Error($"Failed to fetch channels: {channelResponse.StatusCode}. Response Body: {responseBody}");
                }
            }
            catch (Exception ex)
            {
                log.Error(ex, "Error fetching channels.");
            }

            var hasChat = chatChannels.Count > 0;

            _ = _services.Framework.RunOnTick(() =>
            {
                var officerWatcherWasRunning = _officerWatcherRunning;
                var channelWatcherWasRunning = _channelWatcher.IsRunning;
                var requestWatcherWasRunning = _requestWatcher.IsRunning;
                var chatWasActive = _config.SyncedChat && _config.EnableFcChat;

                dto.Roles.RemoveAll(r => r == "chat");
                _config.Roles = dto.Roles;
                var hasOfficerAccess = OfficerPermissions.HasAccess(_config);
                _mainWindow.HasOfficerAccess = hasOfficerAccess;

                var stopChat = false;
                var startChat = false;

                if (channelsFetched)
                {
                    _config.EnableFcChat = hasChat;

                    _chatWindow.ChannelsLoaded = false;
                    _officerChatWindow.ChannelsLoaded = false;

                    var chatIsActiveAfterUpdate = _config.SyncedChat && _config.EnableFcChat;
                    stopChat = chatWasActive && !chatIsActiveAfterUpdate;
                    startChat = !chatWasActive && chatIsActiveAfterUpdate;
                }
                else
                {
                    log.Warning("Skipping FC chat updates because channels could not be fetched.");
                }

                var shouldRunChannelWatcher = _config.Events || _config.SyncedChat || hasOfficerAccess;
                var shouldRunRequestWatcher = _config.Requests;

                var stopOfficer = officerWatcherWasRunning && !hasOfficerAccess;
                var startOfficer = !officerWatcherWasRunning && hasOfficerAccess;
                var stopChannelWatcher = channelWatcherWasRunning && !shouldRunChannelWatcher;
                var startChannelWatcher = !channelWatcherWasRunning && shouldRunChannelWatcher;
                var stopRequestWatcher = requestWatcherWasRunning && !shouldRunRequestWatcher;
                var startRequestWatcher = !requestWatcherWasRunning && shouldRunRequestWatcher;

                _services.PluginInterface.SavePluginConfig(_config);
                MembershipCache.Reset();

                _ = Task.Run(async () =>
                {
                    await _watcherRestartLock.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (stopRequestWatcher)
                        {
                            _services.Log.Info("Stopping request watcher");
                            _requestWatcher.Stop();
                        }

                        if (stopChannelWatcher)
                        {
                            _services.Log.Info("Stopping channel watcher");
                            _channelWatcher.Stop();
                        }

                        if (stopOfficer)
                        {
                            _services.Log.Info("Stopping officer chat window networking");
                            _officerChatWindow.StopNetworking();
                            _officerWatcherRunning = false;
                        }

                        if (stopChat)
                        {
                            _services.Log.Info("Stopping chat window networking");
                            _chatWindow.StopNetworking();
                            _presenceService?.SetPresenceReady(false);
                            _presenceService?.Stop();
                        }

                        if (startChannelWatcher)
                        {
                            _services.Log.Info("Starting channel watcher");
                            await _channelWatcher.Start().ConfigureAwait(false);
                        }

                        if (startRequestWatcher)
                        {
                            _services.Log.Info("Starting request watcher");
                            _requestWatcher.Start();
                        }

                        if (startChat)
                        {
                            _services.Log.Info("Starting chat window networking");
                            _chatWindow.StartNetworking();
                        }

                        if (startOfficer)
                        {
                            _services.Log.Info("Starting officer chat window networking");
                            _officerChatWindow.StartNetworking();
                            _officerWatcherRunning = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        _services.Log.Error(ex, "Error updating watchers after refreshing roles.");
                    }
                    finally
                    {
                        _watcherRestartLock.Release();
                        _ = _services.Framework.RunOnTick(() =>
                        {
                            _mainWindow.HasOfficerAccess = OfficerPermissions.HasAccess(_config);
                        });
                    }
                });
            });

            await RoleCache.Refresh(_httpClient, _config);
            return true;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Error refreshing roles.");
            return false;
        }
    }

    private class RolesDto
    {
        [JsonPropertyName("roles")]
        public List<string> Roles { get; set; } = new();
    }

    private class ChannelListDto
    {
        [JsonPropertyName(ChannelKind.FcChat)] public List<ChannelDto> Chat { get; set; } = new();
    }
}

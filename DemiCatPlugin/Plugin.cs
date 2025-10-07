using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DiscordHelper;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.Toast;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Networking.Http;
using DemiCatPlugin.Avatars;
using DemiCatPlugin.Emoji;
using Microsoft.CSharp.RuntimeBinder;
using StbImageSharp;

namespace DemiCatPlugin;

public class Plugin : IDalamudPlugin
{
    public string Name => "DemiCat";

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IUiBuilder _uiBuilder;
    private readonly IPluginLog _log;

    private readonly Config _config;
    private readonly TokenManager _tokenManager;
    private readonly SettingsWindow _settings;
    private readonly WindowSystem _windows = new("DemiCat");
    private bool _developerWindowAdded;

    private const uint InvalidTokenLinkCommandId = 0x44434B49;
    private const string MewCommand = "/mew";
    private const string MewCommandHelpMessage = "Open the DemiCat main window.";

    private PluginServices? _services;
    private PluginServices Services => _services
        ?? throw new InvalidOperationException("Plugin services are not initialized.");
    private UiRenderer _ui = null!;
    private AvatarCache _avatarCache = null!;
    private ChatWindow _chatWindow = null!;
    private OfficerChatWindow _officerChatWindow = null!;
    private DiscordPresenceService? _presenceService;
    private MainWindow _mainWindow = null!;
    private ChannelWatcher _channelWatcher = null!;
    private RequestWatcher _requestWatcher = null!;
    private NotePadService _notePadService = null!;
    private NotePadWindow _notePadWindow = null!;
    private ChannelSelectionService _channelSelection = null!;
    private EmojiManager _emojiManager = null!;

    private readonly HappyEyeballsCallback _happyEyeballsCallback = new();

    private HttpClient? _httpClient;
    private HttpClient HttpClient => _httpClient
        ?? throw new InvalidOperationException("HTTP client has not been initialized.");

    private HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 8,
            ConnectCallback = _happyEyeballsCallback.ConnectCallback
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("DemiCat/1.0 (+Dalamud)");
        return client;
    }

    private ChannelService _channelService = null!;
    private Action? _openMainUi;
    private bool _invalidTokenToastShown;
    private bool? _savedDockVisibilityPreference;
    private readonly SemaphoreSlim _watchLock = new(1, 1);
    private IEmojiFontHandle? _emojiFontHandle;
    private CommandInfo _mewCommandInfo = null!;

    private const string ManagedFontAtlasAssemblyName = "Dalamud.Interface.ManagedFontAtlas";
    private const string ManagedFontAtlasGlyphRangeBuilderTypeName =
        "Dalamud.Interface.ManagedFontAtlas.FluentGlyphRangeBuilder";
    private const string ManagedFontAtlasSafeFontConfigTypeName =
        "Dalamud.Interface.ManagedFontAtlas.SafeFontConfig";

    private bool _initialized;
    private bool _initializationAttempted;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        _pluginInterface = pluginInterface ?? throw new ArgumentNullException(nameof(pluginInterface));
        _uiBuilder = pluginInterface.UiBuilder ?? throw new ArgumentNullException(nameof(pluginInterface.UiBuilder));
        _log = pluginInterface.Create<IPluginLog>() ?? throw new InvalidOperationException("Failed to acquire plugin log.");

        _config = pluginInterface.GetPluginConfig() as Config ?? new Config();
        _tokenManager = new TokenManager(pluginInterface);

        _settings = new SettingsWindow(pluginInterface, _config, _tokenManager, _log);
        _settings.Plugin = this;
        _windows.AddWindow(_settings);

        _uiBuilder.Draw += OnDraw;
        _uiBuilder.Draw += _windows.Draw;
        _uiBuilder.OpenConfigUi += HandleOpenConfig;
    }

    private void OnDraw()
    {
        if (!_initializationAttempted)
        {
            _initializationAttempted = true;
            Initialize();
        }

        if (_initialized)
        {
            if (!_tokenManager.IsReady())
            {
                _savedDockVisibilityPreference ??= _config.DockVisible;
            }

            DrawOverlay();
        }
    }

    private void Initialize()
    {
        var pluginInterface = _pluginInterface;

        try
        {
            _services ??= pluginInterface.Create<PluginServices>()
                ?? throw new InvalidOperationException("Failed to initialize plugin services.");
            if (Services.PluginInterface == null || Services.Log == null)
                throw new InvalidOperationException("Failed to initialize plugin services.");

            _tokenManager.Load();
            _settings.SynchronizeTokenState();

            var oldVersion = _config.Version;
            var originalApiBaseUrl = _config.ApiBaseUrl;
            _config.Migrate();
            var sanitizedApiBaseUrl = Config.SanitizeApiBaseUrl(_config.ApiBaseUrl);
            var apiBaseUrlChanged = !string.Equals(originalApiBaseUrl, sanitizedApiBaseUrl, StringComparison.Ordinal);
            _config.ApiBaseUrl = sanitizedApiBaseUrl;
            var rolesRemoved = _config.Roles.RemoveAll(r => r == "chat") > 0;
            if (rolesRemoved || _config.Version != oldVersion || apiBaseUrlChanged)
                Services.PluginInterface.SavePluginConfig(_config);

            RequestStateService.Load(_config);

            _httpClient = CreateHttpClient();

            WebTextureCache.FetchOverride = FetchWebTexture;

            PingService.Instance = new PingService(HttpClient, _config, _tokenManager);

            _channelSelection = new ChannelSelectionService(_config);
            _emojiManager = new EmojiManager(HttpClient, _tokenManager, _config);
            _emojiFontHandle = InitializeEmojiFont();
            _emojiManager.EmojiFontHandle = _emojiFontHandle;
            _ui = new UiRenderer(_config, HttpClient, _channelSelection, _emojiManager, _tokenManager);

            _settings.ConfigureServices(
                HttpClient,
                () => RefreshRoles(Services.Log),
                _ui.StartNetworking);

            RegisterDeveloperWindow();

            _presenceService = _config.SyncedChat && _config.EnableFcChat
            ? new DiscordPresenceService(_config, HttpClient, _tokenManager)
            : null;

            _channelService = new ChannelService(_config, HttpClient, _tokenManager);
            var textureProvider = Services.TextureProvider;
            _avatarCache = new AvatarCache(textureProvider, HttpClient);
            _chatWindow = new FcChatWindow(_config, HttpClient, _presenceService, _tokenManager, _channelService, _channelSelection, _avatarCache, _emojiManager);
            _officerChatWindow = new OfficerChatWindow(_config, HttpClient, _presenceService, _tokenManager, _channelService, _channelSelection, _avatarCache, _emojiManager);

            _presenceService?.Reset();

            _notePadService = new NotePadService(_config, HttpClient, _tokenManager);
            _notePadWindow = new NotePadWindow(_config, _notePadService);

            _mainWindow = new MainWindow(
                _config,
                _ui,
                _chatWindow,
                _officerChatWindow,
                _settings,
                HttpClient,
                _channelService,
                _channelSelection,
                _emojiManager,
                _notePadWindow,
                _tokenManager,
                () => _tokenManager.IsReady(),
                () => _tokenManager.State == LinkState.Linked
            );

            _channelWatcher = new ChannelWatcher(_config, _ui, _mainWindow.EventCreateWindow, _mainWindow.TemplatesWindow, _chatWindow, _officerChatWindow, _tokenManager, HttpClient, _channelService);
            _requestWatcher = new RequestWatcher(_config, HttpClient, _tokenManager);

            _channelSelection.ChannelChanged += HandleChannelSelectionValidation;

            _settings.MainWindow = _mainWindow;
            _settings.ChatWindow = _chatWindow;
            _settings.OfficerChatWindow = _officerChatWindow;
            _settings.ChannelWatcher = _channelWatcher;
            _settings.RequestWatcher = _requestWatcher;
            _settings.NotePadService = _notePadService;

            _mainWindow.HasOfficerAccess = OfficerPermissions.HasAccess(_config);
            _mainWindow.SetNotePadReadOnly(!_tokenManager.IsReady());

            _windows.AddWindow(_mainWindow);
            _mainWindow.IsOpen = _config.DockVisible;

            _ = RoleCache.EnsureLoaded(HttpClient, _config, _tokenManager);

            _openMainUi = () =>
            {
                if (_mainWindow != null)
                    _mainWindow.IsOpen = true;
            };
            _uiBuilder.OpenMainUi += _openMainUi;

            _mewCommandInfo = new CommandInfo(OnMewCommand)
            {
                HelpMessage = MewCommandHelpMessage
            };
            Services.CommandManager.AddHandler(MewCommand, _mewCommandInfo);

            _tokenManager.OnLinked += HandleTokenLinked;
            _tokenManager.OnUnlinked += HandleTokenUnlinked;

            if (_tokenManager.IsReady())
                HandleTokenLinked();

            Services.Log.Info("DemiCat loaded.");
            _initialized = true;
        }
        catch (Exception ex)
        {
            try
            {
                _services?.Log.Error(ex, "Failed to initialize DemiCat.");
            }
            catch
            {
                // ignored
            }

            try
            {
                PluginServices.Instance?.ToastGui.ShowError("Failed to initialize DemiCat. Check /xllog for details.");
            }
            catch
            {
                // ignored
            }
        }
    }

    private void HandleOpenConfig()
    {
        _log.Info("Config open requested");
        _settings.IsOpen = true;
    }

    private void RegisterDeveloperWindow()
    {
        if (_developerWindowAdded)
            return;

        if (_settings.DeveloperWindow is { } developerWindow)
        {
            _windows.AddWindow(developerWindow);
            _developerWindowAdded = true;
        }
    }

    public void Dispose()
    {
        _uiBuilder.Draw -= OnDraw;
        _uiBuilder.Draw -= _windows.Draw;
        _uiBuilder.OpenConfigUi -= HandleOpenConfig;
        _windows.RemoveAllWindows();

        if (_openMainUi != null)
        {
            _uiBuilder.OpenMainUi -= _openMainUi;
            _openMainUi = null;
        }

        if (!_initialized)
        {
            _settings.Dispose();
            return;
        }

        WebTextureCache.FetchOverride = null;
        try
        {
            RunOnFrameworkAsync(WebTextureCache.Clear).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, "Failed to clear web texture cache on framework thread.");
            WebTextureCache.Clear();
        }

        if (_emojiManager != null)
        {
            _emojiManager.EmojiFontHandle = null;
        }
        _emojiFontHandle?.Dispose();

        if (_services != null)
        {
            Services.CommandManager.RemoveHandler(MewCommand);
        }

        _tokenManager.OnLinked -= HandleTokenLinked;
        _tokenManager.OnUnlinked -= HandleTokenUnlinked;

        try { _services?.ChatGui.RemoveChatLinkHandler(InvalidTokenLinkCommandId); } catch { }

        if (_channelSelection != null)
        {
            _channelSelection.ChannelChanged -= HandleChannelSelectionValidation;
        }

        _channelWatcher?.Dispose();
        _requestWatcher?.Dispose();
        _notePadWindow?.Dispose();
        _notePadService?.Dispose();
        _presenceService?.Dispose();
        _chatWindow?.Dispose();
        _officerChatWindow?.Dispose();
        _mainWindow?.Dispose();
        if (_ui != null)
        {
            _ui.DisposeAsync().GetAwaiter().GetResult();
        }
        _settings.Dispose();
        _avatarCache?.Dispose();
        _emojiManager?.Dispose();
        _httpClient?.Dispose();
        _happyEyeballsCallback.Dispose();
        if (PluginServices.Instance != null)
        {
            PluginServices.Instance.ProgressOverlay = null;
        }
    }

    private void OnMewCommand(string command, string arguments)
    {
        if (!_tokenManager.IsReady())
        {
            _settings.IsOpen = true;
            return;
        }

        if (_mainWindow != null)
            _mainWindow.IsOpen = !_mainWindow.IsOpen;
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
            response = await HttpClient
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Services.Log.Warning(
                    $"Failed to download texture {url}: {(int)response.StatusCode} {response.StatusCode}. Falling back to text label.");
                await RunOnFrameworkAsync(() => onReady(null)).ConfigureAwait(false);
                return;
            }

            payload = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }
        catch (TaskCanceledException ex)
        {
            Services.Log.Warning(ex, $"Texture download timed out for {url}. Falling back to text label.");
            await RunOnFrameworkAsync(() => onReady(null)).ConfigureAwait(false);
            return;
        }
        catch (HttpRequestException ex)
        {
            Services.Log.Warning(ex, $"Failed to download texture {url}. Falling back to text label.");
            await RunOnFrameworkAsync(() => onReady(null)).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex, $"Unexpected error downloading texture {url}. Falling back to text label.");
            await RunOnFrameworkAsync(() => onReady(null)).ConfigureAwait(false);
            return;
        }
        finally
        {
            response?.Dispose();
        }

        if (payload == null || payload.Length == 0)
        {
            Services.Log.Warning($"Texture download for {url} returned no data. Falling back to text label.");
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
            Services.Log.Error(ex, $"Failed to decode texture {url}. Falling back to text label.");
            await RunOnFrameworkAsync(() => onReady(null)).ConfigureAwait(false);
            return;
        }

        try
        {
            await RunOnFrameworkAsync(() =>
            {
                try
                {
                    var wrap = Services.TextureProvider.CreateFromRaw(
                        RawImageSpecification.Rgba32(image.Width, image.Height),
                        image.Data);
                    var texture = new ForwardingSharedImmediateTexture(wrap);
                    WebTextureCache.Set(url, texture);
                    onReady(texture);
                }
                catch (Exception ex)
                {
                    Services.Log.Error(ex, $"Failed to create texture for {url}. Falling back to text label.");
                    WebTextureCache.Set(url, null);
                    onReady(null);
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex, $"Failed to finalize texture load for {url}. Falling back to text label.");
            await RunOnFrameworkAsync(() => onReady(null)).ConfigureAwait(false);
        }
    }

    private Task RunOnFrameworkAsync(Action action)
    {
        var framework = PluginServices.Instance?.Framework ?? Services.Framework;
        if (framework != null)
            return framework.RunOnTick(action);

        action();
        return Task.CompletedTask;
    }

    private Task SetPresenceAsync(bool enabled)
    {
        return RunOnFrameworkAsync(() =>
        {
            if (_presenceService == null)
            {
                return;
            }

            if (!enabled)
            {
                _presenceService.SetPresenceReady(false);
                _presenceService.Stop();
                return;
            }

            _presenceService.Reload();
            _presenceService.Reset();
            _presenceService.SetPresenceReady(true);
        });
    }

    private IEmojiFontHandle? InitializeEmojiFont()
    {
        try
        {
            var fontPath = ResolveEmojiFontPath();
            if (fontPath == null)
            {
                return null;
            }

            var atlas = TryGetManagedFontAtlas();
            if (atlas == null)
            {
                return null;
            }

            var fontSize = TryGetDefaultFontSize();
            var managedFontAtlasAssembly = TryLoadManagedFontAtlasAssembly();
            if (managedFontAtlasAssembly == null)
            {
                return null;
            }

            var glyphRangeBuilderType = managedFontAtlasAssembly.GetType(
                ManagedFontAtlasGlyphRangeBuilderTypeName);
            var safeFontConfigType = managedFontAtlasAssembly.GetType(
                ManagedFontAtlasSafeFontConfigTypeName);

            if (glyphRangeBuilderType == null || safeFontConfigType == null)
            {
                Services.Log.Info(
                    "Managed font atlas types unavailable; skipping emoji font initialization.");
                return null;
            }

            object? handle = CreateManagedFontHandle(atlas, fontPath, fontSize,
                glyphRangeBuilderType, safeFontConfigType);

            if (handle == null)
            {
                Services.Log.Info(
                    "Managed font atlas could not create an emoji font handle; falling back to default glyphs.");
                return null;
            }

            Services.Log.Info("Loaded emoji font from {Path}.", fontPath);
            return new ManagedFontAtlasEmojiFontHandle(handle);
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, "Failed to load emoji font. Unicode emoji will use fallback glyphs.");
            return null;
        }
    }

    private string? ResolveEmojiFontPath()
    {
        var directory = Services.PluginInterface.AssemblyLocation.Directory;
        if (directory == null)
        {
            return null;
        }

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
                return candidate;
            }
        }

        var paths = string.Join(", ", candidates);
        const string message =
            "DemiCat could not find its emoji font. Channel icons will fall back to text until the font is restored.";

        Services.Log.Info(
            "Emoji font not found at any known path ({Paths}). Unicode emoji will use fallback glyphs.",
            paths
        );

        PluginServices.Instance?.ToastGui.ShowError(message);
        return null;
    }

    private object? TryGetManagedFontAtlas()
    {
        var property = _uiBuilder.GetType().GetProperty("FontAtlas");
        var atlas = property?.GetValue(_uiBuilder);
        if (atlas == null)
        {
            Services.Log.Info("Managed font atlas not available; skipping emoji font initialization.");
        }

        return atlas;
    }

    private float TryGetDefaultFontSize()
    {
        var property = _uiBuilder.GetType().GetProperty("FontDefaultSizePx");
        var value = property?.GetValue(_uiBuilder);
        return value switch
        {
            float f => f,
            double d => (float)d,
            _ => 17f,
        };
    }

    private Assembly? TryLoadManagedFontAtlasAssembly()
    {
        var existing = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(
            asm => string.Equals(asm.GetName().Name, ManagedFontAtlasAssemblyName, StringComparison.Ordinal));
        if (existing != null)
        {
            return existing;
        }

        try
        {
            return Assembly.Load(ManagedFontAtlasAssemblyName);
        }
        catch (FileNotFoundException)
        {
            Services.Log.Info(
                "Dalamud.Interface.ManagedFontAtlas assembly missing; emoji font support disabled.");
            return null;
        }
        catch (Exception ex)
        {
            Services.Log.Warning(
                ex,
                "Failed to load Dalamud.Interface.ManagedFontAtlas; emoji font support disabled.");
            return null;
        }
    }

    private object? CreateManagedFontHandle(object atlas, string fontPath, float fontSize,
        Type glyphRangeBuilderType, Type safeFontConfigType)
    {
        dynamic dynamicAtlas = atlas;

        object? CreateGlyphRanges()
        {
            object? builder = Activator.CreateInstance(glyphRangeBuilderType);
            if (builder == null)
            {
                return null;
            }

            var withMethod = glyphRangeBuilderType.GetMethod("With", new[] { typeof(int), typeof(int) });
            if (withMethod == null)
            {
                return null;
            }

            object? Apply(object? target, int min, int max)
            {
                return target == null ? null : withMethod.Invoke(target, new object[] { min, max });
            }

            builder = Apply(builder, 0x2000, 0x27FF);
            builder = Apply(builder, 0x2B00, 0x2BFF);
            builder = Apply(builder, 0x1F000, 0x1FAFF);
            builder = Apply(builder, 0x200D, 0x200D);
            builder = Apply(builder, 0x20E3, 0x20E3);
            builder = Apply(builder, 0xFE0E, 0xFE0F);

            if (builder == null)
            {
                return null;
            }

            var buildMethod = glyphRangeBuilderType.GetMethod("Build", Type.EmptyTypes);
            return buildMethod?.Invoke(builder, Array.Empty<object>());
        }

        object? CreateFontConfig(object? baseFont, object glyphRanges)
        {
            var config = Activator.CreateInstance(safeFontConfigType);
            if (config == null)
            {
                return null;
            }

            safeFontConfigType.GetProperty("SizePx")?.SetValue(config, fontSize);
            if (baseFont != null)
            {
                safeFontConfigType.GetProperty("MergeFont")?.SetValue(config, baseFont);
            }
            safeFontConfigType.GetProperty("PixelSnapH")?.SetValue(config, true);
            safeFontConfigType.GetProperty("GlyphRanges")?.SetValue(config, glyphRanges);

            var rawProperty = safeFontConfigType.GetProperty("Raw");
            var raw = rawProperty?.GetValue(config);
            var rawType = raw?.GetType();
            var flagsProperty = rawType?.GetProperty("FontBuilderFlags");
            if (raw != null && flagsProperty != null)
            {
                var current = flagsProperty.GetValue(raw);
                var numeric = current != null ? Convert.ToUInt32(current) : 0u;
                numeric |= 1u << 8; // ImGuiFreeTypeBuilderFlags_LoadColor
                var boxed = Convert.ChangeType(numeric, flagsProperty.PropertyType);
                flagsProperty.SetValue(raw, boxed);
            }

            return config;
        }

        try
        {
            var atlasType = atlas.GetType();
            var addFontMethod = atlasType.GetMethod("AddFontFromFile", new[] { typeof(string), safeFontConfigType });
            if (addFontMethod != null)
            {
                object? baseFont = null;
                try
                {
                    baseFont = atlasType.GetProperty("DefaultFont")?.GetValue(atlas);
                    var sizeProperty = baseFont?.GetType().GetProperty("SizePx");
                    sizeProperty?.SetValue(baseFont, fontSize);
                }
                catch
                {
                    baseFont = null;
                }

                var glyphRanges = CreateGlyphRanges();
                if (glyphRanges != null)
                {
                    var config = CreateFontConfig(baseFont, glyphRanges);
                    if (config != null)
                    {
                        var handle = addFontMethod.Invoke(atlas, new[] { fontPath, config });
                        if (handle != null)
                        {
                            return handle;
                        }
                    }
                }
            }

            return dynamicAtlas.NewDelegateFontHandle((Action<dynamic>)(toolkit =>
            {
                toolkit.OnPreBuild((Action<dynamic>)(pre =>
                {
                    dynamic baseFont = pre.AddDalamudDefaultFont(fontSize);
                    var glyphRanges = CreateGlyphRanges();
                    if (glyphRanges == null)
                    {
                        return;
                    }

                    var config = CreateFontConfig(baseFont, glyphRanges);
                    if (config == null)
                    {
                        return;
                    }

                    pre.AddFontFromFile(fontPath, config);
                    pre.Font = baseFont;
                }));
            }));
        }
        catch (RuntimeBinderException ex)
        {
            Services.Log.Warning(ex, "Managed font atlas APIs unavailable; skipping emoji font initialization.");
            return null;
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, "Failed to create managed font handle.");
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
                    ChannelWatcher.Instance?.InvalidateCache();
                    Services.PluginInterface.SavePluginConfig(_config);

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
                Services.Log.Warning(
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
            Services.Log.Warning(ex, "Failed to restart watchers after guild change");
        }

        try
        {
            await _emojiManager.RefreshCustomAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, "Failed to refresh custom emoji after guild change");
        }
    }

    private async Task RevalidateChannelSelectionsAsync(string normalizedGuildId, string changedKind)
    {
        var normalizedChangedKind = ChannelKeyHelper.NormalizeKind(changedKind);
        var kindsToValidate = new[]
        {
            ChannelKind.Chat,
            ChannelKind.Event,
            ChannelKind.Requests,
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

    internal Task WithWatchLock(Func<Task> action) => WithWatchLockAsync(action);

    private async Task WithWatchLockAsync(Func<Task> action)
    {
        await _watchLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            _watchLock.Release();
        }
    }

    internal Task RestartWatchersAsync() =>
        WithWatchLock(async () =>
        {
            await StopWatchersAsync().ConfigureAwait(false);
            await StartWatchersAsync().ConfigureAwait(false);
        });

    private void StartWatchers()
    {
        Services.Log.Info("Starting watchers");
        _ = _settings.HardReloadIdentityAndStartAsync();
    }

    private void HandleTokenLinked()
    {
        _invalidTokenToastShown = false;
        try { _services?.ChatGui.RemoveChatLinkHandler(InvalidTokenLinkCommandId); } catch { }

        var desiredDockVisibility = _savedDockVisibilityPreference ?? _config.DockVisible;
        _savedDockVisibilityPreference = null;
        _mainWindow.IsOpen = desiredDockVisibility;

        _mainWindow.SetNotePadReadOnly(false);
        StartWatchers();
    }

    private void HandleTokenUnlinked(string? reason)
    {
        async Task ExecuteAsync()
        {
            await WithWatchLock(StopWatchersAsync).ConfigureAwait(false);
            _mainWindow.SetNotePadReadOnly(true);

            var savedDockVisibility = _config.DockVisible;
            _savedDockVisibilityPreference = savedDockVisibility;

            _mainWindow.CloseDockForUnlink();

            if (_config.DockVisible != savedDockVisibility)
            {
                _config.DockVisible = savedDockVisibility;
                Services.PluginInterface?.SavePluginConfig(_config);
            }

            var hadOfficerAccess = _config.IsOfficerToken;
            _config.IsOfficerToken = false;
            _mainWindow.HasOfficerAccess = OfficerPermissions.HasAccess(_config);
            if (hadOfficerAccess)
            {
                Services.PluginInterface?.SavePluginConfig(_config);
            }

            if (IsAuthenticationFailure(reason))
            {
                ShowInvalidTokenToast();
            }
        }

        var framework = PluginServices.Instance?.Framework ?? Services.Framework;

        if (framework != null)
        {
            try
            {
                framework.RunOnTick(ExecuteAsync);
                return;
            }
            catch (Exception ex)
            {
                var log = PluginServices.Instance?.Log ?? Services.Log;
                log?.Warning(ex, "Failed to marshal token unlink handling to framework thread");
            }
        }

        ExecuteAsync().GetAwaiter().GetResult();
    }

    internal async Task StartWatchersAsync()
    {
        if (!_tokenManager.IsReady() || !ApiHelpers.ValidateApiBaseUrl(_config))
            return;

        var pingService = PingService.Instance ?? new PingService(HttpClient, _config, _tokenManager);
        HttpResponseMessage? response = null;
        try
        {
            response = await pingService.PingAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, "Failed to ping DemiCat backend while starting watchers");
        }

        if (response == null)
        {
            await SetPresenceAsync(false).ConfigureAwait(false);
            HandleWatcherStartupFailure(string.Empty);
            return;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
        {
            Services.Log.Warning($"Ping returned {(int)response.StatusCode} {response.StatusCode}; clearing token");
            _tokenManager.Clear("Invalid API key");
            await SetPresenceAsync(false).ConfigureAwait(false);
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            var statusText = $" ({(int)response.StatusCode} {response.StatusCode})";
            await SetPresenceAsync(false).ConfigureAwait(false);
            HandleWatcherStartupFailure(statusText);
            return;
        }

        if (_config.Requests)
        {
            Services.Log.Info("Starting request watcher");
            _requestWatcher.Start();
        }

        var hasOfficerAccess = OfficerPermissions.HasAccess(_config);
        if (_config.Events || _config.SyncedChat || hasOfficerAccess)
        {
            Services.Log.Info("Starting channel watcher");
            await _channelWatcher.Start().ConfigureAwait(false);
        }

        if (_config.NotePadEnabled)
        {
            Services.Log.Info("Starting notepad service");
            _notePadService.Start();
        }

        var enableFcChat = _config.SyncedChat && _config.EnableFcChat;
        if (enableFcChat)
        {
            Services.Log.Info("Starting chat window networking");
            _chatWindow.StartNetworking();
        }
        else
        {
            _chatWindow.StopNetworking();
        }

        if (hasOfficerAccess)
        {
            Services.Log.Info("Starting officer chat window networking");
            await _officerChatWindow.StartNetworkingAsync().ConfigureAwait(false);
        }
        else
        {
            await _officerChatWindow.StopNetworkingAsync().ConfigureAwait(false);
        }

        if (_config.Events)
        {
            Services.Log.Info("Starting event watchers");
            await _ui.StartNetworking().ConfigureAwait(false);
            _mainWindow.EventCreateWindow.StartNetworking();
            _mainWindow.TemplatesWindow.StartNetworking();
        }

        var shouldEnablePresence = enableFcChat || hasOfficerAccess;
        await SetPresenceAsync(shouldEnablePresence).ConfigureAwait(false);

        Services.Log.Info("Watchers started");
    }

    private static bool IsAuthenticationFailure(string? reason)
        => string.Equals(reason, "Invalid API key", StringComparison.Ordinal)
            || string.Equals(reason, "Authentication failed", StringComparison.Ordinal);

    private void ShowInvalidTokenToast()
    {
        if (_invalidTokenToastShown)
            return;

        var toastGui = Services.ToastGui;
        var chatGui = Services.ChatGui;

        if (toastGui == null || chatGui == null)
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
                        var framework = Services.Framework;
                        if (framework != null)
                        {
                            _ = framework.RunOnTick(HandleOpenConfig);
                        }
                        else
                        {
                            HandleOpenConfig();
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
        Services.Log.Warning(message);
        PluginServices.Instance?.ToastGui.ShowError(message);
    }

    internal async Task StopWatchersAsync()
    {
        if (!_initialized)
        {
            return;
        }

        Services.Log.Info("Stopping watchers");

        await SetPresenceAsync(false).ConfigureAwait(false);

        _requestWatcher.Stop();
        _channelWatcher.Stop();
        _notePadService.Stop();
        _chatWindow.StopNetworking();
        await _officerChatWindow.StopNetworkingAsync().ConfigureAwait(false);
        _ui.StopNetworking();
        _mainWindow.TemplatesWindow.StopNetworking();
        MembershipCache.Reset();

        Services.Log.Info("Watchers stopped");
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
            var response = await HttpClient.SendAsync(request);
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
                var channelResponse = await HttpClient.SendAsync(channelRequest);
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

            _ = Services.Framework.RunOnTick(() =>
            {
                var officerWatcherWasRunning = _officerChatWindow.IsRunning;
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
                    if (!hasChat)
                    {
                        _config.EnableFcChat = false;
                        _config.EnableFcChatUserSet = false;
                    }
                    else if (!_config.EnableFcChatUserSet)
                    {
                        _config.EnableFcChat = true;
                    }

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

                Services.PluginInterface.SavePluginConfig(_config);
                MembershipCache.Reset();

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await WithWatchLock(async () =>
                        {
                            if (stopRequestWatcher)
                            {
                                Services.Log.Info("Stopping request watcher");
                                _requestWatcher.Stop();
                            }

                            if (stopChannelWatcher)
                            {
                                Services.Log.Info("Stopping channel watcher");
                                _channelWatcher.Stop();
                            }

                            if (stopOfficer)
                            {
                                Services.Log.Info("Stopping officer chat window networking");
                                await _officerChatWindow.StopNetworkingAsync().ConfigureAwait(false);
                            }

                            if (stopChat)
                            {
                                Services.Log.Info("Stopping chat window networking");
                                _chatWindow.StopNetworking();
                            }

                            if (startChannelWatcher)
                            {
                                Services.Log.Info("Starting channel watcher");
                                await _channelWatcher.Start().ConfigureAwait(false);
                            }

                            if (startRequestWatcher)
                            {
                                Services.Log.Info("Starting request watcher");
                                _requestWatcher.Start();
                            }

                            if (startChat)
                            {
                                Services.Log.Info("Starting chat window networking");
                                _chatWindow.StartNetworking();
                            }

                            if (startOfficer)
                            {
                                Services.Log.Info("Starting officer chat window networking");
                                await _officerChatWindow.StartNetworkingAsync().ConfigureAwait(false);
                            }

                            var presenceEnabled = (_config.SyncedChat && _config.EnableFcChat)
                                || OfficerPermissions.HasAccess(_config);
                            await SetPresenceAsync(presenceEnabled).ConfigureAwait(false);
                        }).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Services.Log.Error(ex, "Error updating watchers after refreshing roles.");
                    }
                    finally
                    {
                        _ = Services.Framework.RunOnTick(() =>
                        {
                            _mainWindow.HasOfficerAccess = OfficerPermissions.HasAccess(_config);
                        });
                    }
                });
            });

            await RoleCache.Refresh(HttpClient, _config, _tokenManager);
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

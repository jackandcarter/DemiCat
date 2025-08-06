using Discord;
using Discord.WebSocket;
using DiscordHelper;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Bind configuration values
builder.Services.Configure<BotConfig>(builder.Configuration);
var botConfig = builder.Configuration.Get<BotConfig>() ?? new BotConfig();
builder.WebHost.UseUrls($"http://0.0.0.0:{botConfig.Port}");

// Register Discord client with required gateway intents
builder.Services.AddSingleton(_ =>
{
    var socketConfig = new DiscordSocketConfig
    {
        GatewayIntents = GatewayIntents.Guilds |
                         GatewayIntents.GuildMessages |
                         GatewayIntents.GuildMessageReactions |
                         GatewayIntents.MessageContent
    };

    return new DiscordSocketClient(socketConfig);
});

// Register hosted service and controllers
builder.Services.AddHostedService<DiscordBotHostedService>();
builder.Services.AddControllers();

var app = builder.Build();

// Map controller routes
app.MapControllers();

// Minimal API endpoint for posting embeds
app.MapPost("/api/embeds", async (EmbedDto dto, DiscordSocketClient client, IOptions<BotConfig> options) =>
{
    var embed = new EmbedBuilder()
        .WithTitle(dto.Title)
        .WithDescription(dto.Description)
        .Build();

    foreach (var channelId in options.Value.ChannelIds)
    {
        if (client.GetChannel(channelId) is IMessageChannel channel)
        {
            await channel.SendMessageAsync(embed: embed);
        }
    }

    return Results.Ok(dto);
});

await app.RunAsync();

// Configuration model
public class BotConfig
{
    public string BotToken { get; set; } = string.Empty;
    public ulong[] ChannelIds { get; set; } = Array.Empty<ulong>();
    public ulong BotId { get; set; }
    public int Port { get; set; } = 5000;
}

// Hosted service for the Discord bot
public class DiscordBotHostedService : IHostedService
{
    private readonly DiscordSocketClient _client;
    private readonly BotConfig _config;

    public DiscordBotHostedService(DiscordSocketClient client, IOptions<BotConfig> config)
    {
        _client = client;
        _config = config.Value;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _client.LoginAsync(TokenType.Bot, _config.BotToken);
        await _client.StartAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _client.StopAsync();
        await _client.LogoutAsync();
    }
}


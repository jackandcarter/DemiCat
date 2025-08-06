using Discord;
using Discord.WebSocket;
using DiscordHelper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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

// Register services and controllers
builder.Services.AddSingleton<EmbedCache>();
builder.Services.AddSingleton<EmbedSocketHandler>();
builder.Services.AddHostedService<DiscordBotHostedService>();
builder.Services.AddControllers();

var app = builder.Build();

app.UseWebSockets();

app.Map("/ws/embeds", async context =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        var handler = context.RequestServices.GetRequiredService<EmbedSocketHandler>();
        var socket = await context.WebSockets.AcceptWebSocketAsync();
        await handler.AddSocketAsync(socket);
    }
    else
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
    }
});

// Map controller routes
app.MapControllers();

await app.RunAsync();

// Configuration model
public class BotConfig
{
    public string BotToken { get; set; } = string.Empty;
    public ulong[] ChannelIds { get; set; } = Array.Empty<ulong>();
    public ulong BotId { get; set; }
    public int Port { get; set; } = 5000;
}

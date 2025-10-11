using System;
using System.Threading.Tasks;

namespace DemiCatPlugin;

public interface IChatBridge : IDisposable
{
    void Start();
    void Stop();
    bool IsReady();
    void Subscribe(string channel, string? guildId, string? kind);
    void Unsubscribe(string channel);
    void Ack(string channel, string? guildId, string? kind);
    Task Send(string channel, object payload);
    void Resync(string channel);

    event Action<string>? MessageReceived;
    event Action<DiscordUserDto>? TypingReceived;
    event Action? Linked;
    event Action? Unlinked;
    event Action<string>? StatusChanged;
    event Action<string, long>? ResyncRequested;
    event Action? TemplatesUpdated;
}

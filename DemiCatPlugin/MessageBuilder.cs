namespace DemiCatPlugin;

public class MessageBuilder
{
    private string _channelId = string.Empty;
    private string _content = string.Empty;
    private bool _useCharacterName;
    private string? _messageId;
    private string? _messageChannelId;

    public MessageBuilder WithChannelId(string channelId)
    {
        _channelId = channelId;
        return this;
    }

    public MessageBuilder WithContent(string content)
    {
        _content = content;
        return this;
    }

    public MessageBuilder UseCharacterName(bool useCharacterName)
    {
        _useCharacterName = useCharacterName;
        return this;
    }

    public MessageBuilder WithMessageReference(string? messageId, string? channelId = null)
    {
        _messageId = messageId;
        _messageChannelId = channelId;
        return this;
    }

    public object Build()
    {
        return new
        {
            channelId = _channelId,
            content = _content,
            useCharacterName = _useCharacterName,
            messageReference = BuildMessageReference()
        };
    }

    public object? BuildMessageReference()
    {
        if (string.IsNullOrEmpty(_messageId))
            return null;
        if (!string.IsNullOrEmpty(_messageChannelId))
            return new { messageId = _messageId, channelId = _messageChannelId };
        return new { messageId = _messageId };
    }
}


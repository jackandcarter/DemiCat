namespace DemiCatPlugin.Emoji;

public interface IEmojiFontHandle : IDisposable
{
    IDisposable? Push();
}

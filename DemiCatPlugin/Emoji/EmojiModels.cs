namespace DemiCatPlugin.Emoji
{
    public class UnicodeEmoji
    {
        public string Emoji    { get; set; } = string.Empty;
        public string Name     { get; set; } = string.Empty;
        public string ImageUrl { get; set; } = string.Empty;
    }

    public record CustomEmoji(string Id, string Name, bool Animated);
    public record EmojiRefUnicode(string Emoji);
    public record EmojiRefCustom(string Id, string Name, bool Animated);

    public static class EmojiStrings
    {
        public static readonly (string Emoji, string Label)[] Popular =
        {
            ("😀","grinning"),("😁","beaming"),("😂","tears"),("🤣","rofl"),
            ("😊","smile"),("😎","cool"),("😍","heart eyes"),("😅","sweat"),
            ("👍","thumbs up"),("🎉","tada"),("🔥","fire"),("❤️","heart"),
            ("🤔","thinking")
        };
    }
}

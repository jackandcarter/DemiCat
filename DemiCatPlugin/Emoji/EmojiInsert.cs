namespace DemiCatPlugin.Emoji
{
    public static class EmojiInsert
    {
        public static void InsertUnicode(ref string input, string unicodeEmoji)
            => input += unicodeEmoji;

        public static void InsertCustom(ref string input, CustomEmoji e)
            => input += e.Animated ? $"<a:{e.Name}:{e.Id}>" : $"<:{e.Name}:{e.Id}>";
    }
}

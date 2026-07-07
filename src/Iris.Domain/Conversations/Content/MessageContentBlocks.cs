namespace Iris.Domain.Conversations.Content;

public static class MessageContentBlocks
{
    public static IReadOnlyList<MessageContentBlock> Text(string content)
    {
        return [MessageContentBlock.Text(content)];
    }

    public static string ToVisibleText(IEnumerable<MessageContentBlock> blocks)
    {
        return string.Concat(blocks
            .Where(block => block.Type == ContentBlockType.Text)
            .Select(block => block.Content));
    }
}

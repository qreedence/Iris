using System.Text;
using Iris.Application.AiIntegration.Models;
using Iris.Domain.Conversations.Content;

namespace Iris.Application.Conversations;

internal sealed class StreamedContentAccumulator
{
    private readonly SortedDictionary<int, MutableBlock> _blocks = [];

    public void Append(StreamedChunk chunk)
    {
        if (!_blocks.TryGetValue(chunk.BlockIndex, out var block))
        {
            block = new MutableBlock(chunk.BlockType);
            _blocks[chunk.BlockIndex] = block;
        }

        block.Append(chunk.Content, chunk.ProviderMetadata);
    }

    public IReadOnlyList<MessageContentBlock> ToContentBlocks() =>
        _blocks.Values.Select(block => block.ToContentBlock()).ToList();

    public string? GetPartialVisibleText()
    {
        var text = string.Concat(_blocks.Values
            .Where(block => block.Type == ContentBlockType.Text)
            .Select(block => block.Content));
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private sealed class MutableBlock(ContentBlockType type)
    {
        private readonly StringBuilder _content = new();
        private readonly List<IReadOnlyDictionary<string, object?>> _providerMetadata = [];

        public ContentBlockType Type { get; } = type;
        public string Content => _content.ToString();

        public void Append(
            string? content,
            IReadOnlyList<IReadOnlyDictionary<string, object?>>? providerMetadata)
        {
            if (!string.IsNullOrEmpty(content))
                _content.Append(content);
            if (providerMetadata is not null)
                _providerMetadata.AddRange(providerMetadata);
        }

        public MessageContentBlock ToContentBlock()
        {
            var metadata = _providerMetadata.Count == 0 ? null : _providerMetadata;
            return Type switch
            {
                ContentBlockType.Text => MessageContentBlock.Text(Content),
                ContentBlockType.Thinking => MessageContentBlock.Thinking(Content, metadata),
                _ => new MessageContentBlock
                {
                    Type = Type,
                    Content = Content,
                    ProviderMetadata = metadata,
                }
            };
        }
    }
}

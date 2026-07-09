using System.Text.Json;
using System.Text.Json.Serialization;
using Iris.Domain.Conversations.Content;
using Iris.Domain.Conversations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Iris.Infrastructure.Persistence.Configurations
{
    public class ConversationMessageConfiguration : IEntityTypeConfiguration<ConversationMessage>
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters =
            {
                new JsonStringEnumConverter<ContentBlockType>(JsonNamingPolicy.SnakeCaseLower),
                new JsonStringEnumConverter()
            }
        };

        public void Configure(EntityTypeBuilder<ConversationMessage> builder)
        {
            builder.ToTable("conversation_messages");

            builder.HasKey(m => m.Id);

            builder.HasIndex(m => m.ConversationId);

            builder.Property(m => m.Role)
                .HasConversion<string>()
                .HasMaxLength(20);

            builder.Property(m => m.ContentBlocks)
                .HasColumnType("jsonb")
                .HasConversion(
                    blocks => JsonSerializer.Serialize(blocks, JsonOptions),
                    json => JsonSerializer.Deserialize<List<MessageContentBlock>>(json, JsonOptions) ?? new List<MessageContentBlock>())
                .Metadata.SetValueComparer(new ValueComparer<List<MessageContentBlock>>(
                    (left, right) => JsonSerializer.Serialize(left, JsonOptions) == JsonSerializer.Serialize(right, JsonOptions),
                    blocks => JsonSerializer.Serialize(blocks, JsonOptions).GetHashCode(),
                    blocks => JsonSerializer.Deserialize<List<MessageContentBlock>>(
                        JsonSerializer.Serialize(blocks, JsonOptions),
                        JsonOptions) ?? new List<MessageContentBlock>()));
        }
    }
}

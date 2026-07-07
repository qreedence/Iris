using Iris.Domain.Conversations.Content;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Iris.Infrastructure.Persistence.Configurations;

public class ToolResultPayloadConfiguration : IEntityTypeConfiguration<ToolResultPayload>
{
    public void Configure(EntityTypeBuilder<ToolResultPayload> builder)
    {
        builder.ToTable("tool_result_payloads");

        builder.HasKey(p => p.Id);

        builder.HasIndex(p => p.ConversationId);
        builder.HasIndex(p => p.ToolCallId);

        builder.Property(p => p.ToolCallId)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(p => p.PayloadJson)
            .IsRequired()
            .HasColumnType("jsonb");

        builder.Property(p => p.Preview)
            .HasMaxLength(1000);

        builder.Property(p => p.CreatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP");
    }
}

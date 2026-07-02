using Iris.Domain.Conversations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Iris.Infrastructure.Persistence.Configurations;

public class ConversationTurnRequestConfiguration : IEntityTypeConfiguration<ConversationTurnRequest>
{
    public void Configure(EntityTypeBuilder<ConversationTurnRequest> builder)
    {
        builder.ToTable("conversation_turn_requests");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Model).IsRequired();

        builder.Property(r => r.ModelParameters).HasColumnType("jsonb");

        builder.Property(r => r.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.HasIndex(r => new { r.Status, r.CreatedAt });
        builder.HasIndex(r => new { r.ConversationId, r.Status });
    }
}

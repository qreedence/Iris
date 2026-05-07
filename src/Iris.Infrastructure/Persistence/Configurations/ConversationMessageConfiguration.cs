using Iris.Domain.Conversations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Iris.Infrastructure.Persistence.Configurations
{
    public class ConversationMessageConfiguration : IEntityTypeConfiguration<ConversationMessage>
    {
        public void Configure(EntityTypeBuilder<ConversationMessage> builder)
        {
            builder.ToTable("conversation_messages");

            builder.HasKey(m => m.Id);

            builder.HasIndex(m => m.ConversationId);

            builder.Property(m => m.Role)
                .HasConversion<string>()
                .HasMaxLength(20);

            builder.Property(m => m.Content).IsRequired();
        }
    }
}

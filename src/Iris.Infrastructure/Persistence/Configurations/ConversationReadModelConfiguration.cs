using Iris.Domain.Conversations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Iris.Infrastructure.Persistence.Configurations
{
    public class ConversationReadModelConfiguration : IEntityTypeConfiguration<ConversationReadModel>
    {
        public void Configure(EntityTypeBuilder<ConversationReadModel> builder)
        {
            builder.ToTable("conversation_read_models");

            builder.HasKey(c => c.Id);

            builder.Property(c => c.Title).IsRequired();
        }
    }
}

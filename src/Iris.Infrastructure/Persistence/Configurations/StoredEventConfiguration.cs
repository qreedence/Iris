using Iris.Domain.Conversations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Iris.Infrastructure.Persistence.Configurations
{
    public class StoredEventConfiguration : IEntityTypeConfiguration<StoredEvent>
    {
        public void Configure(EntityTypeBuilder<StoredEvent> builder)
        {
            builder.ToTable("stored_events");

            builder.HasKey(e => e.SequenceNumber);

            builder.Property(e => e.SequenceNumber).ValueGeneratedOnAdd();
            builder.Property(e => e.EventData).HasColumnType("jsonb");

            builder.HasIndex(e => e.AggregateId);
        }
    }
}

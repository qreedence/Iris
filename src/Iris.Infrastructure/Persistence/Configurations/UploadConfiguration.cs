using Iris.Domain.Conversations.Content;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Iris.Infrastructure.Persistence.Configurations;

public class UploadConfiguration : IEntityTypeConfiguration<Upload>
{
    public void Configure(EntityTypeBuilder<Upload> builder)
    {
        builder.ToTable("uploads");

        builder.HasKey(u => u.Id);

        builder.HasIndex(u => u.UserId);
        builder.HasIndex(u => new { u.Status, u.CreatedAt });

        builder.Property(u => u.Status)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(u => u.ContentType)
            .IsRequired()
            .HasMaxLength(255);

        builder.Property(u => u.StorageKey)
            .IsRequired()
            .HasMaxLength(1024);

        builder.Property(u => u.OriginalFileName)
            .HasMaxLength(255);

        builder.Property(u => u.CreatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP");
    }
}

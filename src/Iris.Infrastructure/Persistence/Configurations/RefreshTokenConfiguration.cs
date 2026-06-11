using Iris.Domain.Identity.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Iris.Infrastructure.Persistence.Configurations
{
    public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
    {
        public void Configure(EntityTypeBuilder<RefreshToken> builder)
        {
            builder.ToTable("refresh_tokens");

            builder.HasKey(t => t.Id);

            builder.Property(t => t.Token).HasMaxLength(256);

            builder.HasIndex(t => t.UserId);
            builder.HasIndex(t => t.FamilyId);
            builder.HasIndex(t => t.Token).IsUnique();
        }
    }
}

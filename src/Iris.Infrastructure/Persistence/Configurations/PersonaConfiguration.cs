using Iris.Domain.Personas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Iris.Infrastructure.Persistence.Configurations;

public class PersonaConfiguration : IEntityTypeConfiguration<Persona>
{
    public void Configure(EntityTypeBuilder<Persona> builder)
    {
        builder.ToTable("personas");

        builder.HasKey(p => p.Id);

        builder.HasIndex(p => p.UserId)
            .HasFilter("\"IsDeleted\" = false");

        builder.Property(p => p.Name)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(p => p.Kind)
            .HasConversion<string>()
            .HasMaxLength(20)
            .HasDefaultValue(PersonaKind.User);

        builder.HasIndex(p => p.UserId, "IX_personas_UserId_System")
            .IsUnique()
            .HasFilter("\"Kind\" = 'System' AND \"IsDeleted\" = false");

        builder.Property(p => p.ModelPreference)
            .HasMaxLength(100);

        builder.Property(p => p.Role)
            .HasMaxLength(200);

        builder.Property(p => p.Group)
            .HasMaxLength(100);

        builder.Property(p => p.CreatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        builder.Property(p => p.UpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP");
    }
}

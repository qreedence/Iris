using Iris.Domain.Personas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Iris.Infrastructure.Persistence.Configurations;

public class SystemPromptConfiguration : IEntityTypeConfiguration<SystemPrompt>
{
    public void Configure(EntityTypeBuilder<SystemPrompt> builder)
    {
        builder.ToTable("system_prompts");

        builder.HasKey(sp => sp.PersonaId);

        builder
            .HasOne(sp => sp.Persona)
            .WithOne(p => p.SystemPrompt)
            .HasForeignKey<SystemPrompt>(sp => sp.PersonaId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(sp => sp.CreatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        builder.Property(sp => sp.UpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP");
    }
}

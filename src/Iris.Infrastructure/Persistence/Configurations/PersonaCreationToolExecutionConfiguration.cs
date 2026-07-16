using Iris.Domain.Personas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Iris.Infrastructure.Persistence.Configurations;

public class PersonaCreationToolExecutionConfiguration : IEntityTypeConfiguration<PersonaCreationToolExecution>
{
    public void Configure(EntityTypeBuilder<PersonaCreationToolExecution> builder)
    {
        builder.ToTable("persona_creation_tool_executions");

        builder.HasKey(execution => new { execution.ConversationId, execution.ToolCallId });

        builder.Property(execution => execution.ToolCallId)
            .HasMaxLength(200);

        builder.Property(execution => execution.CreatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP");
    }
}

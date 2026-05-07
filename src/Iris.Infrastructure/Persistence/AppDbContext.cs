using Iris.Domain.Conversations;
using Iris.Domain.Conversations.Entities;
using Microsoft.EntityFrameworkCore;

namespace Iris.Infrastructure.Persistence;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<StoredEvent> StoredEvents { get; set; }
    public DbSet<ConversationReadModel> ConversationReadModels { get; set; }
    public DbSet<ConversationMessage> ConversationMessages { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
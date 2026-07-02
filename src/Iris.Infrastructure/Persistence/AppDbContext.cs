using Iris.Application.Identity.Interfaces;
using Iris.Domain.Conversations;
using Iris.Domain.Conversations.Entities;
using Iris.Domain.Identity.Entities;
using Iris.Domain.Personas;
using Iris.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Iris.Infrastructure.Persistence;

public class AppDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>
{
    private readonly ICurrentUserService _currentUser;

    public AppDbContext(DbContextOptions<AppDbContext> options, ICurrentUserService currentUser)
        : base(options)
    {
        _currentUser = currentUser;
    }

    public Guid CurrentUserId => _currentUser.UserId;
    public DbSet<StoredEvent> StoredEvents { get; set; }
    public DbSet<ConversationReadModel> ConversationReadModels { get; set; }
    public DbSet<ConversationMessage> ConversationMessages { get; set; }
    public DbSet<ConversationTurnRequest> ConversationTurnRequests { get; set; }
    public DbSet<Persona> Personas { get; set; }
    public DbSet<SystemPrompt> SystemPrompts { get; set; }
    public DbSet<RefreshToken> RefreshTokens { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        modelBuilder.Entity<Persona>()
            .HasQueryFilter(p => !p.IsDeleted && p.UserId == CurrentUserId);

        modelBuilder.Entity<SystemPrompt>()
            .HasQueryFilter(sp => !sp.Persona.IsDeleted && sp.Persona.UserId == CurrentUserId);

        modelBuilder.Entity<ConversationReadModel>()
            .HasQueryFilter(c => c.UserId == CurrentUserId);
    }
}

using Iris.Application.Identity.Interfaces;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Tests.Integration.Helpers;

/// <summary>
/// Dispatches MediatR commands through a fresh DI scope, optionally impersonating a
/// given user via <see cref="ICurrentUserService.OverrideUserId"/>. Replaces the
/// scope+OverrideUserId+IMediator.Send block that used to be copy-pasted into every
/// integration test class.
/// </summary>
public static class TestDispatcher
{
    /// <summary>
    /// Dispatches a command as the given user in a fresh scope.
    /// </summary>
    public static async Task<TResponse> SendCommandAsAsync<TResponse>(
        this IServiceProvider services,
        Guid userId,
        IRequest<TResponse> command,
        CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var userService = scope.ServiceProvider.GetRequiredService<ICurrentUserService>();
        userService.OverrideUserId = userId;
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        return await mediator.Send(command, ct);
    }

    /// <summary>
    /// Dispatches a command in a fresh scope without impersonating any user (the
    /// ambient current-user, if any, is used as-is).
    /// </summary>
    public static async Task<TResponse> SendCommandAsync<TResponse>(
        this IServiceProvider services,
        IRequest<TResponse> command,
        CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        return await mediator.Send(command, ct);
    }
}

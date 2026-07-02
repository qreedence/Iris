using System.Runtime.CompilerServices;
using Iris.Application.AiIntegration;
using Iris.Application.AiIntegration.Models;
using NSubstitute;

namespace Iris.Tests.Integration.Helpers;

/// <summary>
/// Shared mock <see cref="IChatProvider"/> factory used by both <see cref="ApiTestFactory"/>
/// and <see cref="IntegrationTestFactory"/>. Both factories need the same
/// StreamAsync default so their mocks don't drift (ApiTestFactory's default once had a
/// StreamAsync stub that IntegrationTestFactory's bare substitute lacked).
/// </summary>
public static class ChatProviderMock
{
    /// <summary>
    /// Creates a mock IChatProvider whose StreamAsync returns "Mock AI response"
    /// with 10/5 tokens. Configure per test via NSubstitute on top of this default.
    /// </summary>
    public static IChatProvider CreateDefault()
    {
        var mock = Substitute.For<IChatProvider>();
        mock.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => StreamResponse("Mock AI response", call.ArgAt<CancellationToken>(1)));
        return mock;
    }

    private static async IAsyncEnumerable<StreamedChunk> StreamResponse(
        string content,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        yield return new StreamedChunk(content, false, null);
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        yield return new StreamedChunk(null, true, new UsageInfo(10, 5, 15));
    }
}

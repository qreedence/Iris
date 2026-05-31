using FluentAssertions;
using Iris.Application.AiIntegration.Models;
using Iris.Application.Conversations;
using Iris.Application.Exceptions;
using Iris.Application.Personas;
using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Events;
using NSubstitute;

namespace Iris.Tests.Unit.Conversations;

public class ConversationTurnPreparerTests
{
    private readonly IEventStore _eventStore = Substitute.For<IEventStore>();
    private readonly IPersonaService _personaService = Substitute.For<IPersonaService>();

    private ConversationTurnPreparer CreateSut() => new(_eventStore, _personaService);

    [Fact]
    public async Task PrepareAsync_ExistingConversation_BuildsChatRequestWithHistoryAndPersonaPrompt()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var personaId = Guid.NewGuid();
        var modelParameters = new ModelParameters(0.7f, 500, 0.9f);

        _eventStore.LoadStreamAsync(conversationId, Arg.Any<CancellationToken>())
            .Returns([
                new ConversationCreated(conversationId, personaId, "Chat"),
                new MessageSent(Guid.NewGuid(), conversationId, "First user message", ChatRole.User),
                new AssistantResponseCompleted(Guid.NewGuid(), conversationId, "First assistant response", "test/model"),
                new MessageSent(Guid.NewGuid(), conversationId, "Second user message", ChatRole.User)
            ]);
        SetupPersona(personaId, systemPrompt: "You are Iris.");

        // Act
        var prepared = await sut.PrepareAsync(
            conversationId,
            "fallback/model",
            modelParameters,
            TestContext.Current.CancellationToken);

        // Assert
        prepared.ChatRequest.Model.Should().Be("fallback/model");
        prepared.ChatRequest.SystemPrompt.Should().Be("You are Iris.");
        prepared.ChatRequest.ModelParameters.Should().Be(modelParameters);
        prepared.ChatRequest.Messages.Should().Equal([
            new ChatMessage(ChatRole.User, "First user message"),
            new ChatMessage(ChatRole.Assistant, "First assistant response"),
            new ChatMessage(ChatRole.User, "Second user message")
        ]);
        prepared.PreStreamEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task PrepareAsync_PersonaHasModelPreference_UsesPreferenceWhenRequestMatches()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var personaId = Guid.NewGuid();
        SetupExistingConversation(conversationId, personaId);
        SetupPersona(personaId, modelPreference: "persona/model");

        // Act
        var prepared = await sut.PrepareAsync(
            conversationId,
            "persona/model",
            null,
            TestContext.Current.CancellationToken);

        // Assert
        prepared.ChatRequest.Model.Should().Be("persona/model");
        prepared.PreStreamEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task PrepareAsync_PersonaHasNoModelPreference_UsesFallbackModel()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var personaId = Guid.NewGuid();
        SetupExistingConversation(conversationId, personaId);
        SetupPersona(personaId, modelPreference: null);

        // Act
        var prepared = await sut.PrepareAsync(
            conversationId,
            "fallback/model",
            null,
            TestContext.Current.CancellationToken);

        // Assert
        prepared.ChatRequest.Model.Should().Be("fallback/model");
        prepared.PreStreamEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task PrepareAsync_ModelDiffersFromEffective_ReturnsModelChangedPreStreamEvent()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var personaId = Guid.NewGuid();
        SetupExistingConversation(conversationId, personaId);
        SetupPersona(personaId, modelPreference: "persona/model");

        // Act
        var prepared = await sut.PrepareAsync(
            conversationId,
            "new/model",
            null,
            TestContext.Current.CancellationToken);

        // Assert
        prepared.ChatRequest.Model.Should().Be("new/model");
        prepared.PreStreamEvents.Should().ContainSingle()
            .Which.Should().Be(new ModelChanged(conversationId, "new/model"));
    }

    [Fact]
    public async Task PrepareAsync_HasModelChangedEvent_UsesModelChangedOverPersonaPreference()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var personaId = Guid.NewGuid();
        _eventStore.LoadStreamAsync(conversationId, Arg.Any<CancellationToken>())
            .Returns([
                new ConversationCreated(conversationId, personaId, "Chat"),
                new MessageSent(Guid.NewGuid(), conversationId, "Hello", ChatRole.User),
                new ModelChanged(conversationId, "changed/model")
            ]);
        SetupPersona(personaId, modelPreference: "persona/model");

        // Act
        var prepared = await sut.PrepareAsync(
            conversationId,
            "changed/model",
            null,
            TestContext.Current.CancellationToken);

        // Assert
        prepared.ChatRequest.Model.Should().Be("changed/model");
        prepared.PreStreamEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task PrepareAsync_ModelChangedThenSwitchAgain_ReturnsNewModelChangedPreStreamEvent()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var personaId = Guid.NewGuid();
        _eventStore.LoadStreamAsync(conversationId, Arg.Any<CancellationToken>())
            .Returns([
                new ConversationCreated(conversationId, personaId, "Chat"),
                new MessageSent(Guid.NewGuid(), conversationId, "Hello", ChatRole.User),
                new ModelChanged(conversationId, "old/model")
            ]);
        SetupPersona(personaId, modelPreference: "persona/model");

        // Act
        var prepared = await sut.PrepareAsync(
            conversationId,
            "newer/model",
            null,
            TestContext.Current.CancellationToken);

        // Assert
        prepared.ChatRequest.Model.Should().Be("newer/model");
        prepared.PreStreamEvents.Should().ContainSingle()
            .Which.Should().Be(new ModelChanged(conversationId, "newer/model"));
    }

    [Fact]
    public async Task PrepareAsync_PersonaNotFound_ThrowsConversationPersonaNotFoundException()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var personaId = Guid.NewGuid();
        SetupExistingConversation(conversationId, personaId);
        _personaService.GetForConversationAsync(personaId, Arg.Any<CancellationToken>())
            .Returns<Task<PersonaDto>>(_ => throw new NotFoundException("Persona not found."));

        // Act
        var act = () => sut.PrepareAsync(
            conversationId,
            "fallback/model",
            null,
            TestContext.Current.CancellationToken);

        // Assert
        var exception = await act.Should().ThrowAsync<ConversationPersonaNotFoundException>();
        exception.Which.ConversationId.Should().Be(conversationId);
        exception.Which.PersonaId.Should().Be(personaId);
    }

    [Fact]
    public async Task PrepareAsync_ConversationDoesNotExist_ThrowsNotFoundException()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        _eventStore.LoadStreamAsync(conversationId, Arg.Any<CancellationToken>())
            .Returns(new List<ConversationEvent>());

        // Act
        var act = () => sut.PrepareAsync(
            conversationId,
            "fallback/model",
            null,
            TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<NotFoundException>()
            .WithMessage("*does not exist*");
    }

    private void SetupExistingConversation(Guid conversationId, Guid personaId)
    {
        _eventStore.LoadStreamAsync(conversationId, Arg.Any<CancellationToken>())
            .Returns([
                new ConversationCreated(conversationId, personaId, "Chat"),
                new MessageSent(Guid.NewGuid(), conversationId, "Hello", ChatRole.User)
            ]);
    }

    private void SetupPersona(Guid personaId, string? systemPrompt = null, string? modelPreference = null)
    {
        _personaService.GetForConversationAsync(personaId, Arg.Any<CancellationToken>())
            .Returns(new PersonaDto(
                personaId,
                "Iris",
                systemPrompt,
                modelPreference,
                null,
                null,
                null,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow));
    }
}

using FluentAssertions;
using Iris.Application.AiIntegration.Models;
using Iris.Application.Conversations;
using Iris.Application.Exceptions;
using Iris.Application.Personas;
using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Events;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Iris.Domain.Conversations.Content;

namespace Iris.Tests.Unit.Conversations;

public class ConversationTurnPreparerTests
{
    private readonly IEventStore _eventStore = Substitute.For<IEventStore>();
    private readonly IPersonaService _personaService = Substitute.For<IPersonaService>();
    private readonly ISystemPromptAssembler _systemPromptAssembler = Substitute.For<ISystemPromptAssembler>();
    private readonly Guid _userId = Guid.NewGuid();

    private ConversationTurnPreparer CreateSut()
    {
        _systemPromptAssembler
            .BuildAsync(Arg.Any<SystemPromptDto>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<SystemPromptDto>().Identity);

        return new ConversationTurnPreparer(
            _eventStore,
            _personaService,
            _systemPromptAssembler,
            NullLogger<ConversationTurnPreparer>.Instance);
    }

    [Fact]
    public async Task PrepareAsync_ExistingConversation_BuildsChatRequestWithHistoryAndAssembledPersonaPrompt()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var personaId = Guid.NewGuid();
        var modelParameters = new ModelParameters(0.7f, 500, 0.9f);

        _eventStore.LoadStreamAsync(conversationId, Arg.Any<CancellationToken>())
            .Returns([
                new ConversationCreated(conversationId, _userId, personaId, "Chat"),
                new MessageSent(Guid.NewGuid(), conversationId, MessageContentBlocks.Text("First user message"), ChatRole.User),
                new AssistantResponseCompleted(Guid.NewGuid(), conversationId, MessageContentBlocks.Text("First assistant response"), "test/model"),
                new MessageSent(Guid.NewGuid(), conversationId, MessageContentBlocks.Text("Second user message"), ChatRole.User)
            ]);
        SetupPersona(personaId, identity: "You are Iris.");
        _systemPromptAssembler
            .BuildAsync(Arg.Any<SystemPromptDto>(), Arg.Any<CancellationToken>())
            .Returns("<identity>You are Iris.</identity>");

        // Act
        var prepared = await sut.PrepareAsync(
            _userId,
            conversationId,
            Guid.NewGuid(),
            "fallback/model",
            changeModel: false,
            modelParameters,
            TestContext.Current.CancellationToken);

        // Assert
        prepared.ChatRequest.Model.Should().Be("fallback/model");
        prepared.ChatRequest.SystemPrompt.Should().Be("<identity>You are Iris.</identity>");
        prepared.ChatRequest.ModelParameters.Should().Be(modelParameters);
        AssertRolesAndVisibleText(
            prepared.ChatRequest.Messages,
            (ChatRole.User, "First user message"),
            (ChatRole.Assistant, "First assistant response"),
            (ChatRole.User, "Second user message"));
        prepared.PreStreamEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task PrepareAsync_UsesSystemPromptAssemblerInsteadOfRawPersonaSection()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var personaId = Guid.NewGuid();
        var systemPrompt = new SystemPromptDto(
            "Raw identity",
            "Raw voice",
            "Raw role",
            "Raw relationship",
            "Raw tools");

        SetupExistingConversation(conversationId, personaId);
        SetupPersona(personaId, systemPrompt);
        _systemPromptAssembler
            .BuildAsync(systemPrompt, Arg.Any<CancellationToken>())
            .Returns("assembled prompt");

        // Act
        var prepared = await sut.PrepareAsync(
            _userId,
            conversationId,
            Guid.NewGuid(),
            "fallback/model",
            changeModel: false,
            modelParameters: null,
            TestContext.Current.CancellationToken);

        // Assert
        prepared.ChatRequest.SystemPrompt.Should().Be("assembled prompt");
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
            _userId,
            conversationId,
            Guid.NewGuid(),
            "persona/model",
            changeModel: false,
            modelParameters: null,
            TestContext.Current.CancellationToken);

        // Assert
        prepared.ChatRequest.Model.Should().Be("persona/model");
        prepared.PreStreamEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task PrepareAsync_PersonaHasModelPreferenceAndChangeModelFalse_UsesPreferenceEvenWhenRequestDiffers()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var personaId = Guid.NewGuid();
        SetupExistingConversation(conversationId, personaId);
        SetupPersona(personaId, modelPreference: "persona/model");

        // Act
        var prepared = await sut.PrepareAsync(
            _userId,
            conversationId,
            Guid.NewGuid(),
            "frontend/fallback",
            changeModel: false,
            modelParameters: null,
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
            _userId,
            conversationId,
            Guid.NewGuid(),
            "fallback/model",
            changeModel: false,
            modelParameters: null,
            TestContext.Current.CancellationToken);

        // Assert
        prepared.ChatRequest.Model.Should().Be("fallback/model");
        prepared.PreStreamEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task PrepareAsync_ChangeModelTrue_ReturnsModelChangedPreStreamEvent()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var personaId = Guid.NewGuid();
        SetupExistingConversation(conversationId, personaId);
        SetupPersona(personaId, modelPreference: "persona/model");

        // Act
        var prepared = await sut.PrepareAsync(
            _userId,
            conversationId,
            Guid.NewGuid(),
            "new/model",
            changeModel: true,
            modelParameters: null,
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
                new ConversationCreated(conversationId, _userId, personaId, "Chat"),
                new MessageSent(Guid.NewGuid(), conversationId, MessageContentBlocks.Text("Hello"), ChatRole.User),
                new ModelChanged(conversationId, "changed/model")
            ]);
        SetupPersona(personaId, modelPreference: "persona/model");

        // Act
        var prepared = await sut.PrepareAsync(
            _userId,
            conversationId,
            Guid.NewGuid(),
            "changed/model",
            changeModel: false,
            modelParameters: null,
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
                new ConversationCreated(conversationId, _userId, personaId, "Chat"),
                new MessageSent(Guid.NewGuid(), conversationId, MessageContentBlocks.Text("Hello"), ChatRole.User),
                new ModelChanged(conversationId, "old/model")
            ]);
        SetupPersona(personaId, modelPreference: "persona/model");

        // Act
        var prepared = await sut.PrepareAsync(
            _userId,
            conversationId,
            Guid.NewGuid(),
            "newer/model",
            changeModel: true,
            modelParameters: null,
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
        _personaService.GetByIdAsync(personaId, Arg.Any<CancellationToken>())
            .Returns<Task<PersonaDto>>(_ => throw new NotFoundException("Persona not found."));

        // Act
        var act = () => sut.PrepareAsync(
            _userId,
            conversationId,
            Guid.NewGuid(),
            "fallback/model",
            changeModel: false,
            modelParameters: null,
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
            _userId,
            conversationId,
            Guid.NewGuid(),
            "fallback/model",
            changeModel: false,
            modelParameters: null,
            TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<NotFoundException>()
            .WithMessage("*does not exist*");
    }

    private void SetupExistingConversation(Guid conversationId, Guid personaId)
    {
        _eventStore.LoadStreamAsync(conversationId, Arg.Any<CancellationToken>())
            .Returns([
                new ConversationCreated(conversationId, _userId, personaId, "Chat"),
                new MessageSent(Guid.NewGuid(), conversationId, MessageContentBlocks.Text("Hello"), ChatRole.User)
            ]);
    }

    [Fact]
    public async Task PrepareAsync_ConversationBelongsToAnotherUser_ThrowsNotFoundException()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var personaId = Guid.NewGuid();
        var attackerId = Guid.NewGuid();

        _eventStore.LoadStreamAsync(conversationId, Arg.Any<CancellationToken>())
            .Returns([
                new ConversationCreated(conversationId, _userId, personaId, "Chat"),
                new MessageSent(Guid.NewGuid(), conversationId, MessageContentBlocks.Text("Hello"), ChatRole.User)
            ]);

        // Act — attacker tries to prepare another user's conversation
        var act = () => sut.PrepareAsync(
            attackerId,
            conversationId,
            Guid.NewGuid(),
            "fallback/model",
            changeModel: false,
            modelParameters: null,
            TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task PrepareAsync_TruncatesStreamAtItsOwnMessage_IgnoringLaterEvents()
    {
        // Two turns queued back to back: MsgA then MsgB, with a ModelChanged AFTER
        // MsgA. Preparing for turn A must reflect the conversation AS OF MsgA — its
        // history ends at MsgA (MsgB excluded) and the later ModelChanged is ignored
        // for model resolution.
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var personaId = Guid.NewGuid();
        var messageAId = Guid.NewGuid();

        _eventStore.LoadStreamAsync(conversationId, Arg.Any<CancellationToken>())
            .Returns([
                new ConversationCreated(conversationId, _userId, personaId, "Chat"),
                new MessageSent(messageAId, conversationId, MessageContentBlocks.Text("Message A"), ChatRole.User),
                new ModelChanged(conversationId, "later/model"),
                new MessageSent(Guid.NewGuid(), conversationId, MessageContentBlocks.Text("Message B"), ChatRole.User),
            ]);
        SetupPersona(personaId, modelPreference: "persona/model");

        var prepared = await sut.PrepareAsync(
            _userId,
            conversationId,
            messageAId,
            "fallback/model",
            changeModel: false,
            modelParameters: null,
            TestContext.Current.CancellationToken);

        // History ends at Message A — Message B (queued after) is not included.
        AssertRolesAndVisibleText(
            prepared.ChatRequest.Messages,
            (ChatRole.User, "Message A"));

        // The ModelChanged after Message A is ignored; model falls to persona pref.
        prepared.ChatRequest.Model.Should().Be("persona/model");
    }


    private static void AssertRolesAndVisibleText(
        IReadOnlyList<ChatMessage> messages,
        params (ChatRole Role, string VisibleText)[] expected)
    {
        messages.Should().HaveCount(expected.Length);

        for (var i = 0; i < expected.Length; i++)
        {
            messages[i].Role.Should().Be(expected[i].Role);
            messages[i].VisibleText.Should().Be(expected[i].VisibleText);
        }
    }

    private void SetupPersona(
        Guid personaId,
        string? identity = null,
        string? modelPreference = null)
    {
        SetupPersona(
            personaId,
            new SystemPromptDto(identity, null, null, null, null),
            modelPreference);
    }

    private void SetupPersona(
        Guid personaId,
        SystemPromptDto? systemPrompt,
        string? modelPreference = null)
    {
        _personaService.GetByIdAsync(personaId, Arg.Any<CancellationToken>())
            .Returns(new PersonaDto(
                personaId,
                "Iris",
                systemPrompt ?? SystemPromptDto.Empty,
                modelPreference,
                null,
                null,
                null,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow));
    }
}

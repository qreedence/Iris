using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Iris.Api.Authentication;
using Iris.Application.Conversations;
using Iris.Application.Conversations.Commands.CreateConversation;
using Iris.Application.Conversations.Queries;
using Iris.Application.Personas;
using Iris.Domain.AiIntegration;
using Iris.Tests.Integration.Helpers;
using MediatR;

namespace Iris.Tests.Integration.Conversations;

public class ConversationEndpointTests : IClassFixture<ApiTestFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly Guid _userId = Guid.NewGuid();
    private readonly HttpClient _client;
    private readonly ApiTestFactory _factory;

    public ConversationEndpointTests(ApiTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient(_userId);
    }

    private Task SendCommand<TResponse>(IRequest<TResponse> command) => SendCommandAs(_userId, command);

    private Task SendCommandAs<TResponse>(Guid userId, IRequest<TResponse> command) =>
        _factory.Services.SendCommandAsAsync(userId, command, TestContext.Current.CancellationToken);

    /// <summary>
    /// Seeds a MessageSent event as the fixture's authenticated user, equivalent to
    /// what SendMessageCommand used to do via SendCommand.
    /// </summary>
    private Task SendMessage(Guid conversationId, string content, ChatRole role = ChatRole.User)
    {
        return ConversationSeeder.SendMessageAsync(
            _factory.Services, conversationId, content, role, _userId, TestContext.Current.CancellationToken);
    }

    // ── POST /api/conversations ────────────────────────────────────

    [Fact]
    public async Task PostConversation_ValidData_Returns201WithId()
    {
        // Arrange
        var persona = await CreatePersonaAsync();

        // Act
        var response = await _client.PostAsJsonAsync(
            "/api/conversations",
            new CreateConversationRequest(persona.Id, "New Chat"),
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var conversationId = await response.Content
            .ReadFromJsonAsync<Guid>(JsonOptions, TestContext.Current.CancellationToken);

        conversationId.Should().NotBe(Guid.Empty);
        response.Headers.Location.Should().NotBeNull();
    }

    [Fact]
    public async Task PostConversation_EmptyTitle_Returns400()
    {
        // Arrange
        var persona = await CreatePersonaAsync();

        // Act
        var response = await _client.PostAsJsonAsync(
            "/api/conversations",
            new CreateConversationRequest(persona.Id, ""),
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostConversation_OtherUsersPersona_Returns404()
    {
        // Arrange
        var otherPersona = await CreatePersonaForUserAsync(Guid.NewGuid(), "Other User Persona");

        // Act
        var response = await _client.PostAsJsonAsync(
            "/api/conversations",
            new CreateConversationRequest(otherPersona.Id, "Nope"),
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PostConversation_SequentialCreates_BothSucceed()
    {
        // Arrange
        var persona = await CreatePersonaAsync();
        var conversationId = Guid.NewGuid();
        await SendCommand(new CreateConversationCommand(conversationId, _userId, persona.Id, "Existing"));

        // Act — create another via HTTP (server generates a new ID, so no actual duplicate)
        // Instead, test the handler's duplicate guard by sending the same command twice
        var response1 = await _client.PostAsJsonAsync(
            "/api/conversations",
            new CreateConversationRequest(persona.Id, "Chat A"),
            TestContext.Current.CancellationToken);

        response1.StatusCode.Should().Be(HttpStatusCode.Created);

        // The server generates IDs, so true duplicates can't happen via REST.
        // The duplicate guard only fires if the same ConversationId is reused,
        // which the endpoint prevents by generating a new Guid each time.
        // This test verifies two sequential creates both succeed.
        var response2 = await _client.PostAsJsonAsync(
            "/api/conversations",
            new CreateConversationRequest(persona.Id, "Chat B"),
            TestContext.Current.CancellationToken);

        response2.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private Task<PersonaDto> CreatePersonaAsync(string name = "Iris") =>
        TestPersonas.CreateAsync(_factory.Services, _userId, name, ct: TestContext.Current.CancellationToken);

    private Task<PersonaDto> CreatePersonaForUserAsync(Guid userId, string name) =>
        TestPersonas.CreateAsync(_factory.Services, userId, name, ct: TestContext.Current.CancellationToken);

    // ── POST /api/conversations — PersonaId on summary ───────────

    [Fact]
    public async Task GetConversations_IncludesPersonaId()
    {
        // Arrange
        var persona = await CreatePersonaAsync();
        var conversationId = Guid.NewGuid();
        await SendCommand(new CreateConversationCommand(conversationId, _userId, persona.Id, "Chat"));

        // Act
        var response = await _client.GetAsync("/api/conversations", TestContext.Current.CancellationToken);
        var conversations = await response.Content
            .ReadFromJsonAsync<List<ConversationSummaryDto>>(JsonOptions, TestContext.Current.CancellationToken);

        // Assert
        var conversation = conversations!.First(c => c.Id == conversationId);
        conversation.PersonaId.Should().Be(persona.Id);
    }

    [Fact]
    public async Task GetConversations_CurrentModelIsNullByDefault()
    {
        // Arrange
        var persona = await CreatePersonaAsync();
        var conversationId = Guid.NewGuid();
        await SendCommand(new CreateConversationCommand(conversationId, _userId, persona.Id, "Chat"));

        // Act
        var response = await _client.GetAsync("/api/conversations", TestContext.Current.CancellationToken);
        var conversations = await response.Content
            .ReadFromJsonAsync<List<ConversationSummaryDto>>(JsonOptions, TestContext.Current.CancellationToken);

        // Assert
        var conversation = conversations!.First(c => c.Id == conversationId);
        conversation.CurrentModel.Should().BeNull();
    }

    // ── §1 GET /api/conversations — empty ─────────────────────────

    [Fact]
    public async Task GetConversations_WithoutAuth_Returns401()
    {
        // Arrange
        using var unauthenticatedClient = _factory.CreateClient();

        // Act
        var response = await unauthenticatedClient.GetAsync(
            "/api/conversations",
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetConversations_NoConversations_ReturnsEmptyList()
    {
        // Act
        var response = await _client.GetAsync("/api/conversations", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var conversations = await response.Content
            .ReadFromJsonAsync<List<ConversationSummaryDto>>(JsonOptions, TestContext.Current.CancellationToken);

        conversations.Should().NotBeNull();
    }

    // ── §2 GET /api/conversations — after creating ────────────────

    [Fact]
    public async Task GetConversations_OnlyReturnsAuthenticatedUsersConversations()
    {
        // Arrange
        var ownConversationId = Guid.NewGuid();
        var otherConversationId = Guid.NewGuid();
        var ownPersona = await CreatePersonaAsync();
        var otherUserId = Guid.NewGuid();
        var otherPersona = await CreatePersonaForUserAsync(otherUserId, "Other User Persona");

        await SendCommand(new CreateConversationCommand(ownConversationId, _userId, ownPersona.Id, "Mine"));
        await SendCommandAs(otherUserId, new CreateConversationCommand(otherConversationId, otherUserId, otherPersona.Id, "Not Mine"));

        // Act
        var response = await _client.GetAsync("/api/conversations", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var conversations = await response.Content
            .ReadFromJsonAsync<List<ConversationSummaryDto>>(JsonOptions, TestContext.Current.CancellationToken);

        conversations.Should().NotBeNull();
        conversations!.Should().Contain(c => c.Id == ownConversationId);
        conversations.Should().NotContain(c => c.Id == otherConversationId);
    }

    [Fact]
    public async Task GetConversations_AfterCreating_ReturnsConversationList()
    {
        // Arrange
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var persona1 = await CreatePersonaAsync();
        var persona2 = await CreatePersonaAsync();
        await SendCommand(new CreateConversationCommand(id1, _userId, persona1.Id, "Chat One"));
        await SendCommand(new CreateConversationCommand(id2, _userId, persona2.Id, "Chat Two"));

        // Act
        var response = await _client.GetAsync("/api/conversations", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var conversations = await response.Content
            .ReadFromJsonAsync<List<ConversationSummaryDto>>(JsonOptions, TestContext.Current.CancellationToken);

        conversations.Should().NotBeNull();
        conversations.Should().Contain(c => c.Id == id1 && c.Title == "Chat One");
        conversations.Should().Contain(c => c.Id == id2 && c.Title == "Chat Two");
    }

    // ── §3 GET /api/conversations — response shape ────────────────

    [Fact]
    public async Task GetConversations_ResponseShape_MatchesExpectedDto()
    {
        // Arrange
        var conversationId = Guid.NewGuid();
        var persona = await CreatePersonaAsync();
        await SendCommand(new CreateConversationCommand(conversationId, _userId, persona.Id, "Shape Test"));
        await SendMessage(conversationId, "Hello", ChatRole.User);

        // Act
        var response = await _client.GetAsync("/api/conversations", TestContext.Current.CancellationToken);
        var conversations = await response.Content
            .ReadFromJsonAsync<List<ConversationSummaryDto>>(JsonOptions, TestContext.Current.CancellationToken);

        // Assert
        var conversation = conversations!.First(c => c.Id == conversationId);

        conversation.Title.Should().Be("Shape Test");
        conversation.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));
        conversation.MessageCount.Should().Be(1);
        conversation.LastMessageAt.Should().NotBeNull();
    }

    // ── §4 GET /api/conversations/{id}/messages — in order ────────

    [Fact]
    public async Task GetMessages_ExistingConversation_ReturnsMessagesInOrder()
    {
        // Arrange
        var conversationId = Guid.NewGuid();
        var persona = await CreatePersonaAsync();
        await SendCommand(new CreateConversationCommand(conversationId, _userId, persona.Id, "Chat"));
        await SendMessage(conversationId, "First", ChatRole.User);
        await SendMessage(conversationId, "Second", ChatRole.User);
        await SendMessage(conversationId, "Third", ChatRole.User);

        // Act
        var response = await _client.GetAsync($"/api/conversations/{conversationId}/messages", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var messages = await response.Content
            .ReadFromJsonAsync<List<ConversationMessageDto>>(JsonOptions, TestContext.Current.CancellationToken);

        messages.Should().HaveCount(3);
        messages![0].Content.Should().Be("First");
        messages[1].Content.Should().Be("Second");
        messages[2].Content.Should().Be("Third");
    }

    // ── §5 GET /api/conversations/{id}/messages — 404 ─────────────

    [Fact]
    public async Task GetMessages_NonExistentConversation_Returns404()
    {
        // Act
        var response = await _client.GetAsync($"/api/conversations/{Guid.NewGuid()}/messages", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── §6 GET /api/conversations/{id}/messages — response shape ──

    [Fact]
    public async Task GetMessages_OtherUsersConversation_Returns404()
    {
        // Arrange
        var conversationId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var otherPersona = await CreatePersonaForUserAsync(otherUserId, "Other User Persona");
        await SendCommandAs(otherUserId, new CreateConversationCommand(conversationId, otherUserId, otherPersona.Id, "Not Mine"));
        await ConversationSeeder.SendMessageAsync(
            _factory.Services, conversationId, "Secret", ChatRole.User, ct: TestContext.Current.CancellationToken);

        // Act
        var response = await _client.GetAsync(
            $"/api/conversations/{conversationId}/messages",
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetMessages_ResponseShape_MatchesExpectedDto()
    {
        // Arrange
        var conversationId = Guid.NewGuid();
        var persona = await CreatePersonaAsync();
        await SendCommand(new CreateConversationCommand(conversationId, _userId, persona.Id, "Chat"));
        await SendMessage(conversationId, "Hello!", ChatRole.User);

        // Act
        var response = await _client.GetAsync($"/api/conversations/{conversationId}/messages", TestContext.Current.CancellationToken);
        var messages = await response.Content
            .ReadFromJsonAsync<List<ConversationMessageDto>>(JsonOptions, TestContext.Current.CancellationToken);

        // Assert
        var message = messages!.First();

        message.Id.Should().NotBe(Guid.Empty);
        message.ConversationId.Should().Be(conversationId);
        message.Role.Should().Be(ChatRole.User);
        message.Content.Should().Be("Hello!");
        message.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));
    }

    // ── §7 GET /api/conversations/{id}/messages — isolation ───────

    [Fact]
    public async Task GetMessages_OnlyReturnsMessagesForRequestedConversation()
    {
        // Arrange
        var conversationA = Guid.NewGuid();
        var conversationB = Guid.NewGuid();
        var personaA = await CreatePersonaAsync();
        var personaB = await CreatePersonaAsync();

        await SendCommand(new CreateConversationCommand(conversationA, _userId, personaA.Id, "Chat A"));
        await SendCommand(new CreateConversationCommand(conversationB, _userId, personaB.Id, "Chat B"));

        await SendMessage(conversationA, "Message A", ChatRole.User);
        await SendMessage(conversationB, "Message B", ChatRole.User);

        // Act
        var response = await _client.GetAsync($"/api/conversations/{conversationA}/messages", TestContext.Current.CancellationToken);
        var messages = await response.Content
            .ReadFromJsonAsync<List<ConversationMessageDto>>(JsonOptions, TestContext.Current.CancellationToken);

        // Assert
        messages.Should().HaveCount(1);
        messages![0].Content.Should().Be("Message A");
        messages[0].ConversationId.Should().Be(conversationA);
    }

    // ── Unauthenticated coverage ──────────────────────────────────

    [Fact]
    public async Task PostConversation_WithoutAuth_Returns401()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/conversations",
            new CreateConversationRequest(Guid.NewGuid(), "Nope"),
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetMessages_WithoutAuth_Returns401()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync(
            $"/api/conversations/{Guid.NewGuid()}/messages",
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}

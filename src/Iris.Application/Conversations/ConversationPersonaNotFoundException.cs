namespace Iris.Application.Conversations;

public class ConversationPersonaNotFoundException : Exception
{
    public ConversationPersonaNotFoundException(Guid conversationId, Guid personaId)
        : base("The persona for this conversation no longer exists.")
    {
        ConversationId = conversationId;
        PersonaId = personaId;
    }

    public Guid ConversationId { get; }
    public Guid PersonaId { get; }
}

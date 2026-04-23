namespace Iris.Application.AiIntegration.Exceptions
{
    public class ChatDeserializationException : ChatProviderException
    {
        public ChatDeserializationException(string message) : base(message) { }
        public ChatDeserializationException(string message, Exception innerException)
: base(message, innerException) { }
    }
}

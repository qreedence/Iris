namespace Iris.Application.AiIntegration.Exceptions
{
    public class ChatRateLimitException : ChatProviderException
    {
        public ChatRateLimitException(string message) : base(message) { }
        public ChatRateLimitException(string message, Exception innerException)
: base(message, innerException) { }
    }
}

namespace Iris.Application.AiIntegration.Exceptions
{
    public class ChatAuthenticationException : ChatProviderException
    {
        public ChatAuthenticationException(string message) : base(message) { }
        public ChatAuthenticationException(string message, Exception innerException)
    : base(message, innerException) { }
    }
}

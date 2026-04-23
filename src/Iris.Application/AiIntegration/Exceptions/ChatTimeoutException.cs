namespace Iris.Application.AiIntegration.Exceptions
{
    public class ChatTimeoutException : ChatProviderException
    {
        public ChatTimeoutException(string message) : base(message) { }
        public ChatTimeoutException(string message, Exception innerException)
: base(message, innerException) { }
    }
}

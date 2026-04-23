namespace Iris.Application.AiIntegration.Exceptions
{
    public class ChatProviderException : Exception
    {
        public ChatProviderException(string message) : base(message) { }
        public ChatProviderException(string message, Exception innerException)
            : base(message, innerException) { }
    }
}

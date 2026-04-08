using Iris.Domain.AiIntegration;

namespace Iris.Application.AiIntegration.Models
{
    public record ChatMessage (ChatRole Role, string Content);
}

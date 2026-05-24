using System.Text.Json.Serialization;

namespace Iris.Domain.AiIntegration
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ChatRole
    {
        System,
        User,
        Assistant
    }
}
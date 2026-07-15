using Microsoft.Extensions.DependencyInjection;

using Iris.Application.AiIntegration.Tools;

namespace Iris.Application
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddApplication(this IServiceCollection services)
        {
            services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly));
            services.AddScoped<Conversations.IChatStreamOrchestrator, Conversations.ChatStreamOrchestrator>();
            services.AddScoped<Conversations.IConversationEventRecorder, Conversations.ConversationEventRecorder>();
            services.AddScoped<Conversations.IConversationTurnPreparer, Conversations.ConversationTurnPreparer>();
            services.AddScoped<IToolExecutionRecorder, ToolExecutionRecorder>();
            services.AddScoped<Personas.ISystemPromptAssembler, Personas.SystemPromptAssembler>();
            return services;
        }
    }
}

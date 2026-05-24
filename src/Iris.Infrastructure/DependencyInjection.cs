using Iris.Application.AiIntegration;
using Iris.Application.Conversations;
using Iris.Application.Conversations.Queries;
using Iris.Application.Personas;
using Iris.Infrastructure.AiIntegration;
using Iris.Infrastructure.Persistence;
using Iris.Infrastructure.Personas;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Infrastructure
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddInfrastructure(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("DefaultConnection")));

            // OpenRouter
            services.Configure<OpenRouterOptions>(
                configuration.GetSection(OpenRouterOptions.SectionName));

            services.AddHttpClient<IChatProvider, OpenRouterChatProvider>((sp, client) =>
            {
                var options = configuration
                    .GetSection(OpenRouterOptions.SectionName)
                    .Get<OpenRouterOptions>()!;

                client.BaseAddress = new Uri(options.BaseUrl);
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {options.ApiKey}");
                client.DefaultRequestHeaders.Add("HTTP-Referer", options.AppUrl);
                client.DefaultRequestHeaders.Add("X-OpenRouter-Title", options.AppTitle);
                client.Timeout = TimeSpan.FromSeconds(30);
            });
            
            services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly));
            services.AddScoped<IConversationQueries, ConversationQueries>();
            services.AddScoped<IEventStore, EfEventStore>();
            services.AddScoped<IPersonaService, PersonaService>();

            return services;
        }
    }
}

using Iris.Application.AiIntegration;
using Iris.Application.AiIntegration.Tools;
using Iris.Application.Conversations;
using Iris.Application.Conversations.Queries;
using Iris.Application.Identity.Interfaces;
using Iris.Application.Personas;
using Iris.Infrastructure.AiIntegration;
using Iris.Infrastructure.Identity;
using Iris.Infrastructure.Persistence;
using Iris.Infrastructure.Personas;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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

            //Identity
            services.AddIdentityCore<ApplicationUser>()
                .AddRoles<IdentityRole<Guid>>()
                .AddEntityFrameworkStores<AppDbContext>();

            // OpenRouter
            services.AddOptions<OpenRouterOptions>()
                .Bind(configuration.GetSection(OpenRouterOptions.SectionName))
                .Validate(options => !string.IsNullOrWhiteSpace(options.ApiKey), "OpenRouter ApiKey is required.")
                .Validate(options => Uri.IsWellFormedUriString(options.BaseUrl, UriKind.Absolute), "OpenRouter BaseUrl must be an absolute URI.")
                .Validate(options => !string.IsNullOrWhiteSpace(options.AppUrl), "OpenRouter AppUrl is required.")
                .Validate(options => Uri.IsWellFormedUriString(options.AppUrl, UriKind.Absolute), "OpenRouter AppUrl must be an absolute URI.")
                .Validate(options => !string.IsNullOrWhiteSpace(options.AppTitle), "OpenRouter AppTitle is required.")
                .ValidateOnStart();

            services.AddOptions<JwtOptions>()
                .Bind(configuration.GetSection(JwtOptions.SectionName))
                .Validate(options => !string.IsNullOrWhiteSpace(options.Secret), "Jwt Secret is required.")
                .Validate(options => !string.IsNullOrWhiteSpace(options.Issuer), "Jwt Issuer is required.")
                .Validate(options => !string.IsNullOrWhiteSpace(options.Audience), "Jwt Audience is required.")
                .Validate(options => options.AccessTokenExpirationMinutes > 0, "Jwt access token expiration must be greater than zero.")
                .Validate(options => options.RefreshTokenExpirationDays > 0, "Jwt refresh token expiration must be greater than zero.")
                .ValidateOnStart();

            services.AddOptions<IrisSystemPromptOptions>()
                .Bind(configuration.GetSection(IrisSystemPromptOptions.SectionName));

            services.AddHttpClient<IChatProvider, OpenRouterChatProvider>((sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<OpenRouterOptions>>().Value;

                client.BaseAddress = new Uri(options.BaseUrl);
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {options.ApiKey}");
                client.DefaultRequestHeaders.Add("HTTP-Referer", options.AppUrl);
                client.DefaultRequestHeaders.Add("X-OpenRouter-Title", options.AppTitle);
                client.Timeout = TimeSpan.FromSeconds(30);
            });
            
            services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly));
            services.AddScoped<IConversationQueries, ConversationQueries>();
            services.AddScoped<IEventStore, EfEventStore>();
            services.AddScoped<IConversationTurnRequestStore, EfConversationTurnRequestStore>();
            services.AddScoped<IToolResultPayloadStore, EfToolResultPayloadStore>();
            services.AddScoped<IToolRegistry, ToolRegistry>();
            services.AddScoped<ITool, GetCurrentTimeTool>();
            services.AddSingleton(TimeProvider.System);
            services.AddScoped<IPersonaService, PersonaService>();
            services.AddScoped<ISystemPromptService, SystemPromptService>();
            services.AddSingleton<IGlobalSystemPromptProvider, ConfigurationGlobalSystemPromptProvider>();
            services.AddScoped<ITokenService, JwtTokenService>();
            services.AddScoped<IAuthService, AuthService>();

            return services;
        }
    }
}

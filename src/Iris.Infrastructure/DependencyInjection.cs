using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Iris.Infrastructure.Persistence;
using Iris.Application.AiIntegration;

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

            //services.AddScoped<IChatProvider, [ChatProviderImplementation]>(); 

            return services;
        }
    }
}

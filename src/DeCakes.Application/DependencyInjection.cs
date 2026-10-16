using DeCakes.Application.Accounts;
using DeCakes.Application.Bakeries;
using DeCakes.Application.Cakes;
using DeCakes.Application.Health;
using Microsoft.Extensions.DependencyInjection;

namespace DeCakes.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<IHealthService, HealthService>();
        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<IBakeryCatalogService, BakeryCatalogService>();
        services.AddScoped<ICakeCatalogService, CakeCatalogService>();
        return services;
    }
}

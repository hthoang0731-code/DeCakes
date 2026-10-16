using DeCakes.Application.Accounts;
using DeCakes.Application.Bakeries;
using DeCakes.Application.Cakes;
using DeCakes.Infrastructure.Accounts;
using DeCakes.Infrastructure.Bakeries;
using DeCakes.Infrastructure.Cakes;
using DeCakes.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DeCakes.Infrastructure;

public static class DependencyInjection
{
    // Chưa kết nối database khi khởi động; connection được mở theo từng thao tác trong IMySqlConnectionFactory.
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IMySqlConnectionFactory>(new MySqlConnectionFactory(configuration));
        services.AddSingleton<IPasswordHashService, AspNetPasswordHashService>();
        services.AddScoped<IUserAccountStore, MySqlUserAccountStore>();
        services.AddScoped<IBakeryCatalogStore, MySqlBakeryCatalogStore>();
        services.AddScoped<ICakeCatalogStore, MySqlCakeCatalogStore>();
        return services;
    }
}

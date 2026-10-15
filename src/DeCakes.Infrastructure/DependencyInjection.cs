using DeCakes.Application.Accounts;
using DeCakes.Infrastructure.Accounts;
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
        return services;
    }
}

using DeCakes.Application.Accounts;
using DeCakes.Application.Admin;
using DeCakes.Infrastructure.Admin;
using DeCakes.Application.Bakeries;
using DeCakes.Application.BakeryWorkspace;
using DeCakes.Application.Cakes;
using DeCakes.Application.DesignRequests;
using DeCakes.Application.Orders;
using DeCakes.Application.Quotes;
using DeCakes.Infrastructure.Accounts;
using DeCakes.Application.BakeryOnboarding;
using DeCakes.Infrastructure.Bakeries;
using DeCakes.Infrastructure.BakeryOnboarding;
using DeCakes.Infrastructure.BakeryWorkspace;
using DeCakes.Infrastructure.Cakes;
using DeCakes.Infrastructure.DesignRequests;
using DeCakes.Infrastructure.Orders;
using DeCakes.Infrastructure.Quotes;
using DeCakes.Infrastructure.Email;
using DeCakes.Application.Subscriptions;
using DeCakes.Infrastructure.Persistence;
using DeCakes.Infrastructure.Subscriptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DeCakes.Infrastructure;

public static class DependencyInjection
{
    // Chưa kết nối database khi khởi động; connection được mở theo từng thao tác trong IMySqlConnectionFactory.
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IMySqlConnectionFactory>(new MySqlConnectionFactory(configuration));
        services.AddSingleton<IPasswordHashService, AspNetPasswordHashService>();
        services.AddScoped<IUserAccountStore, MySqlUserAccountStore>();
        services.AddScoped<IAccountSecurityStore, MySqlAccountSecurityStore>();
        services.AddScoped<IBakeryOnboardingStore, MySqlBakeryOnboardingStore>();
        services.AddScoped<ISubscriptionStore, MySqlSubscriptionStore>();
        // Tắt mặc định; kiểm tra cấu hình merchant khi dùng, không làm lỗi khởi động hoặc health.
        services.AddSingleton<ISubscriptionPaymentGateway>(new PayOsPaymentGateway(configuration));
        // Kiểm tra cấu hình email ở lần gửi; thiếu cấu hình không làm lỗi khởi động hoặc health.
        services.AddSingleton<IAccountEmailSender>(provider =>
            new ResendAccountEmailSender(configuration, provider.GetRequiredService<ILogger<ResendAccountEmailSender>>()));
        services.AddScoped<IAdminStore, MySqlAdminStore>();
        services.AddScoped<IBakeryCatalogStore, MySqlBakeryCatalogStore>();
        services.AddScoped<ICakeCatalogStore, MySqlCakeCatalogStore>();
        services.AddScoped<IDesignRequestStore, MySqlDesignRequestStore>();
        services.AddScoped<IQuoteStore, MySqlQuoteStore>();
        services.AddScoped<IOrderStore, MySqlOrderStore>();
        services.AddScoped<IBakeryWorkspaceStore, MySqlBakeryWorkspaceStore>();
        return services;
    }
}

using DeCakes.Application.Accounts;
using DeCakes.Application.Admin;
using DeCakes.Application.Bakeries;
using DeCakes.Application.BakeryOnboarding;
using DeCakes.Application.BakeryWorkspace;
using DeCakes.Application.Cakes;
using DeCakes.Application.DesignRequests;
using DeCakes.Application.Quotes;
using Microsoft.Extensions.DependencyInjection.Extensions;
using DeCakes.Application.Health;
using DeCakes.Application.Subscriptions;
using Microsoft.Extensions.DependencyInjection;

namespace DeCakes.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<IHealthService, HealthService>();
        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<IAccountSecurityService, AccountSecurityService>();
        services.AddScoped<IBakeryOnboardingService, BakeryOnboardingService>();
        services.AddScoped<IBakeryReviewService, BakeryReviewService>();
        services.AddScoped<IBakeryStaffService, BakeryStaffService>();
        services.AddScoped<ISubscriptionService, SubscriptionService>();
        services.AddScoped<IAdminService, AdminService>();
        services.AddScoped<IBakeryCatalogService, BakeryCatalogService>();
        services.AddScoped<ICakeCatalogService, CakeCatalogService>();
        services.AddScoped<IDesignRequestService, DesignRequestService>();
        services.AddScoped<IQuoteService, QuoteService>();
        services.AddScoped<IBakeryWorkspaceService, BakeryWorkspaceService>();
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }
}

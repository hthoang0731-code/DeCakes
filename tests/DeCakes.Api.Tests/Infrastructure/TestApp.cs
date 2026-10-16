using DeCakes.Api.Authentication;
using DeCakes.Application.Accounts;
using DeCakes.Domain.Accounts;
using DeCakes.Infrastructure.Accounts;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace DeCakes.Api.Tests.Infrastructure;

/// <summary>
/// Api chạy trong TestServer với store user trong bộ nhớ: không kết nối database thật.
/// Môi trường "Testing" nên không nạp user-secrets.
/// </summary>
public sealed class TestApp(
    int authPermitLimit = 1000,
    string environment = "Testing",
    Action<IServiceCollection>? configureServices = null) : WebApplicationFactory<Program>
{
    public InMemoryUserStore Store { get; } = new();

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero));

    public AspNetPasswordHashService Hasher { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IUserAccountStore>();
            services.AddSingleton<IUserAccountStore>(Store);
            services.RemoveAll<IPasswordHashService>();
            services.AddSingleton<IPasswordHashService>(Hasher);
            services.Configure<AuthRateLimitOptions>(o => o.PermitLimit = authPermitLimit);
            services.PostConfigure<CookieAuthenticationOptions>(
                CookieAuthenticationDefaults.AuthenticationScheme, o => o.TimeProvider = Time);
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            services.AddControllers().AddApplicationPart(typeof(TestApp).Assembly);
            configureServices?.Invoke(services);
        });
    }

    public ApiClient CreateApiClient() =>
        new(CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
            AllowAutoRedirect = false,
        }));

    public long AddUser(string email, string password, string role = SystemRoles.Customer, string status = UserStatuses.Active) =>
        Store.Add(email, Hasher.Hash(password), role, status);
}

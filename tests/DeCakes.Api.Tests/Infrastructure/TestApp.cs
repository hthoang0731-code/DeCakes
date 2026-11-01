using DeCakes.Api.Authentication;
using DeCakes.Application.Accounts;
using DeCakes.Application.BakeryOnboarding;
using DeCakes.Application.BakeryWorkspace;
using DeCakes.Application.DesignRequests;
using DeCakes.Application.Subscriptions;
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

    public RecordingEmailSender Emails { get; } = new();

    /// <summary>Giới hạn tần suất của quên/đặt lại mật khẩu và xác minh email; mặc định cao để không ảnh hưởng ca khác.</summary>
    public int RecoveryPermitLimit { get; init; } = 1000;

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero));

    public AspNetPasswordHashService Hasher { get; } = new();

    public ScriptedSubscriptionStore Subscriptions { get; } = new();

    public FakePaymentGateway Payments { get; } = new();

    private ScriptedBakeryOnboardingStore? _onboarding;

    public ScriptedBakeryOnboardingStore Onboarding => _onboarding ??= new ScriptedBakeryOnboardingStore(Store);

    private InMemoryDesignRequestStore? _designRequests;

    public InMemoryDesignRequestStore DesignRequests => _designRequests ??= new InMemoryDesignRequestStore(Time);

    private InMemoryBakeryWorkspaceStore? _bakeryWorkspace;

    public InMemoryBakeryWorkspaceStore BakeryWorkspace => _bakeryWorkspace ??= new InMemoryBakeryWorkspaceStore(DesignRequests);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IUserAccountStore>();
            services.AddSingleton<IUserAccountStore>(Store);
            services.RemoveAll<IAccountSecurityStore>();
            services.AddSingleton<IAccountSecurityStore>(Store);
            services.RemoveAll<IAccountEmailSender>();
            services.AddSingleton<IAccountEmailSender>(Emails);
            services.Configure<RecoveryRateLimitOptions>(o => o.PermitLimit = RecoveryPermitLimit);
            services.RemoveAll<IBakeryOnboardingStore>();
            services.AddSingleton<IBakeryOnboardingStore>(_ => Onboarding);
            services.RemoveAll<ISubscriptionStore>();
            services.AddSingleton<ISubscriptionStore>(Subscriptions);
            services.RemoveAll<ISubscriptionPaymentGateway>();
            services.AddSingleton<ISubscriptionPaymentGateway>(Payments);
            services.RemoveAll<IPasswordHashService>();
            services.AddSingleton<IPasswordHashService>(Hasher);
            services.RemoveAll<IDesignRequestStore>();
            services.AddSingleton<IDesignRequestStore>(_ => DesignRequests);
            services.RemoveAll<IBakeryWorkspaceStore>();
            services.AddSingleton<IBakeryWorkspaceStore>(_ => BakeryWorkspace);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
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

    /// <summary>Mặc định email đã xác minh; ca kiểm thử xác minh email truyền <paramref name="emailVerified"/> = false.</summary>
    public long AddUser(
        string email, string password, string role = SystemRoles.Customer, string status = UserStatuses.Active, bool emailVerified = true) =>
        Store.Add(email, Hasher.Hash(password), role, status, emailVerified);
}

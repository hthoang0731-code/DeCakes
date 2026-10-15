using System.Security.Claims;
using System.Threading.RateLimiting;
using DeCakes.Application.Accounts;
using DeCakes.Domain.Accounts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace DeCakes.Api.Authentication;

public sealed class AuthRateLimitOptions
{
    public int PermitLimit { get; set; } = 10;

    public int WindowSeconds { get; set; } = 60;
}

public static class AuthenticationSetup
{
    public static IServiceCollection AddDeCakesAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = AuthDefaults.AuthCookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.Path = "/";
                options.Cookie.Domain = null;
                // Phiên tuyệt đối 2 giờ, không gia hạn, không remember-me (cookie phiên của trình duyệt).
                options.ExpireTimeSpan = AuthDefaults.SessionLifetime;
                options.SlidingExpiration = false;

                options.Events = new CookieAuthenticationEvents
                {
                    // API: trả mã trạng thái thay vì redirect HTML.
                    OnRedirectToLogin = context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    },
                    OnRedirectToAccessDenied = context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return Task.CompletedTask;
                    },
                    OnValidatePrincipal = ValidatePrincipalAsync,
                };
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthPolicies.Customer, policy => policy.RequireRole(SystemRoles.Customer));
            options.AddPolicy(AuthPolicies.Bakery, policy => policy.RequireRole(SystemRoles.Bakery));
            options.AddPolicy(AuthPolicies.Admin, policy => policy.RequireRole(SystemRoles.Admin));
        });

        services.AddAntiforgery(options =>
        {
            options.HeaderName = AuthDefaults.CsrfHeaderName;
            options.Cookie.Name = AuthDefaults.AntiforgeryCookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.Path = "/";
        });

        services.Configure<AuthRateLimitOptions>(configuration.GetSection("Auth:RateLimit"));
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // Giới hạn theo IP cho register/login. Sau reverse proxy cần cấu hình ForwardedHeaders để có IP thật.
            options.AddPolicy(AuthDefaults.AuthRateLimitPolicy, httpContext =>
            {
                var limits = httpContext.RequestServices.GetRequiredService<IOptions<AuthRateLimitOptions>>().Value;
                return RateLimitPartition.GetFixedWindowLimiter(
                    httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = limits.PermitLimit,
                        Window = TimeSpan.FromSeconds(limits.WindowSeconds),
                        QueueLimit = 0,
                    });
            });
            options.OnRejected = async (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
                }

                var problemDetails = context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
                await problemDetails.WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = context.HttpContext,
                    ProblemDetails =
                    {
                        Status = StatusCodes.Status429TooManyRequests,
                        Title = "Quá nhiều yêu cầu. Thử lại sau.",
                    },
                });
            };
        });

        return services;
    }

    // Mỗi request có cookie: user phải còn tồn tại, active và role khớp claim; nếu không, từ chối cookie.
    // Lỗi database được để nổi lên (500), không coi là đăng xuất.
    private static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        // Endpoint đánh dấu [SkipSessionValidation] (chỉ health): bỏ qua cookie, coi request là ẩn danh.
        // Không truy vấn database và không xóa cookie.
        if (context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<SkipSessionValidationAttribute>() is not null)
        {
            context.RejectPrincipal();
            return;
        }

        var principal = context.Principal;
        var roleClaim = principal?.FindFirstValue(ClaimTypes.Role);
        if (!long.TryParse(principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) || roleClaim is null)
        {
            await RejectAsync(context);
            return;
        }

        var accounts = context.HttpContext.RequestServices.GetRequiredService<IAccountService>();
        var user = await accounts.GetActiveUserAsync(userId, context.HttpContext.RequestAborted);
        if (user is null || user.Role != roleClaim)
        {
            await RejectAsync(context);
            return;
        }

        context.HttpContext.Items[AuthDefaults.CurrentUserItemKey] = user;
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    public static ClaimsPrincipal CreatePrincipal(UserDto user) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Role, user.Role),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme,
            ClaimTypes.NameIdentifier,
            ClaimTypes.Role));
}

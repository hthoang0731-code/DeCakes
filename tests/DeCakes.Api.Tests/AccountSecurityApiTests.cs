using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using DeCakes.Api.Authentication;
using DeCakes.Api.Tests.Infrastructure;
using DeCakes.Application.Accounts;
using DeCakes.Domain.Accounts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeCakes.Api.Tests;

/// <summary>
/// Xác minh email, quên/đặt lại/đổi mật khẩu và thu hồi phiên qua HTTP, với store và email trong bộ nhớ.
/// Store bộ nhớ mô phỏng transaction bằng khóa; tính nguyên tử của SQL được kiểm thử riêng trên MySQL.
/// </summary>
public class AccountSecurityApiTests
{
    private const string Password = "mat-khau-du-dai-1";
    private const string NewPassword = "mat-khau-moi-du-dai-2";

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await JsonAsync(response)).TryGetProperty("code", out var code) ? code.GetString() : null;

    private static async Task<HttpResponseMessage> PostAsync(ApiClient client, string url, object? body = null) =>
        await client.PostAsync(url, body, await client.GetCsrfTokenAsync());

    private static async Task<ApiClient> LoggedInAsync(TestApp app, string email, string password = Password)
    {
        var client = app.CreateApiClient();
        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync(email, password)).StatusCode);
        return client;
    }

    private static object DesignRequestBody() => new
    {
        title = "Bánh sinh nhật",
        ideaDescription = "Bánh hai tầng.",
        budgetMax = 1_000_000m,
        pickupAt = "2026-10-20T09:30:00+07:00",
    };

    // ---------------- Xác minh email ----------------

    [Fact]
    public async Task Register_sends_verification_link_and_unverified_customer_can_sign_in_but_not_create_or_send_requests()
    {
        using var app = new TestApp();
        using var client = app.CreateApiClient();
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(client, "/api/auth/register",
            new { email = " Moi@Example.com ", password = Password, fullName = "Khách" })).StatusCode);

        var email = Assert.Single(app.Emails.Sent);
        Assert.Equal(("verify", "moi@example.com"), (email.Kind, email.To));
        Assert.Equal(AccountTokens.TokenLength, email.Token.Length);
        Assert.Equal(app.Time.GetUtcNow().UtcDateTime + AccountSecurityService.EmailVerificationLifetime, email.ExpiresAt);
        // Kho chỉ có digest, không có token thô.
        var stored = Assert.Single(app.Store.Tokens(app.Store.FindStored("moi@example.com")!.Account.Id));
        Assert.NotEqual(email.Token, Convert.ToBase64String(stored.Hash));

        using var session = await LoggedInAsync(app, "moi@example.com");
        Assert.False((await JsonAsync(await session.GetAsync("/api/auth/me"))).GetProperty("emailVerified").GetBoolean());

        var create = await PostAsync(session, "/api/design-requests", DesignRequestBody());
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(RequireVerifiedEmailAttribute.Code, await CodeAsync(create));
        var send = await PostAsync(session, "/api/design-requests/1/bakeries/1");
        Assert.Equal(HttpStatusCode.Forbidden, send.StatusCode);
        Assert.Equal(RequireVerifiedEmailAttribute.Code, await CodeAsync(send));
        // Đọc dữ liệu của mình vẫn được phép; không có yêu cầu nào được tạo.
        var list = await session.GetAsync("/api/design-requests");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(0, (await JsonAsync(list)).GetProperty("totalCount").GetInt64());

        Assert.Equal(HttpStatusCode.NoContent,
            (await PostAsync(client, "/api/auth/email-verification/confirm", new { token = email.Token })).StatusCode);

        Assert.True((await JsonAsync(await session.GetAsync("/api/auth/me"))).GetProperty("emailVerified").GetBoolean());
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(session, "/api/design-requests", DesignRequestBody())).StatusCode);
    }

    [Fact]
    public async Task Verification_gate_returns_403_before_body_validation()
    {
        using var app = new TestApp();
        app.AddUser("a@example.com", Password, emailVerified: false);
        using var client = await LoggedInAsync(app, "a@example.com");

        var response = await PostAsync(client, "/api/design-requests", new { title = "" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(RequireVerifiedEmailAttribute.Code, await CodeAsync(response));
    }

    [Fact]
    public async Task Register_still_succeeds_when_email_delivery_fails()
    {
        using var app = new TestApp();
        app.Emails.Fail = true;
        using var client = app.CreateApiClient();

        var response = await PostAsync(client, "/api/auth/register", new { email = "a@example.com", password = Password, fullName = "A" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        // Token đã cấp nhưng chưa tới người dùng; user chưa được xác minh.
        var user = app.Store.FindStored("a@example.com")!;
        Assert.False(user.Account.EmailVerified);
        Assert.Single(app.Store.Tokens(user.Account.Id));
    }

    public static TheoryData<string> InvalidVerificationTokens() =>
        ["reused", "garbage", "too-short", "expired", "wrong-purpose", "revoked-by-resend", "email-changed", "user-disabled"];

    [Theory]
    [MemberData(nameof(InvalidVerificationTokens))]
    public async Task Confirm_rejects_every_unusable_token_with_the_same_error(string scenario)
    {
        using var app = new TestApp();
        var id = app.AddUser("a@example.com", Password, emailVerified: false);
        using var session = await LoggedInAsync(app, "a@example.com");
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(session, "/api/auth/email-verification/resend")).StatusCode);
        var token = app.Emails.Last("verify").Token;

        switch (scenario)
        {
            case "reused":
                Assert.Equal(HttpStatusCode.NoContent,
                    (await PostAsync(session, "/api/auth/email-verification/confirm", new { token })).StatusCode);
                break;
            case "garbage": token = new string('!', AccountTokens.TokenLength); break;
            case "too-short": token = token[..^1]; break;
            case "expired": app.Time.Advance(AccountSecurityService.EmailVerificationLifetime); break;
            case "wrong-purpose":
                Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(session, "/api/auth/password/forgot", new { email = "a@example.com" })).StatusCode);
                token = app.Emails.Last("reset").Token;
                break;
            case "revoked-by-resend":
                app.Time.Advance(AccountSecurityService.IssueCooldown + TimeSpan.FromSeconds(1));
                Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(session, "/api/auth/email-verification/resend")).StatusCode);
                break;
            case "email-changed": app.Store.Update(id, a => a with { Email = "b@example.com" }); break;
            case "user-disabled": app.Store.Update(id, a => a with { Status = UserStatuses.Disabled }); break;
        }

        using var anonymous = app.CreateApiClient();
        var response = await PostAsync(anonymous, "/api/auth/email-verification/confirm", new { token });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("TOKEN_INVALID", await CodeAsync(response));
        if (scenario != "reused")
        {
            Assert.False(app.Store.Get(id).Account.EmailVerified);
        }
    }

    [Fact]
    public async Task Concurrent_confirmations_with_one_token_succeed_exactly_once()
    {
        using var app = new TestApp();
        app.AddUser("a@example.com", Password, emailVerified: false);
        using (var session = await LoggedInAsync(app, "a@example.com"))
        {
            await PostAsync(session, "/api/auth/email-verification/resend");
        }

        var token = app.Emails.Last("verify").Token;
        var clients = Enumerable.Range(0, 8).Select(_ => app.CreateApiClient()).ToList();
        var csrf = await Task.WhenAll(clients.Select(c => c.GetCsrfTokenAsync()));

        var responses = await Task.WhenAll(clients.Select((c, i) =>
            c.PostAsync("/api/auth/email-verification/confirm", new { token }, csrf[i])));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.NoContent));
        Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.BadRequest));
        clients.ForEach(c => c.Dispose());
    }

    [Fact]
    public async Task Resend_requires_sign_in_csrf_and_reports_verified_throttled_and_delivery_failure()
    {
        using var app = new TestApp();
        app.AddUser("verified@example.com", Password);
        app.AddUser("a@example.com", Password, emailVerified: false);

        using var anonymous = app.CreateApiClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(anonymous, "/api/auth/email-verification/resend")).StatusCode);

        using var verified = await LoggedInAsync(app, "verified@example.com");
        var already = await PostAsync(verified, "/api/auth/email-verification/resend");
        Assert.Equal(HttpStatusCode.Conflict, already.StatusCode);
        Assert.Equal("EMAIL_ALREADY_VERIFIED", await CodeAsync(already));

        using var session = await LoggedInAsync(app, "a@example.com");
        Assert.Equal(HttpStatusCode.BadRequest,
            (await session.PostAsync("/api/auth/email-verification/resend", null, csrfToken: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(session, "/api/auth/email-verification/resend")).StatusCode);

        var throttled = await PostAsync(session, "/api/auth/email-verification/resend");
        Assert.Equal((HttpStatusCode)429, throttled.StatusCode);
        Assert.Equal("EMAIL_RESEND_THROTTLED", await CodeAsync(throttled));
        Assert.Single(app.Emails.Sent);

        app.Time.Advance(AccountSecurityService.IssueCooldown + TimeSpan.FromSeconds(1));
        app.Emails.Fail = true;
        var failed = await PostAsync(session, "/api/auth/email-verification/resend");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal("EMAIL_DELIVERY_UNAVAILABLE", await CodeAsync(failed));
    }

    [Fact]
    public async Task Resend_is_capped_per_hour_per_user()
    {
        using var app = new TestApp();
        app.AddUser("a@example.com", Password, emailVerified: false);
        using var session = await LoggedInAsync(app, "a@example.com");

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < AccountSecurityService.MaxIssuesPerWindow + 1; i++)
        {
            statuses.Add((await PostAsync(session, "/api/auth/email-verification/resend")).StatusCode);
            app.Time.Advance(AccountSecurityService.IssueCooldown + TimeSpan.FromSeconds(1));
        }

        Assert.Equal(AccountSecurityService.MaxIssuesPerWindow, statuses.Count(s => s == HttpStatusCode.Accepted));
        Assert.Equal((HttpStatusCode)429, statuses[^1]);
    }

    // ---------------- Quên / đặt lại mật khẩu ----------------

    [Fact]
    public async Task Forgot_password_response_is_identical_for_existing_unknown_disabled_and_demo_accounts()
    {
        using var app = new TestApp();
        app.AddUser("active@example.com", Password);
        app.AddUser("disabled@example.com", Password, status: UserStatuses.Disabled);
        app.Store.Add("demo@example.com", null, isDemo: true);
        using var client = app.CreateApiClient();

        var bodies = new List<(HttpStatusCode, string)>();
        foreach (var email in new[] { "active@example.com", "unknown@example.com", "disabled@example.com", "demo@example.com" })
        {
            var response = await PostAsync(client, "/api/auth/password/forgot", new { email });
            bodies.Add((response.StatusCode, await response.Content.ReadAsStringAsync()));
        }

        Assert.All(bodies, b => Assert.Equal((HttpStatusCode.Accepted, ""), b));
        var sent = Assert.Single(app.Emails.Sent);
        Assert.Equal(("reset", "active@example.com"), (sent.Kind, sent.To));
    }

    [Fact]
    public async Task Forgot_password_validates_email_and_requires_csrf()
    {
        using var app = new TestApp();
        using var client = app.CreateApiClient();

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsync("/api/auth/password/forgot", new { email = "a@example.com" }, csrfToken: null)).StatusCode);
        var invalid = await PostAsync(client, "/api/auth/password/forgot", new { email = "khong-hop-le" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.True((await JsonAsync(invalid)).GetProperty("errors").TryGetProperty("email", out _));
    }

    [Fact]
    public async Task Forgot_password_delivery_failure_still_returns_202_and_logs_no_token_or_email()
    {
        var logs = new CapturingLoggerProvider();
        using var app = new TestApp(configureServices: s => s.AddSingleton<ILoggerProvider>(logs));
        app.AddUser("a@example.com", Password);
        app.Emails.Fail = true;
        using var client = app.CreateApiClient();

        var response = await PostAsync(client, "/api/auth/password/forgot", new { email = "a@example.com" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(app.Emails.Sent);
        var attempt = Assert.Single(app.Emails.Attempts);
        Assert.Contains(logs.Messages, m => m.Contains("đặt lại mật khẩu", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, m => m.Contains("a@example.com", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(logs.Messages, m => m.Contains(attempt.Token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Recovery_endpoints_are_rate_limited_per_ip()
    {
        using var app = new TestApp { RecoveryPermitLimit = 2 };
        using var client = app.CreateApiClient();

        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(client, "/api/auth/password/forgot", new { email = "a@example.com" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await PostAsync(client, "/api/auth/password/reset", new { token = "x", newPassword = NewPassword })).StatusCode);
        var limited = await PostAsync(client, "/api/auth/email-verification/confirm", new { token = "x" });

        Assert.Equal((HttpStatusCode)429, limited.StatusCode);
    }

    [Fact]
    public async Task Reset_password_changes_hash_verifies_email_and_revokes_every_existing_session()
    {
        using var app = new TestApp();
        var id = app.AddUser("a@example.com", Password, emailVerified: false);
        using var browserA = await LoggedInAsync(app, "a@example.com");
        using var browserB = await LoggedInAsync(app, "a@example.com");
        using var anonymous = app.CreateApiClient();

        await PostAsync(anonymous, "/api/auth/password/forgot", new { email = "a@example.com" });
        var token = app.Emails.Last("reset").Token;
        var reset = await PostAsync(anonymous, "/api/auth/password/reset", new { token, newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Null(anonymous.SetCookieFor(AuthDefaults.AuthCookieName)); // Không tự đăng nhập.
        Assert.Equal(HttpStatusCode.Unauthorized, (await browserA.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browserB.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.LoginAsync("a@example.com", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.LoginAsync("a@example.com", NewPassword)).StatusCode);
        Assert.True(app.Store.Get(id).Account.EmailVerified);

        var reuse = await PostAsync(anonymous, "/api/auth/password/reset", new { token, newPassword = "lan-thu-ba-du-dai-3" });
        Assert.Equal(HttpStatusCode.BadRequest, reuse.StatusCode);
        Assert.Equal("TOKEN_INVALID", await CodeAsync(reuse));
    }

    [Fact]
    public async Task Invalid_new_password_is_rejected_without_consuming_the_token()
    {
        using var app = new TestApp();
        app.AddUser("a@example.com", Password);
        using var client = app.CreateApiClient();
        await PostAsync(client, "/api/auth/password/forgot", new { email = "a@example.com" });
        var token = app.Emails.Last("reset").Token;

        var invalid = await PostAsync(client, "/api/auth/password/reset", new { token, newPassword = "ngan" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.True((await JsonAsync(invalid)).GetProperty("errors").TryGetProperty("newPassword", out _));

        Assert.Equal(HttpStatusCode.NoContent,
            (await PostAsync(client, "/api/auth/password/reset", new { token, newPassword = NewPassword })).StatusCode);
    }

    public static TheoryData<string> InvalidResetTokens() => ["expired", "superseded", "disabled-user", "verification-token", "after-password-change"];

    [Theory]
    [MemberData(nameof(InvalidResetTokens))]
    public async Task Reset_rejects_unusable_tokens_and_never_reopens_disabled_users(string scenario)
    {
        using var app = new TestApp();
        var id = app.AddUser("a@example.com", Password, emailVerified: false);
        using var client = app.CreateApiClient();
        await PostAsync(client, "/api/auth/password/forgot", new { email = "a@example.com" });
        var token = app.Emails.Last("reset").Token;
        var originalHash = app.Store.Get(id).PasswordHash;

        switch (scenario)
        {
            case "expired": app.Time.Advance(AccountSecurityService.PasswordResetLifetime); break;
            case "superseded":
                app.Time.Advance(AccountSecurityService.IssueCooldown + TimeSpan.FromSeconds(1));
                await PostAsync(client, "/api/auth/password/forgot", new { email = "a@example.com" });
                Assert.NotEqual(token, app.Emails.Last("reset").Token);
                break;
            case "disabled-user": app.Store.Update(id, a => a with { Status = UserStatuses.Disabled }); break;
            case "verification-token":
                using (var session = await LoggedInAsync(app, "a@example.com"))
                {
                    await PostAsync(session, "/api/auth/email-verification/resend");
                }

                token = app.Emails.Last("verify").Token;
                break;
            case "after-password-change":
                using (var session = await LoggedInAsync(app, "a@example.com"))
                {
                    Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(session, "/api/auth/password/change",
                        new { currentPassword = Password, newPassword = NewPassword })).StatusCode);
                }

                originalHash = app.Store.Get(id).PasswordHash;
                break;
        }

        var response = await PostAsync(client, "/api/auth/password/reset", new { token, newPassword = "mat-khau-cua-ke-gian-9" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("TOKEN_INVALID", await CodeAsync(response));
        Assert.Equal(originalHash, app.Store.Get(id).PasswordHash);
        if (scenario == "disabled-user")
        {
            Assert.Equal(UserStatuses.Disabled, app.Store.Get(id).Account.Status);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.LoginAsync("a@example.com", Password)).StatusCode);
        }
    }

    [Fact]
    public async Task Concurrent_resets_with_one_token_succeed_exactly_once()
    {
        using var app = new TestApp();
        var id = app.AddUser("a@example.com", Password);
        using (var client = app.CreateApiClient())
        {
            await PostAsync(client, "/api/auth/password/forgot", new { email = "a@example.com" });
        }

        var token = app.Emails.Last("reset").Token;
        var clients = Enumerable.Range(0, 8).Select(_ => app.CreateApiClient()).ToList();
        var csrf = await Task.WhenAll(clients.Select(c => c.GetCsrfTokenAsync()));

        var responses = await Task.WhenAll(clients.Select((c, i) =>
            c.PostAsync("/api/auth/password/reset", new { token, newPassword = $"mat-khau-song-song-{i:00}" }, csrf[i])));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.NoContent));
        Assert.Equal(1, app.Store.Get(id).Account.SessionVersion);
        clients.ForEach(c => c.Dispose());
    }

    // ---------------- Đổi mật khẩu và thu hồi phiên ----------------

    [Fact]
    public async Task Change_password_keeps_current_browser_signed_in_and_revokes_other_sessions()
    {
        using var app = new TestApp();
        app.AddUser("a@example.com", Password);
        using var current = await LoggedInAsync(app, "a@example.com");
        using var other = await LoggedInAsync(app, "a@example.com");
        var csrf = await current.GetCsrfTokenAsync();

        var response = await current.PostAsync("/api/auth/password/change", new { currentPassword = Password, newPassword = NewPassword }, csrf);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.NotNull(current.SetCookieFor(AuthDefaults.AuthCookieName));
        Assert.Equal(HttpStatusCode.OK, (await current.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/auth/me")).StatusCode);
        // CSRF token cũ vẫn dùng được vì user id không đổi.
        Assert.Equal(HttpStatusCode.NoContent, (await current.PostAsync("/api/auth/logout", null, csrf)).StatusCode);

        using var fresh = app.CreateApiClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await fresh.LoginAsync("a@example.com", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fresh.LoginAsync("a@example.com", NewPassword)).StatusCode);
    }

    [Fact]
    public async Task Change_password_requires_authentication_csrf_and_correct_current_password()
    {
        using var app = new TestApp();
        var id = app.AddUser("a@example.com", Password);
        var hash = app.Store.Get(id).PasswordHash;
        using var anonymous = app.CreateApiClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await PostAsync(anonymous, "/api/auth/password/change", new { currentPassword = Password, newPassword = NewPassword })).StatusCode);

        using var session = await LoggedInAsync(app, "a@example.com");
        Assert.Equal(HttpStatusCode.BadRequest, (await session.PostAsync("/api/auth/password/change",
            new { currentPassword = Password, newPassword = NewPassword }, csrfToken: null)).StatusCode);

        var wrong = await PostAsync(session, "/api/auth/password/change", new { currentPassword = "sai-mat-khau-hien-tai", newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("CURRENT_PASSWORD_INVALID", await CodeAsync(wrong));

        var same = await PostAsync(session, "/api/auth/password/change", new { currentPassword = Password, newPassword = Password });
        Assert.True((await JsonAsync(same)).GetProperty("errors").TryGetProperty("newPassword", out _));

        Assert.Equal(hash, app.Store.Get(id).PasswordHash);
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Cookie_issued_without_session_version_claim_is_rejected()
    {
        using var app = new TestApp();
        var id = app.AddUser("a@example.com", Password);
        using var client = app.CreateApiClient();
        await client.GetCsrfTokenAsync(); // Khởi tạo host.

        var options = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim(ClaimTypes.Role, SystemRoles.Customer)],
            CookieAuthenticationDefaults.AuthenticationScheme));
        var now = app.Time.GetUtcNow();
        var ticket = new AuthenticationTicket(
            principal,
            new AuthenticationProperties { IssuedUtc = now, ExpiresUtc = now + AuthDefaults.SessionLifetime },
            CookieAuthenticationDefaults.AuthenticationScheme);
        client.SetCookie(AuthDefaults.AuthCookieName, options.TicketDataFormat.Protect(ticket));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Admin_without_verified_email_keeps_admin_access()
    {
        using var app = new TestApp();
        app.AddUser("admin@example.com", Password, SystemRoles.Admin, emailVerified: false);
        using var session = await LoggedInAsync(app, "admin@example.com");

        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/test-only/admin")).StatusCode);
        Assert.False((await JsonAsync(await session.GetAsync("/api/auth/me"))).GetProperty("emailVerified").GetBoolean());
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages;

        public ILogger CreateLogger(string categoryName) => new Logger(_messages);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception) + exception);
        }
    }
}

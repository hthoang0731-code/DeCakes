using System.Net;
using System.Text.Json;
using DeCakes.Api.Authentication;
using DeCakes.Api.Tests.Infrastructure;
using DeCakes.Domain.Accounts;
using DeCakes.Infrastructure.Accounts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace DeCakes.Api.Tests;

public class AuthApiTests
{
    private const string Password = "mat-khau-du-dai-1";
    private const string AuthCookie = AuthDefaults.AuthCookieName;
    private const string CsrfCookie = AuthDefaults.AntiforgeryCookieName;

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task AssertCsrfRejectedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("CSRF token thiếu hoặc không hợp lệ.", (await JsonAsync(response)).GetProperty("title").GetString());
    }

    private static async Task<(TestApp App, ApiClient Client, long UserId)> LoggedInAsync(string role = SystemRoles.Customer)
    {
        var app = new TestApp();
        var id = app.AddUser("user@example.com", Password, role);
        var client = app.CreateApiClient();
        var response = await client.LoginAsync("user@example.com", Password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (app, client, id);
    }

    // ---------------- CSRF ----------------

    [Fact]
    public async Task Csrf_endpoint_returns_token_not_cached_and_secure_cookie()
    {
        using var app = new TestApp();
        using var client = app.CreateApiClient();

        var response = await client.GetAsync("/api/auth/csrf");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("token").GetString()));
        Assert.Equal("X-CSRF-TOKEN", body.GetProperty("headerName").GetString());
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        var cookie = client.SetCookieFor(CsrfCookie)!.ToLowerInvariant();
        Assert.Contains("secure", cookie);
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=strict", cookie);
        Assert.Contains("path=/", cookie);
        Assert.DoesNotContain("domain=", cookie);
    }

    [Fact]
    public async Task Register_without_csrf_token_is_rejected()
    {
        using var app = new TestApp();
        using var client = app.CreateApiClient();
        await client.GetCsrfTokenAsync(); // có cookie nhưng không gửi header

        var response = await client.PostAsync("/api/auth/register", new { email = "a@example.com", password = Password, fullName = "A" }, csrfToken: null);

        await AssertCsrfRejectedAsync(response);
        Assert.Null(app.Store.FindStored("a@example.com"));
    }

    [Fact]
    public async Task Login_with_wrong_csrf_token_is_rejected()
    {
        using var app = new TestApp();
        app.AddUser("a@example.com", Password);
        using var client = app.CreateApiClient();
        await client.GetCsrfTokenAsync();

        var response = await client.PostAsync("/api/auth/login", new { email = "a@example.com", password = Password }, "khong-phai-token");

        await AssertCsrfRejectedAsync(response);
        Assert.Null(client.SetCookieFor(AuthCookie));
    }

    [Fact]
    public async Task Csrf_token_from_another_client_is_rejected()
    {
        using var app = new TestApp();
        app.AddUser("a@example.com", Password);
        using var victim = app.CreateApiClient();
        using var attacker = app.CreateApiClient();
        await victim.GetCsrfTokenAsync();
        var attackerToken = await attacker.GetCsrfTokenAsync();

        var response = await victim.PostAsync("/api/auth/login", new { email = "a@example.com", password = Password }, attackerToken);

        await AssertCsrfRejectedAsync(response);
    }

    [Fact]
    public async Task Csrf_token_without_its_cookie_is_rejected()
    {
        using var app = new TestApp();
        app.AddUser("a@example.com", Password);
        using var client = app.CreateApiClient();
        var token = await client.GetCsrfTokenAsync();
        client.RemoveCookie(CsrfCookie);

        var response = await client.PostAsync("/api/auth/login", new { email = "a@example.com", password = Password }, token);

        await AssertCsrfRejectedAsync(response);
    }

    [Fact]
    public async Task Token_issued_before_login_is_not_valid_after_login()
    {
        using var app = new TestApp();
        app.AddUser("a@example.com", Password);
        using var client = app.CreateApiClient();
        var anonymousToken = await client.GetCsrfTokenAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/auth/login", new { email = "a@example.com", password = Password }, anonymousToken)).StatusCode);

        var stale = await client.PostAsync("/api/auth/logout", null, anonymousToken);
        await AssertCsrfRejectedAsync(stale);

        var fresh = await client.PostAsync("/api/auth/logout", null, await client.GetCsrfTokenAsync());
        Assert.Equal(HttpStatusCode.NoContent, fresh.StatusCode);
    }

    [Fact]
    public async Task Authenticated_write_requires_csrf_token()
    {
        var (app, client, _) = await LoggedInAsync();
        using (app)
        using (client)
        {
            await AssertCsrfRejectedAsync(await client.PostAsync("/api/test-only/authenticated-write", null, null));
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/test-only/authenticated-write", null, await client.GetCsrfTokenAsync())).StatusCode);
        }
    }

    // ---------------- Đăng ký ----------------

    [Fact]
    public async Task Register_creates_customer_returns_safe_dto_and_does_not_sign_in()
    {
        using var app = new TestApp();
        using var client = app.CreateApiClient();

        var response = await client.PostAsync(
            "/api/auth/register",
            new { email = "  New@Example.com ", password = Password, fullName = "Khách Mới", phone = "0901234567" },
            await client.GetCsrfTokenAsync());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(["id", "email", "fullName", "phone", "role"], body.EnumerateObject().Select(p => p.Name));
        Assert.Equal("new@example.com", body.GetProperty("email").GetString());
        Assert.Equal(SystemRoles.Customer, body.GetProperty("role").GetString());
        Assert.Null(client.SetCookieFor(AuthCookie));

        var stored = app.Store.FindStored("new@example.com")!;
        Assert.Equal(Application.Accounts.PasswordCheck.Success, PasswordCheck(app, stored.PasswordHash));
        Assert.False(stored.IsDemo);
        Assert.Equal(UserStatuses.Active, stored.Account.Status);
    }

    [Fact]
    public async Task Register_ignores_role_status_and_demo_flag_from_client()
    {
        using var app = new TestApp();
        using var client = app.CreateApiClient();

        var response = await client.PostAsync(
            "/api/auth/register",
            new { email = "hacker@example.com", password = Password, fullName = "X", role = "admin", systemRole = "admin", status = "disabled", isDemo = 1 },
            await client.GetCsrfTokenAsync());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var stored = app.Store.FindStored("hacker@example.com")!;
        Assert.Equal(SystemRoles.Customer, stored.Account.Role);
        Assert.Equal(UserStatuses.Active, stored.Account.Status);
        Assert.False(stored.IsDemo);
    }

    [Fact]
    public async Task Register_duplicate_email_returns_409()
    {
        using var app = new TestApp();
        app.AddUser("a@example.com", Password);
        using var client = app.CreateApiClient();

        var response = await client.PostAsync(
            "/api/auth/register", new { email = "A@EXAMPLE.COM", password = Password, fullName = "A" }, await client.GetCsrfTokenAsync());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Concurrent_registrations_with_same_email_create_one_user()
    {
        using var app = new TestApp();
        using var first = app.CreateApiClient();
        using var second = app.CreateApiClient();
        var firstToken = await first.GetCsrfTokenAsync();
        var secondToken = await second.GetCsrfTokenAsync();

        var responses = await Task.WhenAll(
            first.PostAsync("/api/auth/register", new { email = "race@example.com", password = Password, fullName = "A" }, firstToken),
            second.PostAsync("/api/auth/register", new { email = "race@example.com", password = Password, fullName = "B" }, secondToken));

        Assert.Equal(
            [HttpStatusCode.Created, HttpStatusCode.Conflict],
            responses.Select(r => r.StatusCode).OrderBy(c => (int)c));
    }

    [Fact]
    public async Task Register_invalid_input_returns_validation_problem()
    {
        using var app = new TestApp();
        using var client = app.CreateApiClient();

        var response = await client.PostAsync(
            "/api/auth/register", new { email = "khong-hop-le", password = "ngan", fullName = "" }, await client.GetCsrfTokenAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await JsonAsync(response)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("email", out _));
        Assert.True(errors.TryGetProperty("password", out _));
        Assert.True(errors.TryGetProperty("fullName", out _));
    }

    // ---------------- Đăng nhập ----------------

    [Fact]
    public async Task Login_sets_secure_session_cookie_and_returns_safe_dto()
    {
        using var app = new TestApp();
        var id = app.AddUser("a@example.com", Password);
        using var client = app.CreateApiClient();

        var response = await client.LoginAsync(" A@Example.com ", Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(id, body.GetProperty("id").GetInt64());
        Assert.Equal(["id", "email", "fullName", "phone", "role"], body.EnumerateObject().Select(p => p.Name));

        var cookie = client.SetCookieFor(AuthCookie)!.ToLowerInvariant();
        Assert.StartsWith("__host-decakes.auth=", cookie);
        Assert.Contains("; secure", cookie);
        Assert.Contains("; httponly", cookie);
        Assert.Contains("samesite=lax", cookie);
        Assert.Contains("path=/", cookie);
        Assert.DoesNotContain("domain=", cookie);
        // Cookie phiên của trình duyệt (không remember-me); hạn 2 giờ nằm trong ticket.
        Assert.DoesNotContain("expires=", cookie);
        Assert.DoesNotContain("max-age=", cookie);
    }

    public static TheoryData<string> FailedLoginCases() => ["unknown", "wrong-password", "null-hash", "corrupt-hash", "disabled"];

    [Theory]
    [MemberData(nameof(FailedLoginCases))]
    public async Task Failed_logins_return_the_same_401(string scenario)
    {
        using var app = new TestApp();
        var id = app.AddUser("a@example.com", Password);
        var password = Password;
        switch (scenario)
        {
            case "unknown": app.Store.Remove(id); break;
            case "wrong-password": password = Password + "x"; break;
            case "null-hash": app.Store.SetHash(id, null); break;
            case "corrupt-hash": app.Store.SetHash(id, "%%%khong-phai-base64%%%"); break;
            case "disabled": app.Store.Update(id, a => a with { Status = UserStatuses.Disabled }); break;
        }

        using var client = app.CreateApiClient();
        var response = await client.LoginAsync("a@example.com", password);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("Email hoặc mật khẩu không đúng.", body.GetProperty("title").GetString());
        Assert.False(body.TryGetProperty("detail", out _));
        Assert.Null(client.SetCookieFor(AuthCookie));
    }

    [Fact]
    public async Task Login_with_outdated_hash_rehashes_it()
    {
        using var app = new TestApp();
        var weak = new PasswordHasher<object>(Options.Create(new PasswordHasherOptions { IterationCount = 1_000 }))
            .HashPassword(new object(), Password);
        var id = app.Store.Add("a@example.com", weak);
        Assert.Equal(Application.Accounts.PasswordCheck.SuccessRehashNeeded, app.Hasher.Verify(weak, Password));
        using var client = app.CreateApiClient();

        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync("a@example.com", Password)).StatusCode);

        var newHash = app.Store.Get(id).PasswordHash;
        Assert.NotEqual(weak, newHash);
        Assert.Equal(Application.Accounts.PasswordCheck.Success, app.Hasher.Verify(newHash, Password));
    }

    // ---------------- Phiên đăng nhập ----------------

    [Fact]
    public async Task Me_requires_authentication_without_redirect()
    {
        using var app = new TestApp();
        using var client = app.CreateApiClient();

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Me_returns_current_user_and_logout_clears_cookie()
    {
        var (app, client, id) = await LoggedInAsync();
        using (app)
        using (client)
        {
            var me = await client.GetAsync("/api/auth/me");
            Assert.Equal(HttpStatusCode.OK, me.StatusCode);
            Assert.Equal(id, (await JsonAsync(me)).GetProperty("id").GetInt64());

            var logout = await client.PostAsync("/api/auth/logout", null, await client.GetCsrfTokenAsync());
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
            Assert.Contains("expires=Thu, 01 Jan 1970", client.SetCookieFor(AuthCookie)!, StringComparison.OrdinalIgnoreCase);

            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        }
    }

    public static TheoryData<string> RevokedUserCases() => ["disabled", "role-changed", "deleted"];

    [Theory]
    [MemberData(nameof(RevokedUserCases))]
    public async Task Existing_cookie_is_rejected_when_user_changes(string scenario)
    {
        var (app, client, id) = await LoggedInAsync();
        using (app)
        using (client)
        {
            switch (scenario)
            {
                case "disabled": app.Store.Update(id, a => a with { Status = UserStatuses.Disabled }); break;
                case "role-changed": app.Store.Update(id, a => a with { Role = SystemRoles.Admin }); break;
                case "deleted": app.Store.Remove(id); break;
            }

            var cookieBefore = client.Cookies[AuthCookie];
            var response = await client.GetAsync("/api/auth/me");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains("expires=Thu, 01 Jan 1970", client.SetCookieFor(AuthCookie)!, StringComparison.OrdinalIgnoreCase);

            // Gửi lại cookie cũ vẫn bị từ chối.
            client.SetCookie(AuthCookie, cookieBefore);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        }
    }

    [Fact]
    public async Task Session_expires_after_two_hours_without_sliding()
    {
        var (app, client, _) = await LoggedInAsync();
        using (app)
        using (client)
        {
            app.Time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(59));
            var stillValid = await client.GetAsync("/api/auth/me");
            Assert.Equal(HttpStatusCode.OK, stillValid.StatusCode);
            Assert.Null(client.SetCookieFor(AuthCookie)); // không gia hạn cookie

            app.Time.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        }
    }

    [Fact]
    public async Task Tampered_cookie_is_rejected()
    {
        var (app, client, _) = await LoggedInAsync();
        using (app)
        using (client)
        {
            var value = client.Cookies[AuthCookie];
            client.SetCookie(AuthCookie, value[..^4] + (value.EndsWith("AAAA") ? "BBBB" : "AAAA"));

            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        }
    }

    [Theory]
    [InlineData(SystemRoles.Customer, "/api/test-only/customer", HttpStatusCode.OK)]
    [InlineData(SystemRoles.Customer, "/api/test-only/bakery", HttpStatusCode.Forbidden)]
    [InlineData(SystemRoles.Customer, "/api/test-only/admin", HttpStatusCode.Forbidden)]
    [InlineData(SystemRoles.Bakery, "/api/test-only/bakery", HttpStatusCode.OK)]
    [InlineData(SystemRoles.Bakery, "/api/test-only/admin", HttpStatusCode.Forbidden)]
    [InlineData(SystemRoles.Admin, "/api/test-only/admin", HttpStatusCode.OK)]
    public async Task Role_policies_return_403_without_redirect(string role, string url, HttpStatusCode expected)
    {
        var (app, client, _) = await LoggedInAsync(role);
        using (app)
        using (client)
        {
            var response = await client.GetAsync(url);

            Assert.Equal(expected, response.StatusCode);
            Assert.Null(response.Headers.Location);
        }
    }

    [Fact]
    public async Task Anonymous_request_to_policy_endpoint_returns_401()
    {
        using var app = new TestApp();
        using var client = app.CreateApiClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/test-only/admin")).StatusCode);
    }

    // ---------------- Rate limit và lỗi hệ thống ----------------

    [Fact]
    public async Task Login_and_register_are_rate_limited()
    {
        using var app = new TestApp(authPermitLimit: 3);
        using var client = app.CreateApiClient();
        var token = await client.GetCsrfTokenAsync();

        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await client.PostAsync("/api/auth/login", new { email = "x@example.com", password = Password }, token)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsync("/api/auth/register", new { email = "y@example.com", password = Password, fullName = "Y" }, token)).StatusCode);

        var limited = await client.PostAsync("/api/auth/login", new { email = "x@example.com", password = Password }, token);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(429, (await JsonAsync(limited)).GetProperty("status").GetInt32());

        // Endpoint không thuộc policy vẫn hoạt động.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/csrf")).StatusCode);
    }

    [Fact]
    public async Task Database_failure_on_login_returns_masked_500_not_401()
    {
        using var app = new TestApp();
        using var client = app.CreateApiClient();
        var token = await client.GetCsrfTokenAsync();
        app.Store.Fail = true;

        var response = await client.PostAsync("/api/auth/login", new { email = "a@example.com", password = Password }, token);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SIMULATED", text);
        Assert.DoesNotContain("secret-user", text);
        Assert.DoesNotContain("db.internal", text);
    }

    [Fact]
    public async Task Database_failure_does_not_sign_user_out()
    {
        var (app, client, _) = await LoggedInAsync();
        using (app)
        using (client)
        {
            app.Store.Fail = true;
            var response = await client.GetAsync("/api/auth/me");

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Null(client.SetCookieFor(AuthCookie));
            Assert.DoesNotContain("SIMULATED", await response.Content.ReadAsStringAsync());

            app.Store.Fail = false;
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        }
    }

    // ---------------- Endpoint sẵn có ----------------

    [Fact]
    public async Task Health_still_works_and_openapi_is_hidden_outside_development()
    {
        using var app = new TestApp();
        using var client = app.CreateApiClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/openapi/v1.json")).StatusCode);
    }

    private static Application.Accounts.PasswordCheck PasswordCheck(TestApp app, string? hash) => app.Hasher.Verify(hash, Password);
}

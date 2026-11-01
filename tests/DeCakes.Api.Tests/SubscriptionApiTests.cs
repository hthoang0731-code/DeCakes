using System.Net;
using System.Text;
using System.Text.Json;
using DeCakes.Api.Authentication;
using DeCakes.Api.Tests.Infrastructure;
using DeCakes.Application.BakeryOnboarding;
using DeCakes.Application.Subscriptions;
using DeCakes.Domain.Accounts;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DeCakes.Api.Tests;

/// <summary>
/// HTTP của quyền gói, checkout và webhook với store và cổng thanh toán giả (SubscriptionService thật). Cổng giả chỉ có
/// trong kiểm thử; không chứng minh payOS thật. Hành vi dữ liệu nằm ở MySqlSubscriptionStoreTests.
/// </summary>
public class SubscriptionApiTests
{
    private const string Password = "mat-khau-du-dai-1";
    private const string Base = "/api/bakery-workspace/bakeries/7/subscription";
    private const string Webhook = "/api/payments/payos/webhook";

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await JsonAsync(response)).TryGetProperty("code", out var code) ? code.GetString() : null;

    private static async Task<(ApiClient Client, long UserId)> SignInAsync(TestApp app, string role = SystemRoles.Bakery, bool verified = true)
    {
        var email = $"{role}-{Guid.NewGuid():N}@example.com";
        var id = app.AddUser(email, Password, role, emailVerified: verified);
        var client = app.CreateApiClient();
        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync(email, Password)).StatusCode);
        return (client, id);
    }

    private static async Task<HttpResponseMessage> CheckoutAsync(ApiClient client, object body) =>
        await client.PostAsync($"{Base}/checkout", body, await client.GetCsrfTokenAsync());

    /// <summary>Gửi như nhà cung cấp: không cookie, không CSRF token, nội dung thô.</summary>
    private static async Task<HttpResponseMessage> PostWebhookAsync(TestApp app, string body)
    {
        using var http = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        return await http.PostAsync(Webhook, new StringContent(body, Encoding.UTF8, "application/json"));
    }

    // ---------------- Tình trạng quyền sử dụng ----------------

    [Fact]
    public async Task Status_is_computed_at_read_time_and_ends_exactly_at_period_end()
    {
        using var app = new TestApp();
        var now = app.Time.GetUtcNow().UtcDateTime;
        app.Subscriptions.Status = new SubscriptionStatusRecord(7, new SelectedPlanDto(1, "co-ban", "Cơ bản"), null, null,
            [new SubscriptionPeriodDto("trial", now.AddDays(-29), now.AddHours(1))]);
        var (staff, staffId) = await SignInAsync(app);
        using (staff)
        {
            var active = await JsonAsync(await staff.GetAsync(Base));
            Assert.True(active.GetProperty("accessActive").GetBoolean());
            Assert.Equal(now.AddHours(1), active.GetProperty("accessEndsAt").GetDateTime());

            // Không có job nào chạy: đúng mốc ends_at là hết quyền.
            app.Time.Advance(TimeSpan.FromHours(1));
            var expired = await JsonAsync(await staff.GetAsync(Base));
            Assert.False(expired.GetProperty("accessActive").GetBoolean());
            Assert.Equal(JsonValueKind.Null, expired.GetProperty("accessEndsAt").ValueKind);
            Assert.All(app.Subscriptions.Calls, c => Assert.Equal((staffId, 7L), (c.UserId, c.BakeryId)));
        }

        app.Subscriptions.Status = null;
        var (other, _) = await SignInAsync(app);
        using (other)
        {
            Assert.Equal("BAKERY_NOT_FOUND", await CodeAsync(await other.GetAsync(Base)));
        }

        var (customer, _) = await SignInAsync(app, SystemRoles.Customer);
        using (customer)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync(Base)).StatusCode);
        }
    }

    // ---------------- Checkout ----------------

    [Fact]
    public async Task Checkout_creates_link_from_server_price_and_does_not_activate_without_webhook()
    {
        using var app = new TestApp();
        var (owner, ownerId) = await SignInAsync(app);
        using (owner)
        {
            var response = await CheckoutAsync(owner, new { billingCycle = "month", amount = 1, planId = 99 });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var body = await JsonAsync(response);
            Assert.Equal(("pending", 199000m), (body.GetProperty("status").GetString(), body.GetProperty("amount").GetDecimal()));
            var link = Assert.Single(app.Payments.LinkRequests);
            Assert.Equal(199_000, link.Amount);
            Assert.InRange(link.OrderCode, 1, (1L << 48) - 1);
            Assert.Equal(app.Time.GetUtcNow().UtcDateTime + SubscriptionService.PaymentLinkLifetime, link.ExpiresAt);
            Assert.True(link.Description.Length <= 9);
            Assert.Contains(app.Subscriptions.Calls, c => c == (nameof(ISubscriptionStore.CreatePaymentAsync), ownerId, 7L));
            Assert.Empty(app.Subscriptions.Applied);

            // Quay về từ trang thanh toán chỉ đọc trạng thái ở server: vẫn pending cho tới khi webhook xác minh.
            var payment = await JsonAsync(await owner.GetAsync($"{Base}/payments/31"));
            Assert.Equal("pending", payment.GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task Checkout_requires_csrf_verified_owner_email_and_valid_cycle_before_touching_store()
    {
        using var app = new TestApp();
        var (unverified, _) = await SignInAsync(app, verified: false);
        using (unverified)
        {
            Assert.Equal(RequireVerifiedEmailAttribute.Code, await CodeAsync(await CheckoutAsync(unverified, new { billingCycle = "month" })));
        }

        var (owner, _) = await SignInAsync(app);
        using (owner)
        {
            Assert.Equal(HttpStatusCode.BadRequest,
                (await owner.PostAsync($"{Base}/checkout", new { billingCycle = "month" }, csrfToken: null)).StatusCode);
            var invalid = await CheckoutAsync(owner, new { billingCycle = "week" });
            Assert.True((await JsonAsync(invalid)).GetProperty("errors").TryGetProperty("billingCycle", out _));
        }

        Assert.Empty(app.Subscriptions.Calls);
        Assert.Empty(app.Payments.LinkRequests);
    }

    [Fact]
    public async Task Disabled_gateway_refuses_checkout_without_creating_a_transaction()
    {
        using var app = new TestApp();
        app.Payments.IsEnabled = false;
        var (owner, _) = await SignInAsync(app);
        using (owner)
        {
            var response = await CheckoutAsync(owner, new { billingCycle = "year" });

            Assert.Equal((HttpStatusCode.ServiceUnavailable, "PAYMENT_NOT_CONFIGURED"), (response.StatusCode, await CodeAsync(response)));
            Assert.Equal(0, app.Subscriptions.Count(nameof(ISubscriptionStore.CreatePaymentAsync)));
            Assert.Empty(app.Payments.LinkRequests);
        }

        // Health không phụ thuộc cấu hình thanh toán.
        using var anonymous = app.CreateApiClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/health")).StatusCode);
    }

    [Fact]
    public async Task Provider_failure_marks_transaction_failed_and_returns_502()
    {
        using var app = new TestApp();
        app.Payments.FailLink = true;
        var (owner, _) = await SignInAsync(app);
        using (owner)
        {
            var response = await CheckoutAsync(owner, new { billingCycle = "month" });

            Assert.Equal((HttpStatusCode.BadGateway, "PAYMENT_PROVIDER_ERROR"), (response.StatusCode, await CodeAsync(response)));
            Assert.Equal("failed", app.Subscriptions.PaymentStatus);
            Assert.DoesNotContain("HTTP 500", await response.Content.ReadAsStringAsync());
        }
    }

    [Theory]
    [InlineData(CheckoutOutcome.BakeryNotFound, HttpStatusCode.NotFound, "BAKERY_NOT_FOUND")]
    [InlineData(CheckoutOutcome.OwnerRequired, HttpStatusCode.Forbidden, "OWNER_REQUIRED")]
    [InlineData(CheckoutOutcome.ProfileNotActive, HttpStatusCode.Conflict, "PROFILE_STATUS_NOT_ALLOWED")]
    [InlineData(CheckoutOutcome.PlanNotSelected, HttpStatusCode.Conflict, "PLAN_NOT_SELECTED")]
    [InlineData(CheckoutOutcome.PlanNotAvailable, HttpStatusCode.Conflict, "PLAN_NOT_AVAILABLE")]
    [InlineData(CheckoutOutcome.PlanNotPriced, HttpStatusCode.Conflict, "PLAN_NOT_PRICED")]
    public async Task Checkout_store_outcomes_map_to_codes_and_never_call_provider(CheckoutOutcome outcome, HttpStatusCode status, string code)
    {
        using var app = new TestApp();
        app.Subscriptions.CreateOutcome = outcome;
        var (owner, _) = await SignInAsync(app);
        using (owner)
        {
            var response = await CheckoutAsync(owner, new { billingCycle = "month" });

            Assert.Equal((status, code), (response.StatusCode, await CodeAsync(response)));
            Assert.Empty(app.Payments.LinkRequests);
        }
    }

    // ---------------- Webhook ----------------

    [Fact]
    public async Task Webhook_needs_no_csrf_but_requires_valid_signature()
    {
        using var app = new TestApp();

        var valid = await PostWebhookAsync(app, "paid:123456:199000");
        var forged = await PostWebhookAsync(app, "{\"data\":{\"orderCode\":123456,\"amount\":199000},\"signature\":\"gia\"}");
        var unpaid = await PostWebhookAsync(app, "unpaid:123456");

        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unpaid.StatusCode);
        var applied = Assert.Single(app.Subscriptions.Applied);
        Assert.Equal((123456L, 199000m), (applied.OrderCode, applied.Amount));
    }

    [Theory]
    [InlineData(PaymentApplyOutcome.AlreadyProcessed)]
    [InlineData(PaymentApplyOutcome.Mismatch)]
    [InlineData(PaymentApplyOutcome.UnknownPayment)]
    public async Task Verified_webhook_is_acknowledged_for_duplicates_mismatches_and_unknown_orders(PaymentApplyOutcome outcome)
    {
        using var app = new TestApp();
        app.Subscriptions.ApplyOutcome = outcome;

        Assert.Equal(HttpStatusCode.OK, (await PostWebhookAsync(app, "paid:777:199000")).StatusCode);
    }

    [Fact]
    public async Task Webhook_returns_503_when_payments_are_not_configured()
    {
        using var app = new TestApp();
        app.Payments.IsEnabled = false;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await PostWebhookAsync(app, "paid:1:1")).StatusCode);
        Assert.Empty(app.Subscriptions.Applied);
    }

    [Fact]
    public async Task Csrf_exemption_is_limited_to_the_webhook_endpoint()
    {
        using var app = new TestApp();
        var (owner, _) = await SignInAsync(app);
        using (owner)
        {
            // Cùng phiên đăng nhập, POST khác không có CSRF token vẫn bị từ chối.
            foreach (var url in new[] { $"{Base}/checkout", "/api/auth/logout", "/api/auth/password/change", "/api/bakery-workspace/bakeries/7/profile/submit" })
            {
                var response = await owner.PostAsync(url, new { billingCycle = "month" }, csrfToken: null);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.Equal("CSRF token thiếu hoặc không hợp lệ.", (await JsonAsync(response)).GetProperty("title").GetString());
            }
        }

        Assert.Empty(app.Subscriptions.Calls);
    }
}

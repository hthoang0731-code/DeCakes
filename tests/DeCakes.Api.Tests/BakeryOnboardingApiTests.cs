using System.Net;
using System.Text.Json;
using DeCakes.Api.Authentication;
using DeCakes.Api.Tests.Infrastructure;
using DeCakes.Application.Accounts;
using DeCakes.Application.BakeryOnboarding;
using DeCakes.Domain.Accounts;
using DeCakes.Domain.BakeryOnboarding;

namespace DeCakes.Api.Tests;

/// <summary>
/// HTTP của đăng ký chủ tiệm, hồ sơ, gửi duyệt, staff/lời mời và admin review với store kịch bản: policy, CSRF, xác minh
/// email, user id lấy từ phiên và ánh xạ mã lỗi. Hành vi dữ liệu nằm ở MySqlBakeryOnboardingStoreTests.
/// </summary>
public class BakeryOnboardingApiTests
{
    private const string Password = "mat-khau-du-dai-1";
    private const string Workspace = "/api/bakery-workspace/bakeries/7";

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await JsonAsync(response)).TryGetProperty("code", out var code) ? code.GetString() : null;

    private static async Task<HttpResponseMessage> PostAsync(ApiClient client, string url, object? body = null) =>
        await client.PostAsync(url, body, await client.GetCsrfTokenAsync());

    private static async Task<HttpResponseMessage> SendAsync(ApiClient client, HttpMethod method, string url, object? body = null) =>
        await client.SendAsync(method, url, body, await client.GetCsrfTokenAsync());

    private static async Task<(ApiClient Client, long UserId)> SignInAsync(TestApp app, string role = SystemRoles.Bakery, bool verified = true)
    {
        var email = $"{role}-{Guid.NewGuid():N}@example.com";
        var id = app.AddUser(email, Password, role, emailVerified: verified);
        var client = app.CreateApiClient();
        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync(email, Password)).StatusCode);
        return (client, id);
    }

    private static BakeryProfileRecord Profile(string status = BakeryProfileStatuses.Draft) =>
        new(7, "Tiệm A", "tiem-a-abc123", null, null, "Huế", null, status, "owner", null, false, null);

    // ---------------- Đăng ký chủ tiệm ----------------

    [Fact]
    public async Task Register_bakery_fixes_role_on_server_sends_verification_and_does_not_sign_in()
    {
        using var app = new TestApp();
        using var client = app.CreateApiClient();

        var response = await PostAsync(client, "/api/auth/register-bakery", new
        {
            email = " Chu@Tiem.vn ", password = Password, fullName = "Chủ Tiệm", bakeryName = " Tiệm Bánh Đẹp ",
            role = "admin", systemRole = "admin", status = "active", profileStatus = "active", memberRole = "owner",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(100, body.GetProperty("bakeryId").GetInt64());
        Assert.Equal(SystemRoles.Bakery, body.GetProperty("user").GetProperty("role").GetString());
        Assert.False(body.GetProperty("user").GetProperty("emailVerified").GetBoolean());
        Assert.Null(client.SetCookieFor(AuthDefaults.AuthCookieName));
        Assert.Equal(("chu@tiem.vn", "Tiệm Bánh Đẹp", "tiem-banh-dep"),
            (app.Onboarding.LastOwner!.Email, app.Onboarding.LastOwner.BakeryName, app.Onboarding.LastOwner.SlugBase));
        Assert.Equal(("verify", "chu@tiem.vn"), (app.Emails.Last("verify").Kind, app.Emails.Last("verify").To));
    }

    [Fact]
    public async Task Register_bakery_validates_requires_csrf_and_never_changes_existing_account()
    {
        using var app = new TestApp();
        var customer = app.AddUser("khach@example.com", Password);
        using var client = app.CreateApiClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/auth/register-bakery",
            new { email = "a@example.com", password = Password, fullName = "A", bakeryName = "B" }, csrfToken: null)).StatusCode);

        var invalid = await PostAsync(client, "/api/auth/register-bakery", new { email = "x", password = "ngan", fullName = "", bakeryName = " " });
        var errors = (await JsonAsync(invalid)).GetProperty("errors");
        Assert.Equal(["bakeryName", "email", "fullName", "password"], errors.EnumerateObject().Select(p => p.Name).Order());

        var taken = await PostAsync(client, "/api/auth/register-bakery",
            new { email = "KHACH@example.com", password = Password, fullName = "A", bakeryName = "B" });
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal(SystemRoles.Customer, app.Store.Get(customer).Account.Role);
        Assert.Equal(0, app.Onboarding.Count(nameof(IBakeryOnboardingStore.CreateOwnerAsync)));
    }

    // ---------------- Quyền vào workspace và hồ sơ ----------------

    [Fact]
    public async Task Workspace_onboarding_endpoints_require_bakery_role()
    {
        using var app = new TestApp();
        using var anonymous = app.CreateApiClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Workspace}/profile")).StatusCode);

        foreach (var role in new[] { SystemRoles.Customer, SystemRoles.Admin })
        {
            var (client, _) = await SignInAsync(app, role);
            using (client)
            {
                Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Workspace}/profile")).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(client, $"{Workspace}/profile/submit")).StatusCode);
            }
        }

        Assert.Empty(app.Onboarding.Calls);
    }

    [Fact]
    public async Task Profile_uses_session_user_and_route_bakery_and_hides_non_members()
    {
        using var app = new TestApp();
        var (client, userId) = await SignInAsync(app);
        using (client)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Workspace}/profile")).StatusCode);

            app.Onboarding.Profile = Profile();
            var profile = await JsonAsync(await client.GetAsync($"{Workspace}/profile"));
            Assert.Equal(["addressLine", "phone", "plan"], profile.GetProperty("missingForSubmit").EnumerateArray().Select(e => e.GetString()));
            Assert.All(app.Onboarding.Calls, c => Assert.Equal((userId, 7L), (c.UserId, c.BakeryId)));
        }
    }

    public static TheoryData<OwnerActionOutcome, HttpStatusCode, string> OwnerOutcomes() => new()
    {
        { OwnerActionOutcome.BakeryNotFound, HttpStatusCode.NotFound, "BAKERY_NOT_FOUND" },
        { OwnerActionOutcome.OwnerRequired, HttpStatusCode.Forbidden, "OWNER_REQUIRED" },
        { OwnerActionOutcome.NotAllowedInStatus, HttpStatusCode.Conflict, "PROFILE_STATUS_NOT_ALLOWED" },
        { OwnerActionOutcome.PlanNotAvailable, HttpStatusCode.NotFound, "PLAN_NOT_FOUND" },
        { OwnerActionOutcome.PlanChangeNotAllowed, HttpStatusCode.Conflict, "PLAN_CHANGE_NOT_ALLOWED" },
    };

    [Theory]
    [MemberData(nameof(OwnerOutcomes))]
    public async Task Owner_action_outcomes_map_to_codes(OwnerActionOutcome outcome, HttpStatusCode status, string code)
    {
        using var app = new TestApp();
        app.Onboarding.OwnerOutcome = outcome;
        var (client, _) = await SignInAsync(app);
        using (client)
        {
            var update = await SendAsync(client, HttpMethod.Put, $"{Workspace}/profile", new { name = "Tiệm" });
            var plan = await SendAsync(client, HttpMethod.Put, $"{Workspace}/subscription/plan", new { planId = 3 });

            Assert.Equal((status, code), (update.StatusCode, await CodeAsync(update)));
            Assert.Equal((status, code), (plan.StatusCode, await CodeAsync(plan)));
        }
    }

    [Fact]
    public async Task Profile_update_validates_before_store_and_requires_csrf()
    {
        using var app = new TestApp();
        var (client, _) = await SignInAsync(app);
        using (client)
        {
            Assert.Equal(HttpStatusCode.BadRequest,
                (await client.SendAsync(HttpMethod.Put, $"{Workspace}/profile", new { name = "Tiệm" }, csrfToken: null)).StatusCode);
            var invalid = await SendAsync(client, HttpMethod.Put, $"{Workspace}/profile",
                new { name = "", city = new string('x', 101), phone = "abc" });
            var errors = (await JsonAsync(invalid)).GetProperty("errors");
            Assert.Equal(["city", "name", "phone"], errors.EnumerateObject().Select(p => p.Name).Order());
            Assert.Equal(HttpStatusCode.BadRequest,
                (await SendAsync(client, HttpMethod.Put, $"{Workspace}/subscription/plan", new { planId = 0 })).StatusCode);
            Assert.Empty(app.Onboarding.Calls);
        }
    }

    // ---------------- Gửi duyệt ----------------

    [Fact]
    public async Task Submit_requires_verified_email_before_reaching_the_store()
    {
        using var app = new TestApp();
        var (client, _) = await SignInAsync(app, verified: false);
        using (client)
        {
            var response = await PostAsync(client, $"{Workspace}/profile/submit");

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(RequireVerifiedEmailAttribute.Code, await CodeAsync(response));
            Assert.Equal(0, app.Onboarding.Count(nameof(IBakeryOnboardingStore.SubmitAsync)));
        }
    }

    public static TheoryData<SubmitProfileStatus, HttpStatusCode, string?> SubmitOutcomes() => new()
    {
        { SubmitProfileStatus.Submitted, HttpStatusCode.OK, null },
        { SubmitProfileStatus.BakeryNotFound, HttpStatusCode.NotFound, "BAKERY_NOT_FOUND" },
        { SubmitProfileStatus.OwnerRequired, HttpStatusCode.Forbidden, "OWNER_REQUIRED" },
        { SubmitProfileStatus.NotDraft, HttpStatusCode.Conflict, "PROFILE_NOT_DRAFT" },
        { SubmitProfileStatus.Incomplete, HttpStatusCode.Conflict, "PROFILE_INCOMPLETE" },
    };

    [Theory]
    [MemberData(nameof(SubmitOutcomes))]
    public async Task Submit_outcomes_map_to_codes(SubmitProfileStatus outcome, HttpStatusCode status, string? code)
    {
        using var app = new TestApp();
        app.Onboarding.Profile = Profile(BakeryProfileStatuses.PendingReview);
        app.Onboarding.SubmitResult = new SubmitProfileResult(outcome, outcome == SubmitProfileStatus.Incomplete ? ["city", "plan"] : []);
        var (client, _) = await SignInAsync(app);
        using (client)
        {
            var response = await PostAsync(client, $"{Workspace}/profile/submit");

            Assert.Equal(status, response.StatusCode);
            Assert.Equal(code, await CodeAsync(response));
            if (outcome == SubmitProfileStatus.Incomplete)
            {
                Assert.Equal(["city", "plan"], (await JsonAsync(response)).GetProperty("missing").EnumerateArray().Select(e => e.GetString()));
            }
        }
    }

    // ---------------- Staff và lời mời ----------------

    [Fact]
    public async Task Invite_requires_verified_owner_and_reports_email_delivery()
    {
        using var app = new TestApp();
        var (unverified, _) = await SignInAsync(app, verified: false);
        using (unverified)
        {
            var blocked = await PostAsync(unverified, $"{Workspace}/invitations", new { email = "nv@example.com" });
            Assert.Equal(RequireVerifiedEmailAttribute.Code, await CodeAsync(blocked));
        }

        var (client, ownerId) = await SignInAsync(app);
        using (client)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(client, $"{Workspace}/invitations", new { email = "khong-hop-le" })).StatusCode);

            var created = await PostAsync(client, $"{Workspace}/invitations", new { email = " NV@Example.com " });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var body = await JsonAsync(created);
            Assert.True(body.GetProperty("emailDelivered").GetBoolean());
            Assert.Equal("nv@example.com", body.GetProperty("invitation").GetProperty("email").GetString());
            var sent = app.Emails.Last("invite");
            Assert.Equal(app.Time.GetUtcNow().UtcDateTime + BakeryStaffService.InvitationLifetime, sent.ExpiresAt);
            Assert.Contains(app.Onboarding.Calls, c => c == (nameof(IBakeryOnboardingStore.CreateInvitationAsync), ownerId, 7L));

            app.Emails.Fail = true;
            var undelivered = await PostAsync(client, $"{Workspace}/invitations", new { email = "nv2@example.com" });
            Assert.False((await JsonAsync(undelivered)).GetProperty("emailDelivered").GetBoolean());

            app.Onboarding.InviteStatus = CreateInvitationStatus.AlreadyMember;
            Assert.Equal("ALREADY_MEMBER", await CodeAsync(await PostAsync(client, $"{Workspace}/invitations", new { email = "nv3@example.com" })));
            app.Onboarding.InviteStatus = CreateInvitationStatus.OwnerRequired;
            Assert.Equal("OWNER_REQUIRED", await CodeAsync(await PostAsync(client, $"{Workspace}/invitations", new { email = "nv3@example.com" })));
        }
    }

    public static TheoryData<RemoveMemberOutcome, HttpStatusCode, string?> RemoveOutcomes() => new()
    {
        { RemoveMemberOutcome.Removed, HttpStatusCode.NoContent, null },
        { RemoveMemberOutcome.OwnerRequired, HttpStatusCode.Forbidden, "OWNER_REQUIRED" },
        { RemoveMemberOutcome.BakeryNotFound, HttpStatusCode.NotFound, "BAKERY_NOT_FOUND" },
        { RemoveMemberOutcome.MemberNotFound, HttpStatusCode.NotFound, "MEMBER_NOT_FOUND" },
        { RemoveMemberOutcome.CannotRemoveOwner, HttpStatusCode.Conflict, "CANNOT_REMOVE_OWNER" },
    };

    [Theory]
    [MemberData(nameof(RemoveOutcomes))]
    public async Task Remove_member_outcomes_map_to_codes(RemoveMemberOutcome outcome, HttpStatusCode status, string? code)
    {
        using var app = new TestApp();
        app.Onboarding.RemoveResult = outcome;
        var (client, _) = await SignInAsync(app);
        using (client)
        {
            var response = await SendAsync(client, HttpMethod.Delete, $"{Workspace}/members/9");

            Assert.Equal(status, response.StatusCode);
            if (code is not null) Assert.Equal(code, await CodeAsync(response));
        }
    }

    [Fact]
    public async Task Accepting_invitation_checks_token_format_and_maps_role_conflict()
    {
        using var app = new TestApp();
        var (customer, customerId) = await SignInAsync(app, SystemRoles.Customer);
        using (customer)
        {
            var malformed = await PostAsync(customer, "/api/auth/invitations/accept", new { token = "sai" });
            Assert.Equal("TOKEN_INVALID", await CodeAsync(malformed));
            Assert.Equal(0, app.Onboarding.Count(nameof(IBakeryOnboardingStore.AcceptInvitationAsync)));

            app.Onboarding.AcceptResult = new AcceptInvitationResult(AcceptInvitationStatus.RoleNotAllowed);
            var role = await PostAsync(customer, "/api/auth/invitations/accept", new { token = AccountTokens.Create().Token });
            Assert.Equal((HttpStatusCode.Forbidden, "ROLE_NOT_ALLOWED"), (role.StatusCode, await CodeAsync(role)));
            Assert.Contains(app.Onboarding.Calls, c => c.Method == nameof(IBakeryOnboardingStore.AcceptInvitationAsync) && c.UserId == customerId);
            Assert.Equal(SystemRoles.Customer, app.Store.Get(customerId).Account.Role);
        }

        using var anonymous = app.CreateApiClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await PostAsync(anonymous, "/api/auth/invitations/accept", new { token = AccountTokens.Create().Token })).StatusCode);
    }

    [Fact]
    public async Task Accepting_with_new_account_validates_input_and_maps_existing_account()
    {
        using var app = new TestApp();
        using var client = app.CreateApiClient();
        var token = AccountTokens.Create().Token;

        var invalid = await PostAsync(client, "/api/auth/invitations/accept-new", new { token, password = "ngan", fullName = "" });
        Assert.Equal(["fullName", "password"], (await JsonAsync(invalid)).GetProperty("errors").EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("TOKEN_INVALID", await CodeAsync(await PostAsync(client, "/api/auth/invitations/accept-new",
            new { token = "sai", password = Password, fullName = "Nhân viên" })));

        var created = await PostAsync(client, "/api/auth/invitations/accept-new", new { token, password = Password, fullName = "Nhân viên" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(100, (await JsonAsync(created)).GetProperty("bakeryId").GetInt64());
        Assert.Null(client.SetCookieFor(AuthDefaults.AuthCookieName));

        app.Onboarding.AcceptResult = new AcceptInvitationResult(AcceptInvitationStatus.AccountExists);
        var exists = await PostAsync(client, "/api/auth/invitations/accept-new", new { token, password = Password, fullName = "Nhân viên" });
        Assert.Equal((HttpStatusCode.Conflict, "ACCOUNT_EXISTS"), (exists.StatusCode, await CodeAsync(exists)));
    }

    // ---------------- Admin review ----------------

    [Fact]
    public async Task Review_is_admin_only_requires_csrf_and_uses_admin_from_session()
    {
        using var app = new TestApp();
        var (bakery, _) = await SignInAsync(app);
        using (bakery)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(bakery, "/api/admin/bakeries/7/review", new { decision = "approve" })).StatusCode);
        }

        var (admin, adminId) = await SignInAsync(app, SystemRoles.Admin, verified: false);
        using (admin)
        {
            Assert.Equal(HttpStatusCode.BadRequest,
                (await admin.PostAsync("/api/admin/bakeries/7/review", new { decision = "approve" }, csrfToken: null)).StatusCode);
            Assert.Equal(0, app.Onboarding.Count(nameof(IBakeryOnboardingStore.ReviewAsync)));

            var approved = await PostAsync(admin, "/api/admin/bakeries/7/review", new { decision = "approve", adminUserId = 999 });
            Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
            Assert.Equal((nameof(IBakeryOnboardingStore.ReviewAsync), adminId, 7L), app.Onboarding.Calls[^1]);
            Assert.Equal(BakeryReviewEvents.Approved, app.Onboarding.LastReviewEvent);
        }
    }

    [Theory]
    [InlineData("reject", null, "reason")]
    [InlineData("request_changes", "   ", "reason")]
    [InlineData("suspend", "x", "decision")]
    [InlineData(null, null, "decision")]
    public async Task Review_validates_decision_and_reason(string? decision, string? reason, string field)
    {
        using var app = new TestApp();
        var (admin, _) = await SignInAsync(app, SystemRoles.Admin);
        using (admin)
        {
            var response = await PostAsync(admin, "/api/admin/bakeries/7/review", new { decision, reason });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True((await JsonAsync(response)).GetProperty("errors").TryGetProperty(field, out _));
            Assert.Equal(0, app.Onboarding.Count(nameof(IBakeryOnboardingStore.ReviewAsync)));
        }
    }

    [Theory]
    [InlineData(ReviewOutcome.NotPendingReview, HttpStatusCode.Conflict, "BAKERY_NOT_PENDING_REVIEW")]
    [InlineData(ReviewOutcome.BakeryNotFound, HttpStatusCode.NotFound, "BAKERY_NOT_FOUND")]
    [InlineData(ReviewOutcome.PlanNotSelected, HttpStatusCode.Conflict, "PLAN_NOT_SELECTED")]
    public async Task Review_outcomes_map_to_codes(ReviewOutcome outcome, HttpStatusCode status, string code)
    {
        using var app = new TestApp();
        app.Onboarding.ReviewResult = outcome;
        var (admin, _) = await SignInAsync(app, SystemRoles.Admin);
        using (admin)
        {
            var response = await PostAsync(admin, "/api/admin/bakeries/7/review", new { decision = "reject", reason = " Thiếu giấy phép " });

            Assert.Equal((status, code), (response.StatusCode, await CodeAsync(response)));
            Assert.Equal(("rejected", "Thiếu giấy phép"), (app.Onboarding.LastReviewEvent, app.Onboarding.LastReviewReason));
        }
    }

    [Fact]
    public async Task Plan_catalog_is_public_read_only()
    {
        using var app = new TestApp();
        using var client = app.CreateApiClient();

        var response = await client.GetAsync("/api/subscription-plans");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var plan = Assert.Single((await JsonAsync(response)).EnumerateArray());
        Assert.Equal(JsonValueKind.Null, plan.GetProperty("monthlyPrice").ValueKind);
    }
}

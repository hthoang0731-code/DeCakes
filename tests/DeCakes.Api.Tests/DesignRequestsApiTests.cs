using System.Net;
using System.Text.Json;
using DeCakes.Api.Tests.Infrastructure;
using DeCakes.Application.DesignRequests;
using DeCakes.Domain.Accounts;
using DeCakes.Domain.DesignRequests;

namespace DeCakes.Api.Tests;

/// <summary>API DesignRequests với store trong bộ nhớ (không database). Đồng hồ: FakeTimeProvider, 2026-10-15T08:00Z.</summary>
public class DesignRequestsApiTests
{
    private const string Password = "mat-khau-du-dai-1";
    private const string Base = "/api/design-requests";

    private static object Body(string title = "Bánh sinh nhật", string pickupAt = "2026-10-20T09:30:00+07:00", decimal budgetMax = 1_200_000.50m) => new
    {
        title,
        ideaDescription = "Bánh hai tầng, tông hồng.",
        budgetMin = 500_000m,
        budgetMax,
        sizeDescription = "20 cm",
        servings = 12,
        flavor = "Vani",
        inscriptionText = "Mừng sinh nhật",
        pickupAt,
    };

    private sealed class Session(TestApp app, ApiClient client, long userId) : IDisposable
    {
        public TestApp App { get; } = app;
        public ApiClient Client { get; } = client;
        public long UserId { get; } = userId;

        public async Task<HttpResponseMessage> PostAsync(string url, object? body) => await Client.PostAsync(url, body, await Client.GetCsrfTokenAsync());

        public async Task<HttpResponseMessage> PutAsync(string url, object body) => await Client.SendAsync(HttpMethod.Put, url, body, await Client.GetCsrfTokenAsync());

        public async Task<long> CreateAsync(object? body = null)
        {
            var response = await PostAsync(Base, body ?? Body());
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await JsonAsync(response)).GetProperty("id").GetInt64();
        }

        public void Dispose()
        {
            Client.Dispose();
            App.Dispose();
        }
    }

    private static async Task<Session> LoginAsync(TestApp? app = null, string role = SystemRoles.Customer, string email = "khach@example.com")
    {
        app ??= new TestApp();
        var id = app.AddUser(email, Password, role);
        var client = app.CreateApiClient();
        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync(email, Password)).StatusCode);
        return new Session(app, client, id);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task AssertProblemCodeAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await JsonAsync(response)).GetProperty("code").GetString());
    }

    // ---------- Xác thực, phân quyền, CSRF ----------

    [Fact]
    public async Task Anonymous_gets_401_and_non_customer_gets_403()
    {
        using var anonymousApp = new TestApp();
        using var anonymous = anonymousApp.CreateApiClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Base)).StatusCode);

        using var bakery = await LoginAsync(role: SystemRoles.Bakery);
        Assert.Equal(HttpStatusCode.Forbidden, (await bakery.Client.GetAsync(Base)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bakery.PostAsync(Base, Body())).StatusCode);
    }

    [Fact]
    public async Task Writes_require_csrf_token()
    {
        using var s = await LoginAsync();
        var id = await s.CreateAsync();
        s.App.DesignRequests.AddBakery(9, "Tiệm A");

        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsync(Base, Body(), csrfToken: null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.SendAsync(HttpMethod.Put, $"{Base}/{id}", Body("X"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsync($"{Base}/{id}/bakeries/9", null, csrfToken: null)).StatusCode);
        Assert.Equal(DesignRequestStatuses.Draft, s.App.DesignRequests.StatusOf(id));
    }

    // ---------- Tạo ----------

    [Fact]
    public async Task Create_returns_201_with_location_and_draft_owned_by_claim()
    {
        using var s = await LoginAsync();

        var response = await s.PostAsync(Base, new
        {
            title = "Bánh", ideaDescription = "Ý tưởng", budgetMax = 300_000m, pickupAt = "2026-10-20T09:30:00.250+07:00",
            // Các trường không thuộc hợp đồng bị bỏ qua: không đổi được chủ sở hữu, trạng thái hay tiền tệ.
            customerId = 999, status = "open", currency = "USD", isDemo = 1,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await JsonAsync(response);
        var id = body.GetProperty("id").GetInt64();
        Assert.EndsWith($"{Base}/{id}", response.Headers.Location!.ToString());
        Assert.Equal(DesignRequestStatuses.Draft, body.GetProperty("status").GetString());
        Assert.Equal("VND", body.GetProperty("currency").GetString());
        Assert.Equal("2026-10-20T02:30:00.25Z", body.GetProperty("pickupAt").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("budgetMin").ValueKind);
        Assert.Equal(s.UserId, s.App.DesignRequests.CustomerOf(id));
    }

    [Fact]
    public async Task Create_invalid_input_returns_validation_problem()
    {
        using var s = await LoginAsync();

        var response = await s.PostAsync(Base, new { title = "", budgetMax = 1.005m, servings = 70_000, pickupAt = "2026-10-20T09:30:00" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await JsonAsync(response)).GetProperty("errors");
        foreach (var field in new[] { "title", "ideaDescription", "budgetMax", "servings", "pickupAt" })
        {
            Assert.True(errors.TryGetProperty(field, out _), $"Thiếu lỗi cho {field}");
        }
    }

    [Fact]
    public async Task Create_with_wrong_json_type_is_400()
    {
        using var s = await LoginAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await s.PostAsync(Base, new { title = "A", ideaDescription = "B", budgetMax = "nhiều", pickupAt = "2026-10-20T09:30:00Z" })).StatusCode);
    }

    [Fact]
    public async Task Create_with_unavailable_cake_is_400_and_available_cake_is_accepted()
    {
        using var s = await LoginAsync();
        s.App.DesignRequests.AvailableCakes.Add(42);

        var rejected = await s.PostAsync(Base, new { title = "A", ideaDescription = "B", budgetMax = 1m, pickupAt = "2026-10-20T09:30:00Z", inspiredByCakeId = 41 });
        Assert.True((await JsonAsync(rejected)).GetProperty("errors").TryGetProperty("inspiredByCakeId", out _));

        var accepted = await s.PostAsync(Base, new { title = "A", ideaDescription = "B", budgetMax = 1m, pickupAt = "2026-10-20T09:30:00Z", inspiredByCakeId = 42 });
        Assert.Equal(42, (await JsonAsync(accepted)).GetProperty("inspiredByCakeId").GetInt64());
    }

    // ---------- Đọc và danh sách ----------

    [Fact]
    public async Task Other_customers_requests_are_404_everywhere()
    {
        using var owner = await LoginAsync();
        var id = await owner.CreateAsync();
        owner.App.DesignRequests.AddBakery(9, "Tiệm A");
        using var other = await LoginAsync(owner.App, email: "khac@example.com");

        await AssertProblemCodeAsync(await other.Client.GetAsync($"{Base}/{id}"), HttpStatusCode.NotFound, "REQUEST_NOT_FOUND");
        await AssertProblemCodeAsync(await other.PutAsync($"{Base}/{id}", Body("Chiếm")), HttpStatusCode.NotFound, "REQUEST_NOT_FOUND");
        await AssertProblemCodeAsync(await other.PostAsync($"{Base}/{id}/bakeries/9", null), HttpStatusCode.NotFound, "REQUEST_NOT_FOUND");
        await AssertProblemCodeAsync(await other.Client.GetAsync($"{Base}/{id}/bakeries"), HttpStatusCode.NotFound, "REQUEST_NOT_FOUND");
        await AssertProblemCodeAsync(await other.Client.GetAsync($"{Base}/999999"), HttpStatusCode.NotFound, "REQUEST_NOT_FOUND");
        Assert.Equal(0, (await JsonAsync(await other.Client.GetAsync(Base))).GetProperty("totalCount").GetInt64());

        Assert.Equal(DesignRequestStatuses.Draft, owner.App.DesignRequests.StatusOf(id));
        Assert.Equal("Bánh sinh nhật", (await JsonAsync(await owner.Client.GetAsync($"{Base}/{id}"))).GetProperty("title").GetString());
    }

    [Fact]
    public async Task List_pages_filters_and_validates_query()
    {
        using var s = await LoginAsync();
        var ids = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add(await s.CreateAsync(Body($"Yêu cầu {i}")));
            s.App.Time.Advance(TimeSpan.FromSeconds(1));
        }

        s.App.DesignRequests.AddBakery(9, "Tiệm A");
        Assert.Equal(HttpStatusCode.Created, (await s.PostAsync($"{Base}/{ids[0]}/bakeries/9", null)).StatusCode);

        var page1 = await JsonAsync(await s.Client.GetAsync($"{Base}?page=1&pageSize=2"));
        Assert.Equal(3, page1.GetProperty("totalCount").GetInt64());
        Assert.Equal([ids[2], ids[1]], page1.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetInt64()));
        Assert.Equal(2, page1.GetProperty("pageSize").GetInt32());

        var open = await JsonAsync(await s.Client.GetAsync($"{Base}?status=open"));
        Assert.Equal([ids[0]], open.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetInt64()));
        Assert.Equal(20, open.GetProperty("pageSize").GetInt32());

        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.GetAsync($"{Base}?pageSize=101")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.GetAsync($"{Base}?page=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.GetAsync($"{Base}?page=abc")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.GetAsync($"{Base}?status=pending")).StatusCode);
    }

    // ---------- Sửa ----------

    [Fact]
    public async Task Update_draft_succeeds_even_with_identical_data_then_409_after_send()
    {
        using var s = await LoginAsync();
        var id = await s.CreateAsync();
        s.App.DesignRequests.AddBakery(9, "Tiệm A");

        var updated = await s.PutAsync($"{Base}/{id}", Body("Tiêu đề mới"));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("Tiêu đề mới", (await JsonAsync(updated)).GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.OK, (await s.PutAsync($"{Base}/{id}", Body("Tiêu đề mới"))).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await s.PostAsync($"{Base}/{id}/bakeries/9", null)).StatusCode);
        await AssertProblemCodeAsync(await s.PutAsync($"{Base}/{id}", Body("Sửa sau khi gửi")), HttpStatusCode.Conflict, "REQUEST_NOT_DRAFT");
        Assert.Equal("Tiêu đề mới", (await JsonAsync(await s.Client.GetAsync($"{Base}/{id}"))).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Update_with_invalid_input_is_400_and_unknown_request_404()
    {
        using var s = await LoginAsync();
        var id = await s.CreateAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await s.PutAsync($"{Base}/{id}", Body(pickupAt: "2026-10-01T00:00:00Z"))).StatusCode);
        await AssertProblemCodeAsync(await s.PutAsync($"{Base}/999999", Body()), HttpStatusCode.NotFound, "REQUEST_NOT_FOUND");
    }

    // ---------- Gửi tiệm ----------

    [Fact]
    public async Task Send_returns_201_opens_request_and_lists_bakeries()
    {
        using var s = await LoginAsync();
        var id = await s.CreateAsync();
        s.App.DesignRequests.AddBakery(9, "Tiệm A");
        s.App.DesignRequests.AddBakery(10, "Tiệm B");

        var sent = await s.PostAsync($"{Base}/{id}/bakeries/9", null);
        Assert.Equal(HttpStatusCode.Created, sent.StatusCode);
        Assert.EndsWith($"{Base}/{id}/bakeries", sent.Headers.Location!.ToString());
        var bakery = await JsonAsync(sent);
        Assert.Equal(9, bakery.GetProperty("bakeryId").GetInt64());
        Assert.Equal("pending", bakery.GetProperty("status").GetString());
        Assert.Equal(DesignRequestStatuses.Open, s.App.DesignRequests.StatusOf(id));

        Assert.Equal(HttpStatusCode.Created, (await s.PostAsync($"{Base}/{id}/bakeries/10", null)).StatusCode);
        var list = await JsonAsync(await s.Client.GetAsync($"{Base}/{id}/bakeries"));
        Assert.Equal([9L, 10L], list.EnumerateArray().Select(x => x.GetProperty("bakeryId").GetInt64()));
    }

    [Fact]
    public async Task Send_conflicts_and_not_found_use_specific_codes()
    {
        using var s = await LoginAsync();
        var id = await s.CreateAsync();
        s.App.DesignRequests.AddBakery(9, "Tiệm A");
        s.App.DesignRequests.AddBakery(11, "Tiệm tạm ngưng", active: false);

        Assert.Equal(HttpStatusCode.Created, (await s.PostAsync($"{Base}/{id}/bakeries/9", null)).StatusCode);
        await AssertProblemCodeAsync(await s.PostAsync($"{Base}/{id}/bakeries/9", null), HttpStatusCode.Conflict, "REQUEST_ALREADY_SENT");
        await AssertProblemCodeAsync(await s.PostAsync($"{Base}/{id}/bakeries/11", null), HttpStatusCode.Conflict, "BAKERY_NOT_ACTIVE");
        await AssertProblemCodeAsync(await s.PostAsync($"{Base}/{id}/bakeries/12345", null), HttpStatusCode.NotFound, "BAKERY_NOT_FOUND");

        // Yêu cầu nhận sau 1 giờ; tua 61 phút (vẫn trong phiên 2 giờ) để thời gian nhận đã qua.
        var soon = await s.CreateAsync(Body(pickupAt: "2026-10-15T09:00:00Z"));
        s.App.Time.Advance(TimeSpan.FromMinutes(61));
        await AssertProblemCodeAsync(await s.PostAsync($"{Base}/{soon}/bakeries/9", null), HttpStatusCode.Conflict, "REQUEST_PICKUP_IN_PAST");
    }

    [Theory]
    [InlineData(SendToBakeryOutcome.InvalidArgument, HttpStatusCode.BadRequest, "INVALID_ARGUMENT")]
    [InlineData(SendToBakeryOutcome.RequestNotFound, HttpStatusCode.NotFound, "REQUEST_NOT_FOUND")]
    [InlineData(SendToBakeryOutcome.BakeryNotFound, HttpStatusCode.NotFound, "BAKERY_NOT_FOUND")]
    [InlineData(SendToBakeryOutcome.AlreadySent, HttpStatusCode.Conflict, "REQUEST_ALREADY_SENT")]
    [InlineData(SendToBakeryOutcome.RequestNotOpen, HttpStatusCode.Conflict, "REQUEST_NOT_OPEN")]
    [InlineData(SendToBakeryOutcome.PickupInPast, HttpStatusCode.Conflict, "REQUEST_PICKUP_IN_PAST")]
    [InlineData(SendToBakeryOutcome.BakeryNotActive, HttpStatusCode.Conflict, "BAKERY_NOT_ACTIVE")]
    [InlineData(SendToBakeryOutcome.AccountNotAllowed, HttpStatusCode.Forbidden, "ACCOUNT_NOT_ALLOWED")]
    [InlineData(SendToBakeryOutcome.BakerySubscriptionInactive, HttpStatusCode.Conflict, "BAKERY_SUBSCRIPTION_INACTIVE")]
    public async Task Every_send_outcome_maps_to_http(SendToBakeryOutcome outcome, HttpStatusCode status, string code)
    {
        using var s = await LoginAsync();
        var id = await s.CreateAsync();
        s.App.DesignRequests.ForcedSendOutcome = outcome;

        await AssertProblemCodeAsync(await s.PostAsync($"{Base}/{id}/bakeries/9", null), status, code);
    }

    [Fact]
    public async Task Store_failure_is_masked_500()
    {
        using var s = await LoginAsync();
        s.App.DesignRequests.Fail = true;

        var response = await s.PostAsync(Base, Body());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SIMULATED", text);
        Assert.DoesNotContain("db.internal", text);
    }

    [Fact]
    public async Task OpenApi_in_development_lists_design_request_endpoints()
    {
        using var app = new TestApp(environment: "Development");
        using var client = app.CreateApiClient();

        var text = await (await client.GetAsync("/openapi/v1.json")).Content.ReadAsStringAsync();

        Assert.Contains("/api/design-requests/{id}/bakeries/{bakeryId}", text);
        Assert.Contains("/api/auth/login", text);
    }
}

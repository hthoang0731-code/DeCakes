using System.Net;
using System.Text.Json;
using DeCakes.Api.Tests.Infrastructure;
using DeCakes.Application.DesignRequests;
using DeCakes.Application.Orders;
using DeCakes.Domain.Accounts;
using DeCakes.Domain.Orders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DeCakes.Api.Tests;

public class OrderApiTests
{
    private const string Accept = "/api/design-requests/7/quotes/9/accept";
    private const string CustomerOrders = "/api/orders";
    private const string RequestOrder = "/api/design-requests/7/order";
    private const string BakeryOrders = "/api/bakery-workspace/bakeries/3/orders";
    private const string Decision = BakeryOrders + "/31/decision";
    private const string Password = "mat-khau-kiem-thu-don-hang";

    private static TestApp App(OrderStore store) => new(configureServices: services =>
    {
        services.RemoveAll<IOrderStore>();
        services.AddSingleton<IOrderStore>(store);
    });

    private static async Task<(ApiClient Client, long UserId)> SignedInAsync(TestApp app, string role, bool verified = true)
    {
        var email = role + (verified ? "" : "-unverified") + "@example.com";
        var id = app.AddUser(email, Password, role, emailVerified: verified);
        var client = app.CreateApiClient();
        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync(email, Password)).StatusCode);
        return (client, id);
    }

    private static async Task<HttpResponseMessage> PostAsync(ApiClient client, string path, object? body = null) =>
        await client.PostAsync(path, body, await client.GetCsrfTokenAsync());

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static object Confirm => new { decision = "confirm" };

    [Theory]
    [InlineData(CustomerOrders)]
    [InlineData(CustomerOrders + "/31")]
    [InlineData(RequestOrder)]
    [InlineData(BakeryOrders)]
    [InlineData(BakeryOrders + "/31")]
    public async Task Reads_require_a_session_and_the_matching_role(string path)
    {
        var store = new OrderStore();
        using var app = App(store);
        using (var anonymous = app.CreateApiClient())
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        }

        var wrong = path.StartsWith(BakeryOrders, StringComparison.Ordinal)
            ? new[] { SystemRoles.Customer, SystemRoles.Admin }
            : new[] { SystemRoles.Bakery, SystemRoles.Admin };
        foreach (var role in wrong)
        {
            var (client, _) = await SignedInAsync(app, role);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
        }

        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData(Accept, SystemRoles.Customer)]
    [InlineData(Decision, SystemRoles.Bakery)]
    public async Task Writes_require_a_session_csrf_and_the_matching_role(string path, string role)
    {
        var store = new OrderStore();
        using var app = App(store);
        var body = path == Decision ? Confirm : null;

        using (var anonymous = app.CreateApiClient())
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(anonymous, path, body)).StatusCode);
        }

        var (client, _) = await SignedInAsync(app, role);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(path, body ?? new { }, csrfToken: null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(path, body ?? new { }, csrfToken: "khong-hop-le")).StatusCode);

        foreach (var other in new[] { SystemRoles.Customer, SystemRoles.Bakery, SystemRoles.Admin }.Where(r => r != role))
        {
            var (wrong, _) = await SignedInAsync(app, other);
            Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(wrong, path, body)).StatusCode);
        }

        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task Accept_requires_a_verified_email_but_reads_do_not()
    {
        var store = new OrderStore();
        using var app = App(store);
        var (client, _) = await SignedInAsync(app, SystemRoles.Customer, verified: false);

        var response = await PostAsync(client, Accept);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("EMAIL_NOT_VERIFIED", (await JsonAsync(response)).GetProperty("code").GetString());
        Assert.Equal(0, store.Calls);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(CustomerOrders + "/31")).StatusCode);
    }

    [Fact]
    public async Task Accept_uses_the_session_customer_and_returns_201_with_location_and_snapshot()
    {
        var store = new OrderStore();
        using var app = App(store);
        var (client, userId) = await SignedInAsync(app, SystemRoles.Customer);

        var response = await PostAsync(client, Accept, new { customerId = 999, quoteId = 1 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/api/orders/31", response.Headers.Location!.AbsolutePath);
        Assert.Equal($"accept:{userId}:7:9", store.WriteScope);
        Assert.Equal($"customer:{userId}:31", store.ReadScope);
        var data = await JsonAsync(response);
        var fields = new[] { "id", "requestId", "quoteId", "bakeryId", "bakeryName", "status", "agreedQuoteRevisionNo", "agreedPrice",
            "currency", "agreedPickupAt", "agreedSizeDescription", "agreedServings", "agreedFlavor", "agreedDecorationDescription",
            "agreedInscriptionText", "agreedNote", "agreedDesignAssetId", "agreedDesignAssetKind", "agreedDesignVersionNo",
            "bakeryDecidedAt", "rejectionReason", "pickedUpAt", "cancelledAt", "createdAt", "updatedAt" };
        Assert.Equal(fields.Order(), data.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(2_500_000.50m, data.GetProperty("agreedPrice").GetDecimal());
        Assert.Equal("pending_confirmation", data.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Customer_reads_use_the_session_actor_with_paging_and_filters()
    {
        var store = new OrderStore();
        using var app = App(store);
        var (client, userId) = await SignedInAsync(app, SystemRoles.Customer, verified: false);

        var list = await client.GetAsync(CustomerOrders + "?status=rejected_by_bakery&page=2&pageSize=3");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var page = await JsonAsync(list);
        Assert.Equal((2, 3), (page.GetProperty("page").GetInt32(), page.GetProperty("pageSize").GetInt32()));
        Assert.Equal(($"customer:{userId}", "rejected_by_bakery", 2, 3), (store.ListScope, store.Filter.Status, store.Filter.Page, store.Filter.PageSize));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(CustomerOrders)).StatusCode);
        Assert.Equal((null, 1, 20), store.Filter);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(RequestOrder)).StatusCode);
        Assert.Equal($"customer-request:{userId}:7", store.ReadScope);
    }

    [Theory]
    [InlineData("?status=expired")]
    [InlineData("?page=0")]
    [InlineData("?pageSize=101")]
    [InlineData("?status=confirmed%27%20OR%201=1")]
    public async Task Invalid_list_query_returns_400_without_calling_the_store(string query)
    {
        var store = new OrderStore();
        using var app = App(store);
        var (customer, _) = await SignedInAsync(app, SystemRoles.Customer);
        var (bakery, _) = await SignedInAsync(app, SystemRoles.Bakery);

        Assert.Equal(HttpStatusCode.BadRequest, (await customer.GetAsync(CustomerOrders + query)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await bakery.GetAsync(BakeryOrders + query)).StatusCode);
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData(CustomerOrders + "/31", SystemRoles.Customer, OrderReadOutcome.OrderNotFound, "ORDER_NOT_FOUND")]
    [InlineData(RequestOrder, SystemRoles.Customer, OrderReadOutcome.RequestNotFound, "REQUEST_NOT_FOUND")]
    [InlineData(RequestOrder, SystemRoles.Customer, OrderReadOutcome.OrderNotFound, "ORDER_NOT_FOUND")]
    [InlineData(BakeryOrders, SystemRoles.Bakery, OrderReadOutcome.BakeryNotFound, "BAKERY_NOT_FOUND")]
    [InlineData(BakeryOrders + "/31", SystemRoles.Bakery, OrderReadOutcome.BakeryNotFound, "BAKERY_NOT_FOUND")]
    [InlineData(BakeryOrders + "/31", SystemRoles.Bakery, OrderReadOutcome.OrderNotFound, "ORDER_NOT_FOUND")]
    public async Task Hidden_and_missing_orders_use_coded_404(string path, string role, OrderReadOutcome outcome, string code)
    {
        var store = new OrderStore { Read = outcome };
        using var app = App(store);
        var (client, _) = await SignedInAsync(app, role);

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(code, (await JsonAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Bakery_reads_and_decisions_use_the_route_bakery_and_session_actor()
    {
        var store = new OrderStore();
        using var app = App(store);
        var (client, userId) = await SignedInAsync(app, SystemRoles.Bakery);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(BakeryOrders + "?status=pending_confirmation")).StatusCode);
        Assert.Equal($"bakery:{userId}:3", store.ListScope);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(BakeryOrders + "/31")).StatusCode);
        Assert.Equal($"bakery:{userId}:3:31", store.ReadScope);

        store.Status = OrderStatuses.RejectedByBakery;
        var response = await PostAsync(client, Decision, new { decision = "reject", reason = "  Hết nguyên liệu  ", actorUserId = 999 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal(($"decide:{userId}:3:31", "reject", "Hết nguyên liệu"), (store.WriteScope, store.Decision, store.Reason));
        Assert.Equal($"bakery:{userId}:3:31", store.ReadScope);
        Assert.Equal("rejected_by_bakery", (await JsonAsync(response)).GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("{}", "decision")]
    [InlineData("""{"decision":"approve"}""", "decision")]
    [InlineData("""{"decision":"reject"}""", "reason")]
    [InlineData("""{"decision":"reject","reason":"   "}""", "reason")]
    [InlineData("""{"decision":"confirm","reason":"Đồng ý"}""", "reason")]
    public async Task Invalid_decisions_return_validation_problem_without_calling_the_store(string json, string field)
    {
        var store = new OrderStore();
        using var app = App(store);
        var (client, _) = await SignedInAsync(app, SystemRoles.Bakery);

        var response = await PostAsync(client, Decision, JsonDocument.Parse(json).RootElement);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await JsonAsync(response)).GetProperty("errors").TryGetProperty(field, out _));
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task Reason_longer_than_500_characters_or_wrong_json_type_is_rejected()
    {
        var store = new OrderStore();
        using var app = App(store);
        var (client, _) = await SignedInAsync(app, SystemRoles.Bakery);

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(client, Decision, new { decision = "reject", reason = new string('ệ', 501) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(client, Decision, new { decision = 1 })).StatusCode);
        Assert.Equal(0, store.Calls);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, Decision, new { decision = "reject", reason = new string('ệ', 500) })).StatusCode);
    }

    [Theory]
    [InlineData(OrderWriteOutcome.WorkflowNotReady, 503, "ORDER_WORKFLOW_NOT_READY")]
    [InlineData(OrderWriteOutcome.RequestNotFound, 404, "REQUEST_NOT_FOUND")]
    [InlineData(OrderWriteOutcome.QuoteNotFound, 404, "QUOTE_NOT_FOUND")]
    [InlineData(OrderWriteOutcome.InvalidArgument, 400, "INVALID_ARGUMENT")]
    [InlineData(OrderWriteOutcome.RequestNotOpen, 409, "REQUEST_NOT_OPEN")]
    [InlineData(OrderWriteOutcome.QuoteNotAvailable, 409, "QUOTE_NOT_AVAILABLE")]
    [InlineData(OrderWriteOutcome.QuoteExpired, 409, "QUOTE_EXPIRED")]
    [InlineData(OrderWriteOutcome.QuotePickupPassed, 409, "QUOTE_PICKUP_PASSED")]
    [InlineData(OrderWriteOutcome.DesignAssetInvalid, 409, "DESIGN_ASSET_INVALID")]
    [InlineData(OrderWriteOutcome.BakeryNotActive, 409, "BAKERY_NOT_ACTIVE")]
    [InlineData(OrderWriteOutcome.BakerySubscriptionInactive, 409, "BAKERY_SUBSCRIPTION_INACTIVE")]
    public async Task Accept_outcomes_map_to_coded_problems(OrderWriteOutcome outcome, int status, string code)
    {
        var store = new OrderStore { Write = new OrderWriteResult(outcome) };
        using var app = App(store);
        var (client, _) = await SignedInAsync(app, SystemRoles.Customer);

        var response = await PostAsync(client, Accept);

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal(code, (await JsonAsync(response)).GetProperty("code").GetString());
        Assert.Equal(0, store.Reads);
    }

    [Theory]
    [InlineData(OrderWriteOutcome.WorkflowNotReady, 503, "ORDER_WORKFLOW_NOT_READY")]
    [InlineData(OrderWriteOutcome.BakeryNotFound, 404, "BAKERY_NOT_FOUND")]
    [InlineData(OrderWriteOutcome.OrderNotFound, 404, "ORDER_NOT_FOUND")]
    [InlineData(OrderWriteOutcome.InvalidArgument, 400, "INVALID_ARGUMENT")]
    [InlineData(OrderWriteOutcome.RejectionReasonRequired, 400, "ORDER_REJECTION_REASON_REQUIRED")]
    [InlineData(OrderWriteOutcome.OrderNotPending, 409, "ORDER_NOT_PENDING")]
    [InlineData(OrderWriteOutcome.OrderPickupPassed, 409, "ORDER_PICKUP_PASSED")]
    [InlineData(OrderWriteOutcome.BakeryNotActive, 409, "BAKERY_NOT_ACTIVE")]
    public async Task Decision_outcomes_map_to_coded_problems(OrderWriteOutcome outcome, int status, string code)
    {
        var store = new OrderStore { Write = new OrderWriteResult(outcome) };
        using var app = App(store);
        var (client, _) = await SignedInAsync(app, SystemRoles.Bakery);

        var response = await PostAsync(client, Decision, Confirm);

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(code, (await JsonAsync(response)).GetProperty("code").GetString());
        Assert.Equal(0, store.Reads);
    }

    [Fact]
    public async Task Unknown_failures_are_masked_and_revoked_sessions_cannot_write()
    {
        var store = new OrderStore { Throw = true };
        using var app = App(store);
        var (customer, customerId) = await SignedInAsync(app, SystemRoles.Customer);
        var (bakery, bakeryUserId) = await SignedInAsync(app, SystemRoles.Bakery);

        foreach (var failure in new[] { await PostAsync(customer, Accept), await PostAsync(bakery, Decision, Confirm), await customer.GetAsync(CustomerOrders) })
        {
            Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
            var body = await failure.Content.ReadAsStringAsync();
            Assert.DoesNotContain("hidden-database-details", body);
            Assert.DoesNotContain("stackTrace", body, StringComparison.OrdinalIgnoreCase);
        }

        var calls = store.Calls;
        app.Store.Update(customerId, account => account with { Status = UserStatuses.Disabled });
        app.Store.Update(bakeryUserId, account => account with { Status = UserStatuses.Disabled });
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(customer, Accept)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(bakery, Decision, Confirm)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await customer.GetAsync(CustomerOrders)).StatusCode);
        Assert.Equal(calls, store.Calls);
    }

    private sealed class OrderStore : IOrderStore
    {
        private static readonly DateTime Created = new(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc);
        public OrderWriteResult Write { get; init; } = new(OrderWriteOutcome.Saved, 31);
        public OrderReadOutcome Read { get; init; } = OrderReadOutcome.Found;
        public string Status { get; set; } = OrderStatuses.PendingConfirmation;
        public bool Throw { get; init; }
        public int Calls { get; private set; }
        public int Reads { get; private set; }
        public string? WriteScope { get; private set; }
        public string? ReadScope { get; private set; }
        public string? ListScope { get; private set; }
        public string? Decision { get; private set; }
        public string? Reason { get; private set; }
        public (string? Status, int Page, int PageSize) Filter { get; private set; }

        private void Count()
        {
            Calls++;
            if (Throw) throw new InvalidOperationException("hidden-database-details");
        }

        private OrderDto Order(long id) => new(id, 7, 9, 3, "Tiệm", Status, 2, 2_500_000.50m, "VND", Created.AddDays(10), "20 cm", 12,
            "Vani", "Hoa kem", null, null, 15, "design_proposal", 2, Status == OrderStatuses.PendingConfirmation ? null : Created,
            Status == OrderStatuses.RejectedByBakery ? "Hết nguyên liệu" : null, null, null, Created, Created);

        private Task<OrderReadResult> Find(string scope, long orderId)
        {
            Count();
            Reads++;
            ReadScope = scope;
            return Task.FromResult(new OrderReadResult(Read, Read == OrderReadOutcome.Found ? Order(orderId) : null));
        }

        private Task<OrderListResult> List(string scope, string? status, int page, int pageSize)
        {
            Count();
            ListScope = scope;
            Filter = (status, page, pageSize);
            return Task.FromResult(new OrderListResult(Read, Read == OrderReadOutcome.Found
                ? new PagedResult<OrderDto>([Order(31)], page, pageSize, 1) : null));
        }

        public Task<OrderWriteResult> AcceptAsync(long customerId, long requestId, long quoteId, CancellationToken cancellationToken)
        {
            Count();
            WriteScope = $"accept:{customerId}:{requestId}:{quoteId}";
            return Task.FromResult(Write);
        }

        public Task<OrderWriteResult> DecideAsync(long actorUserId, long bakeryId, long orderId, string decision, string? reason, CancellationToken cancellationToken)
        {
            Count();
            (WriteScope, Decision, Reason) = ($"decide:{actorUserId}:{bakeryId}:{orderId}", decision, reason);
            return Task.FromResult(Write);
        }

        public Task<OrderReadResult> FindForCustomerAsync(long customerId, long orderId, CancellationToken cancellationToken) =>
            Find($"customer:{customerId}:{orderId}", orderId);
        public Task<OrderReadResult> FindForCustomerRequestAsync(long customerId, long requestId, CancellationToken cancellationToken) =>
            Find($"customer-request:{customerId}:{requestId}", 31);
        public Task<OrderReadResult> FindForBakeryAsync(long actorUserId, long bakeryId, long orderId, CancellationToken cancellationToken) =>
            Find($"bakery:{actorUserId}:{bakeryId}:{orderId}", orderId);
        public Task<OrderListResult> ListForCustomerAsync(long customerId, string? status, int page, int pageSize, CancellationToken cancellationToken) =>
            List($"customer:{customerId}", status, page, pageSize);
        public Task<OrderListResult> ListForBakeryAsync(long actorUserId, long bakeryId, string? status, int page, int pageSize, CancellationToken cancellationToken) =>
            List($"bakery:{actorUserId}:{bakeryId}", status, page, pageSize);
    }
}

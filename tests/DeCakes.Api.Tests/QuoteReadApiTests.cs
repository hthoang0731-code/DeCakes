using System.Net;
using System.Text.Json;
using DeCakes.Api.Tests.Infrastructure;
using DeCakes.Application.DesignRequests;
using DeCakes.Application.Quotes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DeCakes.Api.Tests;

public class QuoteReadApiTests
{
    private const string CustomerList = "/api/design-requests/7/quotes";
    private const string BakeryList = "/api/bakery-workspace/bakeries/3/requests/7/quotes";
    private const string Password = "mat-khau-kiem-thu-bao-gia";

    private static TestApp App(QuoteStore store) => new(configureServices: services =>
    {
        services.RemoveAll<IQuoteStore>();
        services.AddSingleton<IQuoteStore>(store);
    });

    private static async Task<ApiClient> SignedInAsync(TestApp app, string role, bool verified = true)
    {
        var email = role + "@example.com";
        app.AddUser(email, Password, role, emailVerified: verified);
        var client = app.CreateApiClient();
        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync(email, Password)).StatusCode);
        return client;
    }

    [Theory]
    [InlineData(CustomerList)]
    [InlineData(CustomerList + "/9")]
    [InlineData(BakeryList)]
    [InlineData(BakeryList + "/9")]
    public async Task All_reads_require_a_session(string path)
    {
        var store = new QuoteStore();
        using var app = App(store);
        using var client = app.CreateApiClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData("customer", BakeryList)]
    [InlineData("customer", BakeryList + "/9")]
    [InlineData("bakery", CustomerList)]
    [InlineData("bakery", CustomerList + "/9")]
    [InlineData("admin", CustomerList)]
    [InlineData("admin", BakeryList)]
    public async Task Roles_cannot_read_other_workspaces(string role, string path)
    {
        var store = new QuoteStore();
        using var app = App(store);
        using var client = await SignedInAsync(app, role);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData("customer", CustomerList + "/9", 0)]
    [InlineData("bakery", BakeryList + "/9", 3)]
    public async Task Detail_uses_session_actor_and_returns_only_the_public_quote_fields(string role, string path, long bakery)
    {
        var store = new QuoteStore();
        using var app = App(store);
        using var client = await SignedInAsync(app, role, verified: false);
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement;
        var fields = new[] { "id", "requestId", "bakeryId", "bakeryName", "revisionNo", "status", "price", "currency",
            "sizeDescription", "servings", "flavor", "decorationDescription", "inscriptionText", "note",
            "estimatedPickupAt", "validUntil", "designAssetId", "withdrawnAt", "createdAt", "updatedAt", "isCurrent", "isExpired" };
        Assert.Equal(fields.Order(), data.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(680_000m, data.GetProperty("price").GetDecimal());
        Assert.True(data.GetProperty("isCurrent").GetBoolean());
        Assert.False(data.GetProperty("isExpired").GetBoolean());
        var me = await client.GetAsync("/api/auth/me");
        using var meDocument = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        Assert.Equal((meDocument.RootElement.GetProperty("id").GetInt64(), bakery, 7L, 9L), store.Scope);
    }

    [Theory]
    [InlineData("customer", CustomerList, QuoteReadOutcome.RequestNotFound, "REQUEST_NOT_FOUND")]
    [InlineData("customer", CustomerList + "/9", QuoteReadOutcome.QuoteNotFound, "QUOTE_NOT_FOUND")]
    [InlineData("bakery", BakeryList, QuoteReadOutcome.BakeryNotFound, "BAKERY_NOT_FOUND")]
    [InlineData("bakery", BakeryList + "/9", QuoteReadOutcome.RequestNotFound, "REQUEST_NOT_FOUND")]
    [InlineData("bakery", BakeryList + "/9", QuoteReadOutcome.QuoteNotFound, "QUOTE_NOT_FOUND")]
    public async Task Hidden_and_missing_objects_use_coded_404(string role, string path, QuoteReadOutcome outcome, string code)
    {
        var store = new QuoteStore { Outcome = outcome };
        using var app = App(store);
        using var client = await SignedInAsync(app, role);
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("?status=expired")]
    [InlineData("?page=0")]
    [InlineData("?pageSize=101")]
    [InlineData("?status=submitted%27%20OR%201=1")]
    public async Task Invalid_query_does_not_reach_the_store(string query)
    {
        var store = new QuoteStore();
        using var app = App(store);
        using var client = await SignedInAsync(app, "customer");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(CustomerList + query)).StatusCode);
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData("customer", CustomerList)]
    [InlineData("bakery", BakeryList)]
    public async Task Lists_have_paging_and_unknown_failures_are_masked(string role, string path)
    {
        var store = new QuoteStore();
        using var app = App(store);
        using var client = await SignedInAsync(app, role);
        var response = await client.GetAsync(path + "?status=submitted&page=2&pageSize=3");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, document.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(3, document.RootElement.GetProperty("pageSize").GetInt32());
        Assert.Equal(("submitted", 2, 3), store.Filter);

        store.Throw = true;
        var failure = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
        var body = await failure.Content.ReadAsStringAsync();
        Assert.DoesNotContain("hidden-database-details", body);
        Assert.DoesNotContain("stackTrace", body, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class QuoteStore : IQuoteStore
    {
        private static readonly DateTime Created = new(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc);
        private static readonly QuoteDto Data = new(9, 7, 3, "Tiệm", 1, "submitted", 680_000m, "VND", "20 cm", 10,
            "Vani", "Hoa", null, null, Created.AddDays(5), Created.AddDays(2), null, null, Created, Created);
        public QuoteReadOutcome Outcome { get; init; } = QuoteReadOutcome.Found;
        public bool Throw { get; set; }
        public int Calls { get; private set; }
        public (long Actor, long Bakery, long Request, long Quote) Scope { get; private set; }
        public (string? Status, int Page, int PageSize) Filter { get; private set; }
        private void Capture(long actor, long bakery, long request, long quote)
        { Calls++; Scope = (actor, bakery, request, quote); if (Throw) throw new InvalidOperationException("hidden-database-details"); }
        public Task<QuoteReadResult> FindForBakeryAsync(long actorUserId, long bakeryId, long requestId, long quoteId, CancellationToken cancellationToken)
        { Capture(actorUserId, bakeryId, requestId, quoteId); return Task.FromResult(new QuoteReadResult(Outcome, Outcome == QuoteReadOutcome.Found ? Data : null)); }
        public Task<QuoteReadResult> FindForCustomerAsync(long customerId, long requestId, long quoteId, CancellationToken cancellationToken)
            => FindForBakeryAsync(customerId, 0, requestId, quoteId, cancellationToken);
        public Task<QuoteListResult> ListForBakeryAsync(long actorUserId, long bakeryId, long requestId, string? status, int page, int pageSize, CancellationToken cancellationToken)
        { Capture(actorUserId, bakeryId, requestId, 0); Filter = (status, page, pageSize); return Task.FromResult(new QuoteListResult(Outcome, Outcome == QuoteReadOutcome.Found ? new PagedResult<QuoteDto>([Data], page, pageSize, 1) : null)); }
        public Task<QuoteListResult> ListForCustomerAsync(long customerId, long requestId, string? status, int page, int pageSize, CancellationToken cancellationToken)
            => ListForBakeryAsync(customerId, 0, requestId, status, page, pageSize, cancellationToken);
    }
}

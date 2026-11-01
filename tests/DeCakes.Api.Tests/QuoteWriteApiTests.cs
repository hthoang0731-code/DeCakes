using System.Net;
using System.Text.Json;
using DeCakes.Api.Tests.Infrastructure;
using DeCakes.Application.Quotes;
using DeCakes.Domain.Accounts;
using DeCakes.Domain.Quotes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DeCakes.Api.Tests;

public class QuoteWriteApiTests
{
    private const string Base = "/api/bakery-workspace/bakeries/3/requests/7/quotes";
    private const string Revise = Base + "/9/revisions";
    private const string Withdraw = Base + "/9/withdraw";
    private const string Password = "mat-khau-kiem-thu-ghi-bao-gia";

    // Thời gian TestApp: 2026-10-15T08:00Z. Giá vượt ngân sách và giờ nhận khác yêu cầu vẫn hợp lệ.
    private static object Body(object? price = null, string validUntil = "2026-10-20T00:00:00Z", object? servings = null) => new
    {
        price = price ?? 2_500_000.50m,
        sizeDescription = "20 cm",
        servings = servings ?? 12,
        flavor = "Vani",
        decorationDescription = "Hoa kem",
        inscriptionText = (string?)null,
        note = (string?)null,
        estimatedPickupAt = "2026-10-25T09:30:00.250+07:00",
        validUntil,
        designAssetId = (long?)null,
    };

    private static TestApp App(QuoteStore store) => new(configureServices: services =>
    {
        services.RemoveAll<IQuoteStore>();
        services.AddSingleton<IQuoteStore>(store);
    });

    private static async Task<(ApiClient Client, long UserId)> SignedInAsync(TestApp app, string role = SystemRoles.Bakery)
    {
        var email = role + "@example.com";
        var id = app.AddUser(email, Password, role);
        var client = app.CreateApiClient();
        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync(email, Password)).StatusCode);
        return (client, id);
    }

    private static async Task<HttpResponseMessage> PostAsync(ApiClient client, string path, object? body) =>
        await client.PostAsync(path, body, await client.GetCsrfTokenAsync());

    public static TheoryData<string, bool> Writes() => new()
    {
        { Base, true },
        { Revise, true },
        { Withdraw, false },
    };

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task Writes_require_a_session_csrf_and_the_bakery_role(string path, bool hasBody)
    {
        var store = new QuoteStore();
        using var app = App(store);
        var body = hasBody ? Body() : null;

        using (var anonymous = app.CreateApiClient())
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(anonymous, path, body)).StatusCode);
        }

        var (bakery, _) = await SignedInAsync(app);
        Assert.Equal(HttpStatusCode.BadRequest, (await bakery.PostAsync(path, body, csrfToken: null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await bakery.PostAsync(path, body, csrfToken: "khong-hop-le")).StatusCode);

        foreach (var role in new[] { SystemRoles.Customer, SystemRoles.Admin })
        {
            var (other, _) = await SignedInAsync(app, role);
            Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(other, path, body)).StatusCode);
        }

        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task Create_uses_the_session_actor_and_returns_201_with_location()
    {
        var store = new QuoteStore { Write = new QuoteWriteResult(QuoteWriteOutcome.Saved, 21) };
        using var app = App(store);
        var (client, userId) = await SignedInAsync(app);

        var response = await PostAsync(client, Base, Body());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(Base + "/21", response.Headers.Location!.AbsolutePath);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(21, document.RootElement.GetProperty("id").GetInt64());
        Assert.True(document.RootElement.GetProperty("isCurrent").GetBoolean());
        Assert.Equal(("create", userId, 3L, 7L, 0L), store.WriteScope);
        Assert.Equal((userId, 3L, 7L, 21L), store.ReadScope);
        Assert.Equal(2_500_000.50m, store.Values!.Price);
        Assert.Equal(new DateTime(2026, 10, 25, 2, 30, 0, 250, DateTimeKind.Utc), store.Values.EstimatedPickupAtUtc);
    }

    [Fact]
    public async Task Revise_returns_the_new_version_and_withdraw_returns_the_withdrawn_one()
    {
        var store = new QuoteStore { Write = new QuoteWriteResult(QuoteWriteOutcome.Saved, 22) };
        using var app = App(store);
        var (client, userId) = await SignedInAsync(app);

        var revised = await PostAsync(client, Revise, Body());
        Assert.Equal(HttpStatusCode.Created, revised.StatusCode);
        Assert.Equal(Base + "/22", revised.Headers.Location!.AbsolutePath);
        Assert.Equal(("revise", userId, 3L, 7L, 9L), store.WriteScope);

        store.Write = new QuoteWriteResult(QuoteWriteOutcome.Saved, 9);
        store.Status = QuoteStatuses.Withdrawn;
        var withdrawn = await PostAsync(client, Withdraw, null);
        Assert.Equal(HttpStatusCode.OK, withdrawn.StatusCode);
        Assert.Null(withdrawn.Headers.Location);
        using var document = JsonDocument.Parse(await withdrawn.Content.ReadAsStringAsync());
        Assert.Equal("withdrawn", document.RootElement.GetProperty("status").GetString());
        Assert.False(document.RootElement.GetProperty("isCurrent").GetBoolean());
        Assert.Equal(("withdraw", userId, 3L, 7L, 9L), store.WriteScope);
    }

    [Theory]
    [InlineData("price-scale", "price")]
    [InlineData("price-overflow", "price")]
    [InlineData("servings-overflow", "servings")]
    [InlineData("valid-until-after-pickup", "validUntil")]
    [InlineData("valid-until-without-offset", "validUntil")]
    [InlineData("valid-until-past", "validUntil")]
    public async Task Invalid_content_returns_validation_problem_without_calling_the_store(string scenario, string field)
    {
        var store = new QuoteStore();
        using var app = App(store);
        var (client, _) = await SignedInAsync(app);
        var body = scenario switch
        {
            "price-scale" => Body(price: 1.234m),
            "price-overflow" => Body(price: 10_000_000_000m),
            "servings-overflow" => Body(servings: 70_000),
            "valid-until-after-pickup" => Body(validUntil: "2026-10-25T02:30:00.251Z"),
            "valid-until-without-offset" => Body(validUntil: "2026-10-20T00:00:00"),
            "valid-until-past" => Body(validUntil: "2026-10-15T08:00:00Z"),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        foreach (var path in new[] { Base, Revise })
        {
            var response = await PostAsync(client, path, body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(document.RootElement.GetProperty("errors").TryGetProperty(field, out _));
        }

        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task Malformed_json_is_rejected_by_binding()
    {
        var store = new QuoteStore();
        using var app = App(store);
        var (client, _) = await SignedInAsync(app);

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(client, Base, new { price = "680000" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(client, Base, new { price = 1e30 })).StatusCode);
        Assert.Equal(0, store.Writes);
    }

    [Theory]
    [InlineData(QuoteWriteOutcome.BakeryNotFound, 404, "BAKERY_NOT_FOUND")]
    [InlineData(QuoteWriteOutcome.RequestNotFound, 404, "REQUEST_NOT_FOUND")]
    [InlineData(QuoteWriteOutcome.QuoteNotFound, 404, "QUOTE_NOT_FOUND")]
    [InlineData(QuoteWriteOutcome.InvalidArgument, 400, "INVALID_ARGUMENT")]
    [InlineData(QuoteWriteOutcome.InvalidPrice, 400, "QUOTE_INVALID_PRICE")]
    [InlineData(QuoteWriteOutcome.InvalidTimes, 400, "QUOTE_INVALID_TIMES")]
    [InlineData(QuoteWriteOutcome.DesignAssetInvalid, 400, "DESIGN_ASSET_INVALID")]
    [InlineData(QuoteWriteOutcome.RequestNotOpen, 409, "REQUEST_NOT_OPEN")]
    [InlineData(QuoteWriteOutcome.RequestDeclined, 409, "REQUEST_DECLINED_BY_BAKERY")]
    [InlineData(QuoteWriteOutcome.QuoteAlreadyExists, 409, "QUOTE_ALREADY_EXISTS")]
    [InlineData(QuoteWriteOutcome.QuoteAlreadyAccepted, 409, "QUOTE_ALREADY_ACCEPTED")]
    [InlineData(QuoteWriteOutcome.QuoteNotCurrent, 409, "QUOTE_NOT_CURRENT")]
    [InlineData(QuoteWriteOutcome.BakeryNotActive, 409, "BAKERY_NOT_ACTIVE")]
    [InlineData(QuoteWriteOutcome.BakerySubscriptionInactive, 409, "BAKERY_SUBSCRIPTION_INACTIVE")]
    public async Task Store_outcomes_map_to_coded_problems(QuoteWriteOutcome outcome, int status, string code)
    {
        var store = new QuoteStore { Write = new QuoteWriteResult(outcome) };
        using var app = App(store);
        var (client, _) = await SignedInAsync(app);

        var response = await PostAsync(client, Revise, Body());

        Assert.Equal(status, (int)response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, store.Reads);
    }

    [Fact]
    public async Task Unknown_failures_are_masked_and_revoked_sessions_cannot_write()
    {
        var store = new QuoteStore { Throw = true };
        using var app = App(store);
        var (client, userId) = await SignedInAsync(app);

        var failure = await PostAsync(client, Withdraw, null);
        Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
        var body = await failure.Content.ReadAsStringAsync();
        Assert.DoesNotContain("hidden-database-details", body);
        Assert.DoesNotContain("stackTrace", body, StringComparison.OrdinalIgnoreCase);

        var writes = store.Writes;
        app.Store.Update(userId, account => account with { Status = UserStatuses.Disabled });
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(client, Base, Body())).StatusCode);
        Assert.Equal(writes, store.Writes);
    }

    private sealed class QuoteStore : IQuoteStore
    {
        private static readonly DateTime Created = new(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc);
        public QuoteWriteResult Write { get; set; } = new(QuoteWriteOutcome.Saved, 21);
        public string Status { get; set; } = QuoteStatuses.Submitted;
        public bool Throw { get; init; }
        public int Writes { get; private set; }
        public int Reads { get; private set; }
        public (string Action, long Actor, long Bakery, long Request, long Quote) WriteScope { get; private set; }
        public (long Actor, long Bakery, long Request, long Quote) ReadScope { get; private set; }
        public QuoteValues? Values { get; private set; }

        private Task<QuoteWriteResult> Record(string action, long actor, long bakery, long request, long quote, QuoteValues? values)
        {
            Writes++;
            WriteScope = (action, actor, bakery, request, quote);
            Values = values;
            if (Throw) throw new InvalidOperationException("hidden-database-details");
            return Task.FromResult(Write);
        }

        public Task<QuoteWriteResult> CreateAsync(long actorUserId, long bakeryId, long requestId, QuoteValues values, CancellationToken cancellationToken) =>
            Record("create", actorUserId, bakeryId, requestId, 0, values);
        public Task<QuoteWriteResult> ReviseAsync(long actorUserId, long bakeryId, long requestId, long quoteId, QuoteValues values, CancellationToken cancellationToken) =>
            Record("revise", actorUserId, bakeryId, requestId, quoteId, values);
        public Task<QuoteWriteResult> WithdrawAsync(long actorUserId, long bakeryId, long requestId, long quoteId, CancellationToken cancellationToken) =>
            Record("withdraw", actorUserId, bakeryId, requestId, quoteId, null);

        public Task<QuoteReadResult> FindForBakeryAsync(long actorUserId, long bakeryId, long requestId, long quoteId, CancellationToken cancellationToken)
        {
            Reads++;
            ReadScope = (actorUserId, bakeryId, requestId, quoteId);
            return Task.FromResult(new QuoteReadResult(QuoteReadOutcome.Found, new QuoteDto(
                quoteId, requestId, bakeryId, "Tiệm", 1, Status, 2_500_000.50m, "VND", "20 cm", 12, "Vani", "Hoa kem", null, null,
                Created.AddDays(10), Created.AddDays(5), null, Status == QuoteStatuses.Withdrawn ? Created : null, Created, Created)));
        }

        public Task<QuoteReadResult> FindForCustomerAsync(long customerId, long requestId, long quoteId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<QuoteListResult> ListForBakeryAsync(long actorUserId, long bakeryId, long requestId, string? status, int page, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<QuoteListResult> ListForCustomerAsync(long customerId, long requestId, string? status, int page, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

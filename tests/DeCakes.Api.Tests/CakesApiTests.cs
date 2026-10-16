using System.Net;
using System.Text.Json;
using DeCakes.Api.Authentication;
using DeCakes.Api.Tests.Infrastructure;
using DeCakes.Application.Cakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DeCakes.Api.Tests;

public class CakesApiTests
{
    private readonly InMemoryCakeCatalogStore _store = new();

    private TestApp CreateApp() => new(configureServices: services =>
    {
        services.RemoveAll<ICakeCatalogStore>();
        services.AddSingleton<ICakeCatalogStore>(_store);
    });

    [Theory]
    [InlineData("/api/cakes")]
    [InlineData("/api/cakes?page=&pageSize=&bakeryId=")]
    public async Task Anonymous_list_defaults_and_public_json_shape(string url)
    {
        _store.Items.Add(new CakeListItemDto(1, 2, "Bánh Việt", null, "VND"));
        using var app = CreateApp();
        using var client = app.CreateApiClient();

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var body = json.RootElement;
        Assert.Equal(1, body.GetProperty("page").GetInt32());
        Assert.Equal(20, body.GetProperty("pageSize").GetInt32());
        Assert.False(body.GetProperty("hasNext").GetBoolean());
        var item = Assert.Single(body.GetProperty("items").EnumerateArray());
        Assert.Equal(["bakeryId", "currency", "id", "name", "referencePrice"], item.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal("Bánh Việt", item.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("referencePrice").ValueKind);
        Assert.Equal("VND", item.GetProperty("currency").GetString());
    }

    [Theory]
    [InlineData("?page=0", "page")]
    [InlineData("?page=abc", "page")]
    [InlineData("?page=2147483648", "page")]
    [InlineData("?pageSize=0", "pageSize")]
    [InlineData("?pageSize=101", "pageSize")]
    [InlineData("?pageSize=abc", "pageSize")]
    [InlineData("?bakeryId=0", "bakeryId")]
    [InlineData("?bakeryId=-1", "bakeryId")]
    [InlineData("?bakeryId=abc", "bakeryId")]
    [InlineData("?bakeryId=9223372036854775808", "bakeryId")]
    [InlineData("?bakeryId=1%20OR%201=1", "bakeryId")]
    public async Task Invalid_list_input_returns_400_without_store(string query, string field)
    {
        using var app = CreateApp();
        using var client = app.CreateApiClient();
        var response = await client.GetAsync("/api/cakes" + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty(field, out _));
        Assert.Equal(0, _store.Calls);
    }

    [Theory]
    [InlineData("?pageSize=1&bakeryId=7", 1, true)]
    [InlineData("?page=2&pageSize=1&bakeryId=7", 3, false)]
    [InlineData("?bakeryId=99", 0, false)]
    [InlineData("?page=3&pageSize=1&bakeryId=7", 0, false)]
    [InlineData("?page=2147483647&pageSize=100", 0, false)]
    public async Task Filter_and_paging_return_200_and_expected_rows(string query, long expectedId, bool hasNext)
    {
        _store.Items.AddRange([
            new CakeListItemDto(3, 7, "Bánh C", 12.34m, "VND"),
            new CakeListItemDto(2, 8, "Bánh B", 0m, "VND"),
            new CakeListItemDto(1, 7, "Bánh A", null, "VND"),
        ]);
        using var app = CreateApp();
        using var client = app.CreateApiClient();
        var response = await client.GetAsync("/api/cakes" + query);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var body = json.RootElement;
        Assert.Equal(hasNext, body.GetProperty("hasNext").GetBoolean());
        var items = body.GetProperty("items").EnumerateArray().ToArray();
        if (expectedId == 0) Assert.Empty(items);
        else Assert.Equal(expectedId, Assert.Single(items).GetProperty("id").GetInt64());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(123456.78)]
    public async Task Detail_exposes_only_approved_fields_and_numeric_or_null_price(double? price)
    {
        decimal? amount = price.HasValue ? (decimal)price.Value : null;
        _store.Details[1] = new CakeDetailDto(1, 7, "Bánh O'Việt", amount, "VND", null, "Vani", null);
        using var app = CreateApp();
        using var client = app.CreateApiClient();

        var response = await client.GetAsync("/api/cakes/1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var body = json.RootElement;
        Assert.Equal(["bakeryId", "currency", "description", "flavor", "id", "name", "referencePrice", "sizeLabel"],
            body.EnumerateObject().Select(p => p.Name).Order().ToArray());
        var value = body.GetProperty("referencePrice");
        if (amount.HasValue) { Assert.Equal(JsonValueKind.Number, value.ValueKind); Assert.Equal(amount.Value, value.GetDecimal()); }
        else Assert.Equal(JsonValueKind.Null, value.ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("description").ValueKind);
        Assert.Equal("Vani", body.GetProperty("flavor").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("sizeLabel").ValueKind);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("9223372036854775808")]
    public async Task Invalid_detail_id_is_400_without_store(string id)
    {
        using var app = CreateApp();
        using var client = app.CreateApiClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/cakes/" + id)).StatusCode);
        Assert.Equal(0, _store.Calls);
    }

    [Fact]
    public async Task Missing_or_nonpublic_detail_is_404_problem()
    {
        using var app = CreateApp();
        using var client = app.CreateApiClient();
        var response = await client.GetAsync("/api/cakes/9223372036854775807");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/api/cakes")]
    [InlineData("/api/cakes/1")]
    public async Task Store_failure_is_masked_500(string url)
    {
        _store.Fail = true;
        using var app = CreateApp();
        using var client = app.CreateApiClient();
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("SIMULATED", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/cakes")]
    [InlineData("/api/cakes/1")]
    public async Task Public_routes_preserve_cookie_validation_failure_behavior(string url)
    {
        using var app = CreateApp();
        app.AddUser("cakes@example.com", "mat-khau-du-dai-1");
        using var client = app.CreateApiClient();
        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync("cakes@example.com", "mat-khau-du-dai-1")).StatusCode);
        app.Store.Fail = true;
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Null(client.SetCookieFor(AuthDefaults.AuthCookieName));
        Assert.True(client.Cookies.ContainsKey(AuthDefaults.AuthCookieName));
        Assert.Equal(0, _store.Calls);
    }
}

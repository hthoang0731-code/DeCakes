using System.Net;
using System.Text.Json;
using DeCakes.Api.Authentication;
using DeCakes.Api.Tests.Infrastructure;
using DeCakes.Application.Bakeries;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DeCakes.Api.Tests;

public class BakeriesApiTests
{
    private readonly InMemoryBakeryCatalogStore _store = new();

    private TestApp CreateApp() => new(configureServices: services =>
    {
        services.RemoveAll<IBakeryCatalogStore>();
        services.AddSingleton<IBakeryCatalogStore>(_store);
    });

    [Theory]
    [InlineData("/api/bakeries")]
    [InlineData("/api/bakeries?page=&pageSize=")]
    [InlineData("/api/bakeries?city=ignored&sort=name&includeDemo=true")]
    public async Task Anonymous_get_without_csrf_uses_defaults_and_only_public_dto_fields(string url)
    {
        _store.Items.Add(new BakeryListItemDto(17, "Tiệm bánh O'Bánh", "tiem-banh", null));
        using var app = CreateApp();
        using var client = app.CreateApiClient();

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var body = json.RootElement;
        Assert.Equal(["hasNext", "items", "page", "pageSize"], body.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(1, body.GetProperty("page").GetInt32());
        Assert.Equal(20, body.GetProperty("pageSize").GetInt32());
        Assert.False(body.GetProperty("hasNext").GetBoolean());
        var item = Assert.Single(body.GetProperty("items").EnumerateArray());
        Assert.Equal(["city", "id", "name", "slug"], item.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(17, item.GetProperty("id").GetInt64());
        Assert.Equal("Tiệm bánh O'Bánh", item.GetProperty("name").GetString());
        Assert.Equal("tiem-banh", item.GetProperty("slug").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("city").ValueKind);
        Assert.Empty(client.LastSetCookies);
        Assert.Equal(1, _store.Calls);
    }

    [Theory]
    [InlineData("?page=0", "page")]
    [InlineData("?page=-1", "page")]
    [InlineData("?page=abc", "page")]
    [InlineData("?page=2147483648", "page")]
    [InlineData("?pageSize=0", "pageSize")]
    [InlineData("?pageSize=-1", "pageSize")]
    [InlineData("?pageSize=101", "pageSize")]
    [InlineData("?pageSize=1.5", "pageSize")]
    [InlineData("?pageSize=2147483648", "pageSize")]
    [InlineData("?pageSize=1%27%20OR%201=1", "pageSize")]
    public async Task Invalid_input_returns_validation_problem_without_querying_store(string query, string field)
    {
        using var app = CreateApp();
        using var client = app.CreateApiClient();

        var response = await client.GetAsync("/api/bakeries" + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(400, json.RootElement.GetProperty("status").GetInt32());
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty(field, out _));
        Assert.Equal(0, _store.Calls);
    }

    [Theory]
    [InlineData(1, 2, true, 1, 2)]
    [InlineData(2, 2, false, 3, 4)]
    [InlineData(3, 2, false, 0, 0)]
    [InlineData(int.MaxValue, 100, false, 0, 0)]
    public async Task Paging_has_next_last_and_out_of_range_pages_match_contract(
        int page, int pageSize, bool hasNext, long first, long second)
    {
        for (int i = 4; i >= 1; i--) _store.Items.Add(new BakeryListItemDto(i, $"Tiệm {i}", $"tiem-{i}", "Huế"));
        using var app = CreateApp();
        using var client = app.CreateApiClient();

        var response = await client.GetAsync($"/api/bakeries?page={page}&pageSize={pageSize}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var body = json.RootElement;
        Assert.Equal(page, body.GetProperty("page").GetInt32());
        Assert.Equal(pageSize, body.GetProperty("pageSize").GetInt32());
        Assert.Equal(hasNext, body.GetProperty("hasNext").GetBoolean());
        Assert.Equal(first == 0 ? [] : new[] { first, second },
            body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt64()).ToArray());
    }

    [Fact]
    public async Task Empty_catalog_returns_200_with_an_empty_page()
    {
        using var app = CreateApp();
        using var client = app.CreateApiClient();

        var response = await client.GetAsync("/api/bakeries");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Empty(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(1, json.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(20, json.RootElement.GetProperty("pageSize").GetInt32());
        Assert.False(json.RootElement.GetProperty("hasNext").GetBoolean());
    }

    [Fact]
    public async Task Catalog_failure_is_masked_500_not_an_empty_list()
    {
        _store.Fail = true;
        using var app = CreateApp();
        using var client = app.CreateApiClient();

        var response = await client.GetAsync("/api/bakeries");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("SIMULATED", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Cookie_session_is_still_validated_and_failure_does_not_clear_cookie()
    {
        using var app = CreateApp();
        app.AddUser("catalog@example.com", "mat-khau-du-dai-1");
        using var client = app.CreateApiClient();
        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync("catalog@example.com", "mat-khau-du-dai-1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/bakeries")).StatusCode);
        int callsBeforeFailure = _store.Calls;
        app.Store.Fail = true;

        var response = await client.GetAsync("/api/bakeries");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(callsBeforeFailure, _store.Calls);
        Assert.Null(client.SetCookieFor(AuthDefaults.AuthCookieName));
        Assert.True(client.Cookies.ContainsKey(AuthDefaults.AuthCookieName));
        Assert.DoesNotContain("SIMULATED", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Revoked_session_is_cleared_but_public_get_remains_available()
    {
        using var app = CreateApp();
        long id = app.AddUser("disabled@example.com", "mat-khau-du-dai-1");
        using var client = app.CreateApiClient();
        await client.LoginAsync("disabled@example.com", "mat-khau-du-dai-1");
        app.Store.Remove(id);

        var response = await client.GetAsync("/api/bakeries");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(client.SetCookieFor(AuthDefaults.AuthCookieName));
        Assert.False(client.Cookies.ContainsKey(AuthDefaults.AuthCookieName));
    }
}

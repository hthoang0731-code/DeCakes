using System.Net;
using System.Text.Json;
using DeCakes.Api.Tests.Infrastructure;
using DeCakes.Application.Bakeries;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DeCakes.Api.Tests;

public class BakeryDetailsApiTests
{
    private readonly InMemoryBakeryCatalogStore _store = new();

    private TestApp CreateApp() => new(configureServices: services =>
    {
        services.RemoveAll<IBakeryCatalogStore>();
        services.AddSingleton<IBakeryCatalogStore>(_store);
    });

    [Fact]
    public async Task Anonymous_detail_has_only_public_fields_and_preserves_nulls()
    {
        _store.Details[1] = new BakeryDetailDto(1, "Tiệm O'Bánh", "tiem", "Huế", null, null, null);
        using var app = CreateApp();
        using var client = app.CreateApiClient();

        var response = await client.GetAsync("/api/bakeries/1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var body = json.RootElement;
        Assert.Equal(["addressLine", "city", "description", "id", "name", "phone", "slug"],
            body.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal("Tiệm O'Bánh", body.GetProperty("name").GetString());
        Assert.Equal("Huế", body.GetProperty("city").GetString());
        foreach (string field in new[] { "description", "addressLine", "phone" })
            Assert.Equal(JsonValueKind.Null, body.GetProperty(field).ValueKind);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("9223372036854775808")]
    public async Task Invalid_id_returns_400_without_store_call(string id)
    {
        using var app = CreateApp();
        using var client = app.CreateApiClient();

        var response = await client.GetAsync("/api/bakeries/" + id);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty("id", out _));
        Assert.Equal(0, _store.Calls);
    }

    [Fact]
    public async Task No_visible_detail_returns_404_problem()
    {
        using var app = CreateApp();
        using var client = app.CreateApiClient();

        var response = await client.GetAsync("/api/bakeries/9223372036854775807");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Failure_is_masked_500()
    {
        _store.Fail = true;
        using var app = CreateApp();
        using var client = app.CreateApiClient();

        var response = await client.GetAsync("/api/bakeries/1");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("SIMULATED", await response.Content.ReadAsStringAsync());
    }
}

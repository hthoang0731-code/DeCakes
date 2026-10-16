using DeCakes.Application.Cakes;
using DeCakes.Infrastructure.Cakes;

namespace DeCakes.Infrastructure.Tests.MySql;

public class MySqlCakeCatalogStoreTests : IAsyncLifetime
{
    private readonly MySqlTestDatabase _database = new();
    private MySqlCakeCatalogStore Store => new(_database.Factory);

    public Task InitializeAsync() => _database.InitializeAsync();
    public Task DisposeAsync() => _database.DisposeAsync();

    [MySqlFact]
    public async Task Empty_catalog_returns_empty_list_and_missing_detail()
    {
        Assert.Empty(await Store.ListPublicAsync(0, 21, null, CancellationToken.None));
        Assert.Null(await Store.FindPublicByIdAsync(long.MaxValue, CancellationToken.None));
    }

    [MySqlFact]
    public async Task Visibility_matrix_is_identical_for_list_and_direct_detail()
    {
        var publicIds = new List<long>();
        foreach (string parentStatus in new[] { "draft", "pending_review", "active", "suspended" })
        foreach (bool parentDemo in new[] { false, true })
        {
            long bakeryId = await _database.SeedBakeryAsync("Tiệm", parentStatus, parentDemo);
            foreach (string visibility in new[] { "draft", "published", "hidden" })
            foreach (bool cakeDemo in new[] { false, true })
            {
                long id = await _database.SeedCakeAsync(bakeryId, "Bánh", visibility, cakeDemo);
                bool visible = parentStatus == "active" && !parentDemo && visibility == "published" && !cakeDemo;
                var detail = await Store.FindPublicByIdAsync(id, CancellationToken.None);
                Assert.Equal(visible, detail is not null);
                if (visible) publicIds.Add(id);
            }
        }

        var rows = await Store.ListPublicAsync(0, 101, null, CancellationToken.None);

        Assert.Equal(publicIds, rows.Select(row => row.Id));
    }

    [MySqlFact]
    public async Task Bakery_filter_is_applied_before_paging_and_unknown_or_nonpublic_parent_is_empty()
    {
        long firstBakery = await _database.SeedBakeryAsync("First");
        long secondBakery = await _database.SeedBakeryAsync("Second");
        long hiddenBakery = await _database.SeedBakeryAsync("Hidden", "suspended");
        await _database.SeedCakeAsync(hiddenBakery, "Parent hidden");
        var ids = new List<long>();
        for (int i = 0; i < 5; i++)
        {
            await _database.SeedCakeAsync(secondBakery, "Other bakery");
            await _database.SeedCakeAsync(firstBakery, "Hidden", "hidden");
            ids.Add(await _database.SeedCakeAsync(firstBakery, $"Bánh {5 - i}"));
            await _database.SeedCakeAsync(firstBakery, "Demo", isDemo: true);
        }

        var rows = await Store.ListPublicAsync(2, 3, firstBakery, CancellationToken.None);

        Assert.Equal(ids.Skip(2).Take(3), rows.Select(row => row.Id));
        Assert.All(rows, row => Assert.Equal(firstBakery, row.BakeryId));
        Assert.Empty(await Store.ListPublicAsync(0, 21, hiddenBakery, CancellationToken.None));
        Assert.Empty(await Store.ListPublicAsync(0, 21, long.MaxValue, CancellationToken.None));
    }

    [MySqlFact]
    public async Task Nullable_zero_and_maximum_decimal_prices_and_unicode_map_exactly()
    {
        long bakeryId = await _database.SeedBakeryAsync("Tiệm");
        long nullId = await _database.SeedCakeAsync(bakeryId, "O'Bánh Việt");
        long zeroId = await _database.SeedCakeAsync(bakeryId, "Zero", referencePrice: 0m);
        long maximumId = await _database.SeedCakeAsync(bakeryId, "Maximum", referencePrice: 9999999999.99m,
            description: "Mô tả bánh", flavor: "Vani – dâu", sizeLabel: "18 cm");

        var rows = await Store.ListPublicAsync(0, 21, null, CancellationToken.None);
        Assert.Equal(new decimal?[] { null, 0m, 9999999999.99m }, rows.Select(row => row.ReferencePrice));
        Assert.All(rows, row => Assert.Equal("VND", row.Currency));
        var nullable = await Store.FindPublicByIdAsync(nullId, CancellationToken.None);
        Assert.NotNull(nullable);
        Assert.Equal("O'Bánh Việt", nullable.Name);
        Assert.Null(nullable.ReferencePrice);
        Assert.Null(nullable.Description);
        Assert.Null(nullable.Flavor);
        Assert.Null(nullable.SizeLabel);
        Assert.Equal(0m, (await Store.FindPublicByIdAsync(zeroId, CancellationToken.None))!.ReferencePrice);
        var maximum = await Store.FindPublicByIdAsync(maximumId, CancellationToken.None);
        Assert.Equal(new CakeDetailDto(maximumId, bakeryId, "Maximum", 9999999999.99m, "VND",
            "Mô tả bánh", "Vani – dâu", "18 cm"), maximum);
    }

    [MySqlFact]
    public async Task Service_and_sql_agree_on_lookahead_last_page_and_64_bit_offset()
    {
        long bakeryId = await _database.SeedBakeryAsync("Tiệm");
        for (int i = 0; i < 3; i++) await _database.SeedCakeAsync(bakeryId, $"Bánh {i}");
        var service = new CakeCatalogService(Store);

        var first = Assert.IsType<CakeListResult.Succeeded>(await service.ListAsync(1, 2, bakeryId, CancellationToken.None)).Page;
        Assert.Equal(2, first.Items.Count);
        Assert.True(first.HasNext);
        var last = Assert.IsType<CakeListResult.Succeeded>(await service.ListAsync(2, 2, bakeryId, CancellationToken.None)).Page;
        Assert.Single(last.Items);
        Assert.False(last.HasNext);
        var beyond = Assert.IsType<CakeListResult.Succeeded>(await service.ListAsync(int.MaxValue, 100, null, CancellationToken.None)).Page;
        Assert.Empty(beyond.Items);
        Assert.False(beyond.HasNext);
    }

    [MySqlFact]
    public async Task Maximum_page_size_has_one_lookahead_row()
    {
        long bakeryId = await _database.SeedBakeryAsync("Tiệm");
        for (int i = 0; i < 101; i++) await _database.SeedCakeAsync(bakeryId, $"Bánh {i}");

        var page = Assert.IsType<CakeListResult.Succeeded>(
            await new CakeCatalogService(Store).ListAsync(1, 100, bakeryId, CancellationToken.None)).Page;

        Assert.Equal(100, page.Items.Count);
        Assert.True(page.HasNext);
    }
}

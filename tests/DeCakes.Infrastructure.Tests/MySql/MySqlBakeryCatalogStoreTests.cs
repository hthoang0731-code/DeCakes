using DeCakes.Application.Bakeries;
using DeCakes.Infrastructure.Bakeries;
using MySqlConnector;

namespace DeCakes.Infrastructure.Tests.MySql;

// Mỗi test có database riêng: các ca phân trang không phụ thuộc dữ liệu hoặc thứ tự chạy ca khác.
public class MySqlBakeryCatalogStoreTests : IAsyncLifetime
{
    private readonly MySqlTestDatabase _database = new();

    private MySqlBakeryCatalogStore Store => new(_database.Factory);

    public Task InitializeAsync() => _database.InitializeAsync();

    public Task DisposeAsync() => _database.DisposeAsync();

    [MySqlFact]
    public async Task Empty_database_returns_no_bakeries()
    {
        Assert.Empty(await Store.ListPublicAsync(0, 21, CancellationToken.None));
    }

    [MySqlFact]
    public async Task Only_active_non_demo_bakeries_are_public()
    {
        foreach (string status in new[] { "draft", "pending_review", "suspended" })
        {
            await _database.SeedBakeryAsync(status, status);
            await _database.SeedBakeryAsync(status + " demo", status, isDemo: true);
        }

        await _database.SeedBakeryAsync("Demo active", isDemo: true);
        long publicId = await _database.SeedBakeryAsync("Public active");

        var rows = await Store.ListPublicAsync(0, 21, CancellationToken.None);

        Assert.Equal(publicId, Assert.Single(rows).Id);
    }

    [MySqlFact]
    public async Task Unicode_apostrophes_and_nullable_city_map_without_extra_fields()
    {
        long first = await _database.SeedBakeryAsync("Tiệm O'Bánh – Huế");
        long second = await _database.SeedBakeryAsync("Bánh Việt", city: "Thành phố Hồ Chí Minh");

        var rows = await Store.ListPublicAsync(0, 21, CancellationToken.None);

        Assert.Equal(new[] { first, second }, rows.Select(row => row.Id));
        Assert.Equal("Tiệm O'Bánh – Huế", rows[0].Name);
        Assert.StartsWith("test-", rows[0].Slug);
        Assert.Null(rows[0].City);
        Assert.Equal("Thành phố Hồ Chí Minh", rows[1].City);
    }

    [MySqlFact]
    public async Task Visibility_is_applied_before_offset_and_limit_and_pages_are_ordered_by_id()
    {
        var ids = new List<long>();
        for (int i = 6; i >= 1; i--)
        {
            await _database.SeedBakeryAsync("Hidden", "draft");
            ids.Add(await _database.SeedBakeryAsync($"Tiệm {i}"));
            await _database.SeedBakeryAsync("Demo", isDemo: true);
        }

        var rows = await Store.ListPublicAsync(2, 3, CancellationToken.None);

        Assert.Equal(ids.Skip(2).Take(3), rows.Select(row => row.Id));
        var service = new BakeryCatalogService(Store);
        var middle = Assert.IsType<BakeryListResult.Succeeded>(await service.ListAsync(2, 2, CancellationToken.None)).Page;
        Assert.Equal(ids.Skip(2).Take(2), middle.Items.Select(item => item.Id));
        Assert.True(middle.HasNext);
        var last = Assert.IsType<BakeryListResult.Succeeded>(await service.ListAsync(3, 2, CancellationToken.None)).Page;
        Assert.Equal(ids.Skip(4), last.Items.Select(item => item.Id));
        Assert.False(last.HasNext);
        var beyond = Assert.IsType<BakeryListResult.Succeeded>(await service.ListAsync(4, 2, CancellationToken.None)).Page;
        Assert.Empty(beyond.Items);
        Assert.False(beyond.HasNext);
    }

    [MySqlFact]
    public async Task Maximum_page_size_trims_the_lookahead_row()
    {
        for (int i = 0; i < 101; i++) await _database.SeedBakeryAsync($"Tiệm {i}");

        var result = Assert.IsType<BakeryListResult.Succeeded>(
            await new BakeryCatalogService(Store).ListAsync(1, 100, CancellationToken.None)).Page;

        Assert.Equal(100, result.Items.Count);
        Assert.True(result.HasNext);
    }

    [MySqlFact]
    public async Task Large_offset_is_bound_as_a_64_bit_value()
    {
        await _database.SeedBakeryAsync("Public");

        Assert.Empty(await Store.ListPublicAsync(214748364600L, 101, CancellationToken.None));
    }

    [MySqlFact]
    public async Task Detail_excludes_every_nonpublic_and_demo_bakery()
    {
        foreach (string status in new[] { "draft", "pending_review", "suspended" })
        {
            long id = await _database.SeedBakeryAsync(status, status);
            Assert.Null(await Store.FindPublicByIdAsync(id, CancellationToken.None));
        }

        long demoId = await _database.SeedBakeryAsync("Demo", isDemo: true);
        Assert.Null(await Store.FindPublicByIdAsync(demoId, CancellationToken.None));
        Assert.Null(await Store.FindPublicByIdAsync(long.MaxValue, CancellationToken.None));
    }

    [MySqlFact]
    public async Task Detail_maps_nullable_and_populated_contact_fields()
    {
        long emptyId = await _database.SeedBakeryAsync("Nullable");
        var empty = await Store.FindPublicByIdAsync(emptyId, CancellationToken.None);
        Assert.NotNull(empty);
        Assert.Null(empty.Description);
        Assert.Null(empty.AddressLine);
        Assert.Null(empty.Phone);
        long id = await _database.SeedBakeryAsync("Tiệm", city: "Huế", description: "Bánh Việt", addressLine: "12 Đường A", phone: "0900000000");

        var detail = await Store.FindPublicByIdAsync(id, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(id, detail.Id);
        Assert.Equal("Huế", detail.City);
        Assert.Equal("Bánh Việt", detail.Description);
        Assert.Equal("12 Đường A", detail.AddressLine);
        Assert.Equal("0900000000", detail.Phone);
    }

    [MySqlFact]
    public async Task Catalog_reader_still_has_no_bakery_insert_permission()
    {
        await using var connection = await _database.Factory.OpenConnectionAsync(CancellationToken.None);
        await using var command = new MySqlCommand("INSERT INTO bakeries (name, slug) VALUES ('Forbidden', 'forbidden')", connection);

        var error = await Assert.ThrowsAsync<MySqlException>(() => command.ExecuteNonQueryAsync());

        Assert.Equal(1142, error.Number);
    }
}

using System.Globalization;
using System.Text.Json;
using DeCakes.Application.Admin;
using DeCakes.Infrastructure.Admin;

namespace DeCakes.Infrastructure.Tests.MySql;

public sealed class MySqlAdminStoreTests : IAsyncLifetime
{
    // Reads use SELECT-only credentials; setup uses AdminFactory. Write fixtures remain unchanged.
    private readonly MySqlTestDatabase _database = new() { CatalogReadOnly = true };
    private MySqlAdminStore Store => new(_database.Factory);
    private static CancellationToken Ct => CancellationToken.None;
    public Task InitializeAsync() => _database.InitializeAsync();
    public Task DisposeAsync() => _database.DisposeAsync();
    private static AdminUserFilter Users(bool? demo = null) => new(null, null, null, demo);
    private static AdminBakeryFilter Bakeries(bool? demo = null) => new(null, null, demo);

    private Task<long> User(string role = "customer", string status = "active", bool demo = false, string? name = null, string? email = null) =>
        _database.AdminExecuteAsync("""
            INSERT INTO users (email, full_name, system_role, status, is_demo, phone)
            VALUES (@email, @name, @role, @status, @demo, @phone)
            """, ("@email", email ?? $"{Guid.NewGuid():N}@example.test"), ("@name", name ?? "Nguyễn O'An"),
            ("@role", role), ("@status", status), ("@demo", demo), ("@phone", demo ? null : "0123"));

    [MySqlFact]
    public async Task Empty_lists_and_dashboard_include_zero_buckets()
    {
        Assert.Equal(0L, (await Store.ListUsersAsync(Users(), 1, 20, Ct)).TotalCount);
        Assert.Empty((await Store.ListBakeriesAsync(Bakeries(), 1, 20, Ct)).Items);
        var d = await Store.DashboardAsync(null, Ct);
        Assert.Null(d.IsDemo);
        Assert.Equal(3, d.UsersByRole.Count);
        Assert.Equal(2, d.UsersByStatus.Count);
        Assert.Equal(5, d.BakeriesByProfileStatus.Count);
        Assert.Equal(4, d.DesignRequestsByStatus.Count);
        Assert.Equal(6, d.OrdersByStatus.Count);
        Assert.All(d.UsersByRole.Values.Concat(d.UsersByStatus.Values).Concat(d.BakeriesByProfileStatus.Values)
            .Concat(d.DesignRequestsByStatus.Values).Concat(d.OrdersByStatus.Values), count => Assert.Equal(0L, count));
        Assert.Null(await Store.FindUserAsync(999, Ct));
        Assert.Null(await Store.FindBakeryAsync(999, Ct));
    }

    [MySqlFact]
    public async Task User_filters_counts_order_large_offset_and_details_match_schema()
    {
        var expected = new List<long>();
        foreach (var role in new[] { "customer", "bakery", "admin" })
        foreach (var status in new[] { "active", "disabled" })
        foreach (var demo in new[] { false, true }) expected.Add(await User(role, status, demo));
        var before = await Snapshot();
        var all = await Store.ListUsersAsync(Users(), 1, 100, Ct);
        Assert.Equal(expected, all.Items.Select(u => u.Id));
        Assert.Equal(12L, all.TotalCount);
        var joined = new List<long>();
        for (int page = 1; page <= 5; page++)
        {
            var part = await Store.ListUsersAsync(Users(), page, 3, Ct);
            Assert.Equal(12L, part.TotalCount);
            joined.AddRange(part.Items.Select(u => u.Id));
        }
        Assert.Equal(expected, joined);
        foreach (var demo in new[] { false, true })
        {
            Assert.Equal(6L, (await Store.ListUsersAsync(Users(demo), 1, 100, Ct)).TotalCount);
            foreach (var role in new[] { "customer", "bakery", "admin" })
            foreach (var status in new[] { "active", "disabled" })
            {
                var result = await Store.ListUsersAsync(new(null, role, status, demo), 1, 20, Ct);
                var user = Assert.Single(result.Items);
                Assert.Equal(1L, result.TotalCount);
                Assert.Equal((role, status, demo), (user.Role, user.Status, user.IsDemo));
                Assert.Equal(user, await Store.FindUserAsync(user.Id, Ct));
                Assert.Equal(demo ? null : "0123", user.Phone);
                Assert.Equal(DateTimeKind.Utc, user.CreatedAt.Kind);
                Assert.Equal(DateTimeKind.Utc, user.UpdatedAt.Kind);
            }
        }
        var beyond = await Store.ListUsersAsync(Users(), int.MaxValue, 100, Ct);
        Assert.Empty(beyond.Items);
        Assert.Equal(12L, beyond.TotalCount);
        Assert.Equal(before, await Snapshot());
    }

    [MySqlFact]
    public async Task Search_is_literal_parameterized_and_works_for_both_user_columns_and_bakery_columns()
    {
        var user = await User(name: "Tiệm %_!\\ O'Bánh – Huế", email: "literal@example.test");
        await User(name: "Tiệm abc O'Bánh", email: "other@example.test");
        var bakery = await _database.SeedBakeryAsync("Tiệm %_!\\ O'Bánh – Huế", "draft", description: "Mô tả", addressLine: "Địa chỉ", city: "Huế", phone: "0123");
        await _database.SeedBakeryAsync("Tiệm abc O'Bánh");
        foreach (var term in new[] { "%", "_", "!", "\\", "%_!\\", "Huế", "literal@example" })
        {
            var result = await Store.ListUsersAsync(new(term, null, null, null), 1, 20, Ct);
            Assert.Equal(user, Assert.Single(result.Items).Id);
            Assert.Equal(1L, result.TotalCount);
        }
        foreach (var term in new[] { "%", "_", "!", "\\", "%_!\\", "Huế" })
            Assert.Equal(bakery, Assert.Single((await Store.ListBakeriesAsync(new(term, null, null), 1, 20, Ct)).Items).Id);
        foreach (var term in new[] { "' OR 1=1 --", "does-not-exist" })
        {
            Assert.Empty((await Store.ListUsersAsync(new(term, null, null, null), 1, 20, Ct)).Items);
            Assert.Empty((await Store.ListBakeriesAsync(new(term, null, null), 1, 20, Ct)).Items);
        }
        var detail = (await Store.FindBakeryAsync(bakery, Ct))!;
        Assert.Equal(bakery, Assert.Single((await Store.ListBakeriesAsync(new(detail.Slug, null, null), 1, 20, Ct)).Items).Id);
        Assert.Equal(("Mô tả", "Địa chỉ", "Huế", "0123", "draft"), (detail.Description, detail.AddressLine, detail.City, detail.Phone, detail.ProfileStatus));
        Assert.Equal(DateTimeKind.Utc, detail.CreatedAt.Kind);
    }

    [MySqlFact]
    public async Task Bakery_status_demo_and_paging_do_not_use_public_catalog_predicate()
    {
        var ids = new List<long>();
        foreach (var status in new[] { "draft", "pending_review", "active", "suspended" })
        foreach (var demo in new[] { false, true })
        {
            var id = await _database.SeedBakeryAsync(status, status, demo);
            ids.Add(id);
            var item = Assert.Single((await Store.ListBakeriesAsync(new(null, status, demo), 1, 20, Ct)).Items);
            Assert.Equal((id, status, demo), (item.Id, item.ProfileStatus, item.IsDemo));
            Assert.Equal(item, await Store.FindBakeryAsync(id, Ct));
            Assert.Null(item.Description);
            Assert.Null(item.Phone);
        }
        Assert.Equal(ids, (await Store.ListBakeriesAsync(Bakeries(), 1, 100, Ct)).Items.Select(b => b.Id));
        Assert.Equal(4L, (await Store.ListBakeriesAsync(Bakeries(true), 1, 100, Ct)).TotalCount);
        var middle = await Store.ListBakeriesAsync(Bakeries(), 2, 3, Ct);
        Assert.Equal(ids.Skip(3).Take(3), middle.Items.Select(b => b.Id));
        Assert.Equal(8L, middle.TotalCount);
        var beyond = await Store.ListBakeriesAsync(Bakeries(), int.MaxValue, 100, Ct);
        Assert.Empty(beyond.Items);
        Assert.Equal(8L, beyond.TotalCount);
    }

    [MySqlFact]
    public async Task Dashboard_counts_every_status_and_demo_uses_source_request_for_orders_without_writes()
    {
        foreach (var role in new[] { "customer", "bakery", "admin" })
        foreach (var status in new[] { "active", "disabled" })
        foreach (var demo in new[] { false, true }) await User(role, status, demo);
        foreach (var status in new[] { "draft", "pending_review", "active", "suspended", "rejected" })
        foreach (var demo in new[] { false, true }) await _database.SeedBakeryAsync(status, status, demo);
        var customer = Convert.ToInt64(await _database.AdminScalarAsync("SELECT MIN(id) FROM users WHERE system_role='customer' AND is_demo=0"));
        var owner = Convert.ToInt64(await _database.AdminScalarAsync("SELECT MIN(id) FROM users WHERE system_role='bakery' AND is_demo=0"));
        var bakery = Convert.ToInt64(await _database.AdminScalarAsync("SELECT MIN(id) FROM bakeries WHERE is_demo=0"));
        await _database.AdminExecuteAsync("INSERT INTO bakery_members (bakery_id,user_id,member_role) VALUES (@b,@u,'owner')", ("@b", bakery), ("@u", owner));
        foreach (var demo in new[] { false, true })
        {
            foreach (var status in new[] { "draft", "open", "ordered", "cancelled" }) await Request(customer, demo, status);
            foreach (var status in new[] { "pending_confirmation", "confirmed", "rejected_by_bakery", "ready", "picked_up", "cancelled" })
            {
                var request = await Request(customer, demo, "ordered");
                await _database.AdminExecuteAsync("INSERT INTO request_bakeries (design_request_id,bakery_id) VALUES (@r,@b)", ("@r", request), ("@b", bakery));
                var quote = await _database.AdminExecuteAsync("""
                    INSERT INTO quotes (design_request_id,bakery_id,price,servings,flavor,decoration_description,estimated_pickup_at,valid_until,created_by_user_id)
                    VALUES (@r,@b,100,8,'Vani','Hoa',UTC_TIMESTAMP(3)+INTERVAL 3 DAY,UTC_TIMESTAMP(3)+INTERVAL 1 DAY,@u)
                    """, ("@r", request), ("@b", bakery), ("@u", owner));
                await _database.AdminExecuteAsync("""
                    INSERT INTO orders (quote_id,design_request_id,customer_id,bakery_id,agreed_quote_revision_no,agreed_price,
                        agreed_pickup_at,agreed_servings,agreed_flavor,agreed_decoration_description,status,
                        bakery_decided_at,bakery_decided_by_user_id,picked_up_at,cancelled_at)
                    VALUES (@q,@r,@c,@b,1,100,UTC_TIMESTAMP(3)+INTERVAL 3 DAY,8,'Vani','Hoa',@s,
                        IF(@s IN ('confirmed','rejected_by_bakery','ready','picked_up'),UTC_TIMESTAMP(3),NULL),
                        IF(@s IN ('confirmed','rejected_by_bakery','ready','picked_up'),@u,NULL),
                        IF(@s='picked_up',UTC_TIMESTAMP(3),NULL),IF(@s='cancelled',UTC_TIMESTAMP(3),NULL))
                    """, ("@q", quote), ("@r", request), ("@c", customer), ("@b", bakery), ("@s", status), ("@u", owner));
            }
        }
        var before = await Snapshot();
        foreach (bool? demo in new bool?[] { null, false, true })
        {
            var d = await Store.DashboardAsync(demo, Ct);
            var multiplier = demo is null ? 2L : 1L;
            Assert.Equal(demo, d.IsDemo);
            Assert.All(d.UsersByRole.Values, n => Assert.Equal(2 * multiplier, n));
            Assert.All(d.UsersByStatus.Values, n => Assert.Equal(3 * multiplier, n));
            Assert.All(d.BakeriesByProfileStatus.Values, n => Assert.Equal(multiplier, n));
            Assert.Equal(7 * multiplier, d.DesignRequestsByStatus["ordered"]);
            Assert.All(d.DesignRequestsByStatus.Where(kv => kv.Key != "ordered"), kv => Assert.Equal(multiplier, kv.Value));
            Assert.All(d.OrdersByStatus.Values, n => Assert.Equal(multiplier, n));
        }
        await Store.ListUsersAsync(Users(), 1, 20, Ct);
        await Store.ListBakeriesAsync(Bakeries(), 1, 20, Ct);
        await Store.FindUserAsync(customer, Ct);
        await Store.FindBakeryAsync(bakery, Ct);
        Assert.Equal(before, await Snapshot());
    }

    private Task<long> Request(long customer, bool demo, string status) => _database.AdminExecuteAsync("""
        INSERT INTO design_requests (customer_id,title,idea_description,budget_max,pickup_at,status,is_demo)
        VALUES (@c,'Bánh thử','Hoa',100,UTC_TIMESTAMP(3)+INTERVAL 3 DAY,@s,@d)
        """, ("@c", customer), ("@s", status), ("@d", demo));

    private async Task<string> Snapshot()
    {
        var rows = new List<string>();
        await using var connection = await _database.AdminFactory.OpenConnectionAsync();
        foreach (var table in new[] { "users", "bakeries", "bakery_members", "cakes", "design_requests", "request_bakeries", "quotes", "orders", "media_assets" })
        {
            await using var command = new MySqlConnector.MySqlCommand($"SELECT * FROM {table} ORDER BY id", connection);
            // bakery_members has a composite primary key and no id column.
            if (table == "bakery_members") command.CommandText = "SELECT * FROM bakery_members ORDER BY bakery_id,user_id";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows.Add(table + JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount)
                    .Select(i => reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)).ToArray()));
        }
        return string.Join('\n', rows);
    }

}

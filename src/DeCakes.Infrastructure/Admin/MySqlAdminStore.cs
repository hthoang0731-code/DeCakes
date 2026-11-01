using DeCakes.Application.Admin;
using DeCakes.Infrastructure.Persistence;
using MySqlConnector;

namespace DeCakes.Infrastructure.Admin;

/// <summary>Admin reads all profiles, including demo/disabled/draft. No Catalog visibility predicates.</summary>
public sealed class MySqlAdminStore(IMySqlConnectionFactory connectionFactory) : IAdminStore
{
    private const string UserColumns = "id, email, full_name, phone, system_role, status, is_demo, created_at, updated_at";
    private const string BakeryColumns = "id, name, slug, description, address_line, city, phone, profile_status, is_demo, created_at, updated_at";
    private const string UserWhere = "(@search IS NULL OR email LIKE @search ESCAPE '!' OR full_name LIKE @search ESCAPE '!') " +
        "AND (@role IS NULL OR system_role = @role) AND (@status IS NULL OR status = @status) AND (@isDemo IS NULL OR is_demo = @isDemo)";
    private const string BakeryWhere = "(@search IS NULL OR name LIKE @search ESCAPE '!' OR slug LIKE @search ESCAPE '!') " +
        "AND (@status IS NULL OR profile_status = @status) AND (@isDemo IS NULL OR is_demo = @isDemo)";

    public Task<AdminPage<AdminUserDto>> ListUsersAsync(AdminUserFilter filter, int page, int pageSize, CancellationToken cancellationToken) =>
        ListAsync("users", UserColumns, UserWhere, filter.Search, filter.Role, filter.Status, filter.IsDemo,
            page, pageSize, ReadUser, cancellationToken);

    public Task<AdminPage<AdminBakeryDto>> ListBakeriesAsync(AdminBakeryFilter filter, int page, int pageSize, CancellationToken cancellationToken) =>
        ListAsync("bakeries", BakeryColumns, BakeryWhere, filter.Search, null, filter.ProfileStatus, filter.IsDemo,
            page, pageSize, ReadBakery, cancellationToken);

    public Task<AdminUserDto?> FindUserAsync(long id, CancellationToken cancellationToken) =>
        FindAsync("users", UserColumns, id, ReadUser, cancellationToken);

    public Task<AdminBakeryDto?> FindBakeryAsync(long id, CancellationToken cancellationToken) =>
        FindAsync("bakeries", BakeryColumns, id, ReadBakery, cancellationToken);

    // All identifiers and column lists below are private, fixed SQL constants. User input is only bound parameters.
    private async Task<AdminPage<T>> ListAsync<T>(
        string table, string columns, string where, string? search, string? role, string? status, bool? isDemo,
        int page, int pageSize, Func<MySqlDataReader, T> map, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        // One statement snapshot for count and rows, including an empty/out-of-range page.
        await using var command = new MySqlCommand($"""
            WITH filtered AS (SELECT {columns} FROM {table} WHERE {where}),
                 paged AS (SELECT * FROM filtered ORDER BY id ASC LIMIT @limit OFFSET @offset)
            SELECT paged.*, totals.total_count
              FROM (SELECT COUNT(*) AS total_count FROM filtered) totals
              LEFT JOIN paged ON TRUE
             ORDER BY paged.id ASC
            """, connection);
        Add(command, "@search", MySqlDbType.VarChar, search is null ? null : LiteralPattern(search));
        Add(command, "@role", MySqlDbType.VarChar, role);
        Add(command, "@status", MySqlDbType.VarChar, status);
        Add(command, "@isDemo", MySqlDbType.Bool, isDemo);
        Add(command, "@limit", MySqlDbType.Int32, pageSize);
        Add(command, "@offset", MySqlDbType.Int64, (long)(page - 1) * pageSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<T>();
        long total = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            total = reader.GetInt64("total_count");
            if (!reader.IsDBNull(reader.GetOrdinal("id"))) items.Add(map(reader));
        }

        return new(items, page, pageSize, total);
    }

    private async Task<T?> FindAsync<T>(string table, string columns, long id, Func<MySqlDataReader, T> map, CancellationToken cancellationToken)
        where T : class
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new MySqlCommand($"SELECT {columns} FROM {table} WHERE id = @id", connection);
        Add(command, "@id", MySqlDbType.Int64, id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? map(reader) : null;
    }

    public async Task<AdminDashboardDto> DashboardAsync(bool? isDemo, CancellationToken cancellationToken)
    {
        var roles = Counts("customer", "bakery", "admin");
        var users = Counts("active", "disabled");
        var bakeries = Counts("draft", "pending_review", "active", "suspended", "rejected");
        var requests = Counts("draft", "open", "ordered", "cancelled");
        var orders = Counts("pending_confirmation", "confirmed", "rejected_by_bakery", "ready", "picked_up", "cancelled");
        Dictionary<string, long>[] groups = [roles, users, bakeries, requests, orders];
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        // A single statement also keeps all dashboard buckets on the same read snapshot.
        await using var command = new MySqlCommand("""
            SELECT 0 AS kind, system_role AS bucket, COUNT(*) AS total FROM users
             WHERE @isDemo IS NULL OR is_demo = @isDemo GROUP BY system_role
            UNION ALL
            SELECT 1, status, COUNT(*) FROM users
             WHERE @isDemo IS NULL OR is_demo = @isDemo GROUP BY status
            UNION ALL
            SELECT 2, profile_status, COUNT(*) FROM bakeries
             WHERE @isDemo IS NULL OR is_demo = @isDemo GROUP BY profile_status
            UNION ALL
            SELECT 3, status, COUNT(*) FROM design_requests
             WHERE @isDemo IS NULL OR is_demo = @isDemo GROUP BY status
            UNION ALL
            SELECT 4, o.status, COUNT(*) FROM orders o JOIN design_requests dr ON dr.id = o.design_request_id
             WHERE @isDemo IS NULL OR dr.is_demo = @isDemo GROUP BY o.status
            """, connection);
        Add(command, "@isDemo", MySqlDbType.Bool, isDemo);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            groups[reader.GetInt32("kind")][reader.GetString("bucket")] = reader.GetInt64("total");
        return new(isDemo, roles, users, bakeries, requests, orders);
    }

    private static Dictionary<string, long> Counts(params string[] keys) => keys.ToDictionary(k => k, _ => 0L);

    // Explicit SQL LIKE escape marker; user text is still bound as a parameter. Backslash is literal too.
    private static string LiteralPattern(string value) => "%" + value.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_") + "%";

    private static void Add(MySqlCommand command, string name, MySqlDbType type, object? value) =>
        command.Parameters.Add(name, type).Value = value ?? DBNull.Value;

    private static AdminUserDto ReadUser(MySqlDataReader reader) => new(
        reader.GetInt64("id"), reader.GetString("email"), reader.GetString("full_name"), Nullable(reader, "phone"),
        reader.GetString("system_role"), reader.GetString("status"), reader.GetBoolean("is_demo"),
        reader.GetDateTime("created_at"), reader.GetDateTime("updated_at"));

    private static AdminBakeryDto ReadBakery(MySqlDataReader reader) => new(
        reader.GetInt64("id"), reader.GetString("name"), reader.GetString("slug"), Nullable(reader, "description"),
        Nullable(reader, "address_line"), Nullable(reader, "city"), Nullable(reader, "phone"),
        reader.GetString("profile_status"), reader.GetBoolean("is_demo"), reader.GetDateTime("created_at"), reader.GetDateTime("updated_at"));

    private static string? Nullable(MySqlDataReader reader, string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetString(name);
}

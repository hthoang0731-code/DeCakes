using DeCakes.Application.Accounts;
using DeCakes.Domain.Accounts;
using DeCakes.Infrastructure.Accounts;
using MySqlConnector;

namespace DeCakes.Infrastructure.Tests.MySql;

/// <summary>
/// MySqlUserAccountStore thật trên MySQL 8.4 kiểm thử (database decakes_test_* tạm). Bị bỏ qua khi chưa cấu hình
/// DECAKES_TEST_MYSQL_ADMIN. Không dùng Aiven, secret development hay dữ liệu demo.
/// </summary>
[Collection(MySqlCollection.Name)]
public class MySqlUserAccountStoreTests(MySqlTestDatabase database)
{
    private static readonly AspNetPasswordHashService Hasher = new();
    private static CancellationToken Ct => CancellationToken.None;

    private MySqlUserAccountStore Store => new(database.Factory);

    private static string UniqueEmail(string prefix = "u") => $"{prefix}-{Guid.NewGuid():N}@example.com";

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await database.Factory.OpenConnectionAsync(Ct);
        await using var command = new MySqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync(Ct))!, typeof(T));
    }

    [MySqlFact]
    public async Task Fixture_runs_on_isolated_mysql_84_test_database()
    {
        Assert.True(database.Available);
        Assert.StartsWith("8.4.", database.ServerVersion);
        Assert.Equal(database.DatabaseName, await ScalarAsync<string>("SELECT DATABASE()"));
        Assert.StartsWith(MySqlTestSettings.UserPrefix, await ScalarAsync<string>("SELECT SUBSTRING_INDEX(CURRENT_USER(), '@', 1)"));
    }

    [MySqlFact]
    public async Task Insert_customer_sets_server_side_role_status_demo_and_reads_back()
    {
        var email = UniqueEmail();
        var hash = Hasher.Hash("mat-khau-du-dai-1");
        var before = DateTime.UtcNow.AddMinutes(-1);

        var id = await Store.CreateCustomerAsync(new NewCustomer(email, hash, "Nguyễn Văn A", Phone: null), Ct);

        await using var connection = await database.Factory.OpenConnectionAsync(Ct);
        await using var command = new MySqlCommand(
            "SELECT system_role, status, is_demo, phone, password_hash, created_at, updated_at FROM users WHERE id = @id", connection);
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        Assert.Equal(SystemRoles.Customer, reader.GetString(0));
        Assert.Equal(UserStatuses.Active, reader.GetString(1));
        Assert.False(reader.GetBoolean(2));
        Assert.True(reader.IsDBNull(3));
        Assert.Equal(hash, reader.GetString(4));
        var createdAt = reader.GetDateTime(5);
        var updatedAt = reader.GetDateTime(6);
        Assert.Equal(DateTimeKind.Utc, createdAt.Kind);
        Assert.InRange(createdAt, before, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(createdAt, updatedAt);

        var byId = await Store.FindByIdAsync(id, Ct);
        Assert.Equal(new UserAccount(id, email, "Nguyễn Văn A", null, SystemRoles.Customer, UserStatuses.Active), byId);
        var byEmail = await Store.FindByEmailAsync(email, Ct);
        Assert.Equal(byId, byEmail!.Account);
        Assert.Equal(hash, byEmail.PasswordHash);
    }

    [MySqlFact]
    public async Task Insert_customer_with_phone_reads_back_phone()
    {
        var id = await Store.CreateCustomerAsync(new NewCustomer(UniqueEmail(), "h", "A", "0901 234 567"), Ct);

        Assert.Equal("0901 234 567", (await Store.FindByIdAsync(id, Ct))!.Phone);
    }

    [MySqlFact]
    public async Task Duplicate_email_maps_1062_to_duplicate_email_exception()
    {
        var email = UniqueEmail("dup");
        await Store.CreateCustomerAsync(new NewCustomer(email, "h", "A", null), Ct);

        var error = await Assert.ThrowsAsync<DuplicateEmailException>(() =>
            Store.CreateCustomerAsync(new NewCustomer(email, "h", "B", null), Ct));

        var inner = Assert.IsType<MySqlException>(error.InnerException);
        Assert.Equal(MySqlErrorCode.DuplicateKeyEntry, inner.ErrorCode);
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM users WHERE email = @e", ("@e", email)));
    }

    [MySqlFact]
    public async Task Concurrent_registrations_with_same_email_create_exactly_one_row()
    {
        var email = UniqueEmail("race");
        var attempts = Enumerable.Range(0, 12).Select(async i =>
        {
            try
            {
                // Xen kẽ hoa/thường: collation ai_ci coi là cùng một email.
                var variant = i % 2 == 0 ? email : email.ToUpperInvariant();
                await Store.CreateCustomerAsync(new NewCustomer(variant, "h", $"User {i}", null), Ct);
                return true;
            }
            catch (DuplicateEmailException)
            {
                return false;
            }
        });

        var results = await Task.WhenAll(attempts);

        Assert.Equal(1, results.Count(created => created));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM users WHERE email = @e", ("@e", email)));
    }

    [MySqlFact]
    public async Task Email_comparison_follows_schema_collation()
    {
        Assert.Equal(
            "utf8mb4_0900_ai_ci",
            await ScalarAsync<string>(
                "SELECT collation_name FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'users' AND column_name = 'email'"));

        var token = Guid.NewGuid().ToString("N")[..10];
        var email = $"cafe-{token}@example.com";
        var id = await Store.CreateCustomerAsync(new NewCustomer(email, "h", "A", null), Ct);

        // Không phân biệt hoa thường khi tra cứu.
        Assert.Equal(id, (await Store.FindByEmailAsync(email.ToUpperInvariant(), Ct))!.Account.Id);
        // ai_ci không phân biệt dấu: "café" trùng "cafe" theo UNIQUE của database.
        await Assert.ThrowsAsync<DuplicateEmailException>(() =>
            Store.CreateCustomerAsync(new NewCustomer($"café-{token}@example.com", "h", "B", null), Ct));
    }

    [MySqlFact]
    public async Task Replace_hash_succeeds_when_current_hash_matches()
    {
        var oldHash = Hasher.Hash("mat-khau-du-dai-1");
        var id = await Store.CreateCustomerAsync(new NewCustomer(UniqueEmail(), oldHash, "A", null), Ct);
        var createdAt = await ScalarAsync<DateTime>("SELECT created_at FROM users WHERE id = @id", ("@id", id));
        var newHash = Hasher.Hash("mat-khau-du-dai-1");

        Assert.True(await Store.ReplacePasswordHashAsync(id, oldHash, newHash, Ct));

        Assert.Equal(newHash, await ScalarAsync<string>("SELECT password_hash FROM users WHERE id = @id", ("@id", id)));
        Assert.True(await ScalarAsync<DateTime>("SELECT updated_at FROM users WHERE id = @id", ("@id", id)) >= createdAt);
    }

    [MySqlFact]
    public async Task Replace_hash_does_not_overwrite_when_current_hash_differs()
    {
        var currentHash = Hasher.Hash("mat-khau-du-dai-1");
        var id = await Store.CreateCustomerAsync(new NewCustomer(UniqueEmail(), currentHash, "A", null), Ct);

        Assert.False(await Store.ReplacePasswordHashAsync(id, Hasher.Hash("khac-mat-khau-12"), "moi", Ct));

        Assert.Equal(currentHash, await ScalarAsync<string>("SELECT password_hash FROM users WHERE id = @id", ("@id", id)));
    }

    [MySqlFact]
    public async Task Replace_hash_compares_case_sensitively()
    {
        var currentHash = Hasher.Hash("mat-khau-du-dai-1");
        var differentCase = SwapCase(currentHash);
        Assert.NotEqual(currentHash, differentCase);
        var id = await Store.CreateCustomerAsync(new NewCustomer(UniqueEmail(), currentHash, "A", null), Ct);

        Assert.False(await Store.ReplacePasswordHashAsync(id, differentCase, "moi", Ct));

        Assert.Equal(currentHash, await ScalarAsync<string>("SELECT password_hash FROM users WHERE id = @id", ("@id", id)));
    }

    [MySqlFact]
    public async Task Test_account_has_no_delete_privilege_like_decakes_app()
    {
        await using var connection = await database.Factory.OpenConnectionAsync(Ct);
        await using var command = new MySqlCommand("DELETE FROM users WHERE id = -1", connection);

        var error = await Assert.ThrowsAsync<MySqlException>(() => command.ExecuteNonQueryAsync(Ct));
        Assert.Equal(MySqlErrorCode.TableAccessDenied, error.ErrorCode);
    }

    private static string SwapCase(string value) =>
        new(value.Select(c => char.IsUpper(c) ? char.ToLowerInvariant(c) : char.IsLower(c) ? char.ToUpperInvariant(c) : c).ToArray());
}

using System.Security.Cryptography;
using DeCakes.Infrastructure.Persistence;
using MySqlConnector;

namespace DeCakes.Infrastructure.Tests.MySql;

/// <summary>
/// Tạo một database decakes_test_* và một tài khoản dc_test_* riêng trên MySQL 8.4 kiểm thử (loopback),
/// nạp schema nguồn, rồi xóa cả hai khi xong. Tài khoản kiểm thử có quyền như decakes_app trên bảng users
/// (SELECT, INSERT, UPDATE; không DELETE).
/// </summary>
public sealed class MySqlTestDatabase : IAsyncLifetime
{
    private MySqlConnectionStringBuilder? _admin;

    public bool Available { get; private set; }

    public string DatabaseName { get; } = MySqlTestSettings.DatabasePrefix + Guid.NewGuid().ToString("N")[..12];

    public string UserName { get; } = MySqlTestSettings.UserPrefix + Guid.NewGuid().ToString("N")[..12];

    public string ServerVersion { get; private set; } = "";

    public IMySqlConnectionFactory Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var raw = MySqlTestSettings.AdminConnectionString;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return; // Không có MySQL kiểm thử: các ca [MySqlFact] đã được đánh dấu Skip.
        }

        _admin = MySqlTestSettings.ValidateAdminConnectionString(raw);
        if (!MySqlTestSettings.IsTestDatabaseName(DatabaseName))
        {
            throw new InvalidOperationException("Tên database kiểm thử không hợp lệ.");
        }

        await using (var connection = new MySqlConnection(_admin.ConnectionString))
        {
            await connection.OpenAsync();
            ServerVersion = connection.ServerVersion;
            if (!ServerVersion.StartsWith(MySqlTestSettings.RequiredServerVersionPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Cần MySQL {MySqlTestSettings.RequiredServerVersionPrefix}x, server là {ServerVersion}.");
            }

            await ExecuteAsync(connection, $"CREATE DATABASE `{DatabaseName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci");
        }

        // Nạp schema trong đúng database tạm; kiểm tra lại database hiện tại trước khi chạy script.
        var schemaConnection = new MySqlConnectionStringBuilder(_admin.ConnectionString) { Database = DatabaseName };
        await using (var connection = new MySqlConnection(schemaConnection.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var check = new MySqlCommand("SELECT DATABASE()", connection))
            {
                if (!string.Equals(await check.ExecuteScalarAsync() as string, DatabaseName, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Kết nối nạp schema không ở database kiểm thử.");
                }
            }

            await ExecuteAsync(connection, SchemaScript.LoadSanitizedSchema());
        }

        // Tài khoản kiểm thử tạo sau khi có bảng: GRANT mức bảng cần bảng đã tồn tại.
        await using (var connection = new MySqlConnection(_admin.ConnectionString))
        {
            await connection.OpenAsync();
            var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
            await ExecuteAsync(connection, $"CREATE USER '{UserName}'@'%' IDENTIFIED BY @password", ("@password", password));
            await ExecuteAsync(connection, $"GRANT SELECT ON `{DatabaseName}`.* TO '{UserName}'@'%'");
            await ExecuteAsync(connection, $"GRANT INSERT, UPDATE ON `{DatabaseName}`.`users` TO '{UserName}'@'%'");

            var testUser = new MySqlConnectionStringBuilder(_admin.ConnectionString)
            {
                Database = DatabaseName,
                UserID = UserName,
                Password = password,
                DateTimeKind = MySqlDateTimeKind.Utc,
            };
            Factory = new TestConnectionFactory(testUser.ConnectionString);
        }

        Available = true;
    }

    /// <summary>Nạp một tiệm bằng kết nối setup, chỉ trong database kiểm thử riêng; không tăng quyền của Factory.</summary>
    public async Task<long> SeedBakeryAsync(
        string name, string profileStatus = "active", bool isDemo = false, string? city = null,
        string? description = null, string? addressLine = null, string? phone = null)
    {
        if (!Available || _admin is null || !MySqlTestSettings.IsTestDatabaseName(DatabaseName))
        {
            throw new InvalidOperationException("Chưa có database kiểm thử an toàn để nạp bakery.");
        }

        var setup = new MySqlConnectionStringBuilder(_admin.ConnectionString) { Database = DatabaseName };
        await using var connection = new MySqlConnection(setup.ConnectionString);
        await connection.OpenAsync();
        await using (var check = new MySqlCommand("SELECT DATABASE()", connection))
        {
            if (!string.Equals(await check.ExecuteScalarAsync() as string, DatabaseName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Kết nối setup không ở database kiểm thử.");
            }
        }

        await using var command = new MySqlCommand(
            """
            INSERT INTO bakeries (name, slug, city, profile_status, is_demo, description, address_line, phone)
            VALUES (@name, @slug, @city, @profileStatus, @isDemo, @description, @addressLine, @phone)
            """, connection);
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@slug", "test-" + Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@city", (object?)city ?? DBNull.Value);
        command.Parameters.AddWithValue("@profileStatus", profileStatus);
        command.Parameters.AddWithValue("@isDemo", isDemo);
        command.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
        command.Parameters.AddWithValue("@addressLine", (object?)addressLine ?? DBNull.Value);
        command.Parameters.AddWithValue("@phone", (object?)phone ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
        return command.LastInsertedId;
    }

    public async Task<long> SeedCakeAsync(
        long bakeryId, string name, string visibility = "published", bool isDemo = false,
        decimal? referencePrice = null, string? description = null, string? flavor = null, string? sizeLabel = null)
    {
        if (!Available || _admin is null || !MySqlTestSettings.IsTestDatabaseName(DatabaseName))
        {
            throw new InvalidOperationException("Chưa có database kiểm thử an toàn để nạp cake.");
        }

        var setup = new MySqlConnectionStringBuilder(_admin.ConnectionString) { Database = DatabaseName };
        await using var connection = new MySqlConnection(setup.ConnectionString);
        await connection.OpenAsync();
        await using (var check = new MySqlCommand("SELECT DATABASE()", connection))
        {
            if (!string.Equals(await check.ExecuteScalarAsync() as string, DatabaseName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Kết nối setup không ở database kiểm thử.");
            }
        }

        await using var command = new MySqlCommand(
            """
            INSERT INTO cakes (bakery_id, name, visibility, is_demo, reference_price, description, flavor, size_label)
            VALUES (@bakeryId, @name, @visibility, @isDemo, @price, @description, @flavor, @sizeLabel)
            """, connection);
        command.Parameters.AddWithValue("@bakeryId", bakeryId);
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@visibility", visibility);
        command.Parameters.AddWithValue("@isDemo", isDemo);
        command.Parameters.AddWithValue("@price", (object?)referencePrice ?? DBNull.Value);
        command.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
        command.Parameters.AddWithValue("@flavor", (object?)flavor ?? DBNull.Value);
        command.Parameters.AddWithValue("@sizeLabel", (object?)sizeLabel ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
        return command.LastInsertedId;
    }

    public async Task DisposeAsync()
    {
        if (_admin is null)
        {
            return;
        }

        await using var connection = new MySqlConnection(_admin.ConnectionString);
        await connection.OpenAsync();
        if (MySqlTestSettings.IsTestDatabaseName(DatabaseName))
        {
            await ExecuteAsync(connection, $"DROP DATABASE IF EXISTS `{DatabaseName}`");
        }

        await ExecuteAsync(connection, $"DROP USER IF EXISTS '{UserName}'@'%'");
    }

    private static async Task ExecuteAsync(MySqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new MySqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private sealed class TestConnectionFactory(string connectionString) : IMySqlConnectionFactory
    {
        public async Task<MySqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
    }
}

[CollectionDefinition(Name)]
public sealed class MySqlCollection : ICollectionFixture<MySqlTestDatabase>
{
    public const string Name = "MySQL kiểm thử";
}

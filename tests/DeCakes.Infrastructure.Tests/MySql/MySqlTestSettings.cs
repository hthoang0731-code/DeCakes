using System.Net;
using MySqlConnector;

namespace DeCakes.Infrastructure.Tests.MySql;

/// <summary>
/// Cấu hình và chốt bảo vệ cho kiểm thử MySQL thật. Chỉ đọc biến môi trường riêng của kiểm thử,
/// không đọc user-secrets hay ConnectionStrings:DeCakesDb của ứng dụng.
/// </summary>
public static class MySqlTestSettings
{
    /// <summary>Chuỗi kết nối tài khoản quản trị của MySQL 8.4 kiểm thử chạy trên máy (loopback).</summary>
    public const string AdminConnectionVariable = "DECAKES_TEST_MYSQL_ADMIN";

    public const string DatabasePrefix = "decakes_test_";
    public const string UserPrefix = "dc_test_";
    public const string RequiredServerVersionPrefix = "8.4.";

    public static string? AdminConnectionString => Environment.GetEnvironmentVariable(AdminConnectionVariable);

    /// <summary>
    /// Từ chối mọi cấu hình có thể trỏ tới database thật: host không phải loopback, Aiven, tài khoản của ứng dụng
    /// hoặc quản trị Aiven, hay có sẵn tên database. Ném lỗi thay vì bỏ qua để không chạy nhầm.
    /// </summary>
    public static MySqlConnectionStringBuilder ValidateAdminConnectionString(string connectionString)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString);
        var hosts = builder.Server.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (hosts.Length == 0 || !hosts.All(IsLoopback))
        {
            throw new InvalidOperationException($"{AdminConnectionVariable}: chỉ chấp nhận MySQL kiểm thử trên loopback (localhost, 127.0.0.1, ::1).");
        }

        if (connectionString.Contains("aivencloud", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{AdminConnectionVariable}: không được trỏ tới Aiven.");
        }

        if (builder.UserID.Equals("avnadmin", StringComparison.OrdinalIgnoreCase)
            || builder.UserID.Equals("decakes_app", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{AdminConnectionVariable}: không dùng tài khoản của Aiven hoặc của ứng dụng.");
        }

        if (!string.IsNullOrEmpty(builder.Database))
        {
            throw new InvalidOperationException($"{AdminConnectionVariable}: không đặt Database; kiểm thử tự tạo database {DatabasePrefix}*.");
        }

        return builder;
    }

    public static bool IsTestDatabaseName(string name) =>
        name.StartsWith(DatabasePrefix, StringComparison.Ordinal) && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_');

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));
}

/// <summary>Bỏ qua kiểm thử khi chưa cấu hình MySQL kiểm thử (không chuyển sang database khác).</summary>
public sealed class MySqlFactAttribute : FactAttribute
{
    public MySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(MySqlTestSettings.AdminConnectionString))
        {
            Skip = $"Chưa có MySQL 8.4 kiểm thử: đặt biến môi trường {MySqlTestSettings.AdminConnectionVariable} (xem tests/README.md).";
        }
    }
}

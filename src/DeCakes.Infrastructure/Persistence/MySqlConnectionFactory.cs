using Microsoft.Extensions.Configuration;
using MySqlConnector;

namespace DeCakes.Infrastructure.Persistence;

public sealed class MySqlConnectionFactory : IMySqlConnectionFactory
{
    public const string ConnectionStringName = "DeCakesDb";

    private readonly Lazy<string> _connectionString;

    // Không mở kết nối và không kiểm tra cấu hình ở đây: ứng dụng vẫn khởi động được khi chưa có secret
    // (ví dụ /api/health). Cấu hình được kiểm tra ở lần mở kết nối đầu tiên.
    public MySqlConnectionFactory(IConfiguration configuration)
    {
        _connectionString = new Lazy<string>(
            () => BuildValidatedConnectionString(configuration.GetConnectionString(ConnectionStringName)),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public async Task<MySqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new MySqlConnection(_connectionString.Value);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    // Thông báo lỗi không chứa giá trị cấu hình (chuỗi kết nối, host, tài khoản, mật khẩu).
    internal static string BuildValidatedConnectionString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.TrimStart().StartsWith('<'))
        {
            throw new InvalidOperationException(
                $"Thiếu cấu hình ConnectionStrings:{ConnectionStringName}. Dùng user-secrets hoặc biến môi trường (xem README).");
        }

        MySqlConnectionStringBuilder builder;
        try
        {
            builder = new MySqlConnectionStringBuilder(raw);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException($"ConnectionStrings:{ConnectionStringName} không hợp lệ.");
        }

        if (builder.SslMode != MySqlSslMode.VerifyFull)
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{ConnectionStringName} phải dùng SslMode=VerifyFull.");
        }

        if (string.IsNullOrWhiteSpace(builder.SslCa) || !File.Exists(builder.SslCa))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{ConnectionStringName} phải có SslCa trỏ tới file CA tồn tại.");
        }

        if (string.IsNullOrWhiteSpace(builder.UserID)
            || builder.UserID.Equals("avnadmin", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{ConnectionStringName} phải dùng tài khoản backend riêng, không phải avnadmin.");
        }

        // Dữ liệu thời gian trong schema là UTC (DATETIME(3)); đọc ra với Kind = Utc.
        builder.DateTimeKind = MySqlDateTimeKind.Utc;
        return builder.ConnectionString;
    }
}

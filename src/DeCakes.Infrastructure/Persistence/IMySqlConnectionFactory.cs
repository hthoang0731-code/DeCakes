using MySqlConnector;

namespace DeCakes.Infrastructure.Persistence;

/// <summary>
/// Tạo và mở connection MySQL cho từng thao tác. Người gọi sở hữu connection và phải dispose.
/// Chỉ dùng trong Infrastructure; kiểu MySqlConnector không đi vào Application hoặc Domain.
/// </summary>
public interface IMySqlConnectionFactory
{
    Task<MySqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);
}

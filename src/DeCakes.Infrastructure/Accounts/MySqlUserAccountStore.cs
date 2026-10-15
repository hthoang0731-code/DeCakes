using DeCakes.Application.Accounts;
using DeCakes.Infrastructure.Persistence;
using MySqlConnector;

namespace DeCakes.Infrastructure.Accounts;

public sealed class MySqlUserAccountStore(IMySqlConnectionFactory connectionFactory) : IUserAccountStore
{
    private const string AccountColumns = "id, email, full_name, phone, system_role, status";

    public async Task<UserCredentials?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken)
    {
        // So sánh theo collation của cột (utf8mb4_0900_ai_ci); không đổi quy tắc so sánh ở đây.
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new MySqlCommand(
            $"SELECT {AccountColumns}, password_hash FROM users WHERE email = @email LIMIT 1", connection);
        command.Parameters.Add("@email", MySqlDbType.VarChar).Value = normalizedEmail;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var hashOrdinal = reader.GetOrdinal("password_hash");
        return new UserCredentials(ReadAccount(reader), reader.IsDBNull(hashOrdinal) ? null : reader.GetString(hashOrdinal));
    }

    public async Task<UserAccount?> FindByIdAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new MySqlCommand($"SELECT {AccountColumns} FROM users WHERE id = @id", connection);
        command.Parameters.Add("@id", MySqlDbType.Int64).Value = id;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAccount(reader) : null;
    }

    public async Task<long> CreateCustomerAsync(NewCustomer customer, CancellationToken cancellationToken)
    {
        // Vai trò, trạng thái và is_demo cố định ở server, không lấy từ dữ liệu client.
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new MySqlCommand(
            """
            INSERT INTO users (email, password_hash, full_name, phone, system_role, status, is_demo)
            VALUES (@email, @passwordHash, @fullName, @phone, 'customer', 'active', 0)
            """,
            connection);
        command.Parameters.Add("@email", MySqlDbType.VarChar).Value = customer.Email;
        command.Parameters.Add("@passwordHash", MySqlDbType.VarChar).Value = customer.PasswordHash;
        command.Parameters.Add("@fullName", MySqlDbType.VarChar).Value = customer.FullName;
        command.Parameters.Add("@phone", MySqlDbType.VarChar).Value = (object?)customer.Phone ?? DBNull.Value;

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            // UNIQUE uq_users_email là nguồn sự thật, kể cả khi hai lần đăng ký chạy đồng thời.
            // Khóa UNIQUE khác duy nhất của users là PRIMARY KEY tự tăng, không thể trùng từ câu INSERT này.
            throw new DuplicateEmailException(ex);
        }

        return command.LastInsertedId;
    }

    public async Task<bool> ReplacePasswordHashAsync(
        long id, string expectedCurrentHash, string newHash, CancellationToken cancellationToken)
    {
        // So sánh hash theo nhị phân: collation mặc định của cột không phân biệt hoa thường.
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new MySqlCommand(
            """
            UPDATE users
               SET password_hash = @newHash, updated_at = UTC_TIMESTAMP(3)
             WHERE id = @id AND password_hash COLLATE utf8mb4_bin = @expectedHash
            """,
            connection);
        command.Parameters.Add("@newHash", MySqlDbType.VarChar).Value = newHash;
        command.Parameters.Add("@id", MySqlDbType.Int64).Value = id;
        command.Parameters.Add("@expectedHash", MySqlDbType.VarChar).Value = expectedCurrentHash;

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static UserAccount ReadAccount(MySqlDataReader reader)
    {
        var phoneOrdinal = reader.GetOrdinal("phone");
        return new UserAccount(
            reader.GetInt64(reader.GetOrdinal("id")),
            reader.GetString(reader.GetOrdinal("email")),
            reader.GetString(reader.GetOrdinal("full_name")),
            reader.IsDBNull(phoneOrdinal) ? null : reader.GetString(phoneOrdinal),
            reader.GetString(reader.GetOrdinal("system_role")),
            reader.GetString(reader.GetOrdinal("status")));
    }
}

using DeCakes.Application.Accounts;
using DeCakes.Domain.Accounts;
using DeCakes.Infrastructure.Persistence;
using MySqlConnector;

namespace DeCakes.Infrastructure.Accounts;

/// <summary>
/// user_tokens và đổi hash (migrations/V003__account_security.sql). Không đi qua 6 procedure nghiệp vụ, nên mỗi thao
/// tác nhiều câu lệnh dùng transaction riêng của ứng dụng. Mọi lần đọc dùng để quyết định nằm trong transaction và là
/// locking read (FOR UPDATE), thứ tự khóa user_tokens rồi users. Token chỉ được tra theo SHA-256, không ghi log.
/// </summary>
public sealed class MySqlAccountSecurityStore(IMySqlConnectionFactory connectionFactory) : IAccountSecurityStore
{
    public async Task<UserCredentials?> FindCredentialsByIdAsync(long userId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new MySqlCommand(
            $"SELECT {MySqlUserAccountStore.AccountColumns}, password_hash FROM users WHERE id = @id", connection);
        command.Parameters.Add("@id", MySqlDbType.Int64).Value = userId;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var hashOrdinal = reader.GetOrdinal("password_hash");
        return new UserCredentials(
            MySqlUserAccountStore.ReadAccount(reader), reader.IsDBNull(hashOrdinal) ? null : reader.GetString(hashOrdinal));
    }

    public async Task<TokenIssueStats> GetIssueStatsAsync(
        long userId, string purpose, DateTime since, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new MySqlCommand(
            """
            SELECT COUNT(*) AS issued, MAX(created_at) AS last_issued
              FROM user_tokens
             WHERE user_id = @userId AND purpose = @purpose AND created_at >= @since
            """,
            connection);
        command.Parameters.Add("@userId", MySqlDbType.Int64).Value = userId;
        command.Parameters.Add("@purpose", MySqlDbType.VarChar).Value = purpose;
        command.Parameters.Add("@since", MySqlDbType.DateTime).Value = since;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var lastOrdinal = reader.GetOrdinal("last_issued");
        return new TokenIssueStats(
            Convert.ToInt32(reader.GetValue(reader.GetOrdinal("issued"))),
            reader.IsDBNull(lastOrdinal) ? null : reader.GetDateTime(lastOrdinal));
    }

    public async Task IssueTokenAsync(NewAccountToken token, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await RevokeOpenTokensAsync(connection, transaction, token.UserId, token.Purpose, token.CreatedAt, cancellationToken);

        await using (var insert = new MySqlCommand(
                         """
                         INSERT INTO user_tokens (user_id, purpose, token_hash, email, created_at, expires_at)
                         VALUES (@userId, @purpose, @tokenHash, @email, @createdAt, @expiresAt)
                         """,
                         connection, transaction))
        {
            insert.Parameters.Add("@userId", MySqlDbType.Int64).Value = token.UserId;
            insert.Parameters.Add("@purpose", MySqlDbType.VarChar).Value = token.Purpose;
            insert.Parameters.Add("@tokenHash", MySqlDbType.Binary).Value = token.TokenHash;
            insert.Parameters.Add("@email", MySqlDbType.VarChar).Value = token.Email;
            insert.Parameters.Add("@createdAt", MySqlDbType.DateTime).Value = token.CreatedAt;
            insert.Parameters.Add("@expiresAt", MySqlDbType.DateTime).Value = token.ExpiresAt;
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> ConfirmEmailAsync(byte[] tokenHash, DateTime now, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var token = await LockUsableTokenAsync(
            connection, transaction, tokenHash, AccountTokenPurposes.EmailVerification, now, cancellationToken);
        if (token is null)
        {
            return false; // Dispose rollback, không đổi gì.
        }

        var user = await LockUserAsync(connection, transaction, token.UserId, cancellationToken);
        if (user is null || user.Status != UserStatuses.Active || !string.Equals(user.Email, token.Email, StringComparison.Ordinal))
        {
            return false;
        }

        await ConsumeAsync(connection, transaction, token.Id, now, cancellationToken);
        await using (var verify = new MySqlCommand(
                         """
                         UPDATE users
                            SET email_verified_at = COALESCE(email_verified_at, @now), updated_at = @now
                          WHERE id = @userId
                         """,
                         connection, transaction))
        {
            verify.Parameters.Add("@now", MySqlDbType.DateTime).Value = now;
            verify.Parameters.Add("@userId", MySqlDbType.Int64).Value = token.UserId;
            await verify.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> ResetPasswordAsync(
        byte[] tokenHash, string newPasswordHash, DateTime now, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var token = await LockUsableTokenAsync(
            connection, transaction, tokenHash, AccountTokenPurposes.PasswordReset, now, cancellationToken);
        if (token is null)
        {
            return false;
        }

        // Không mở lại user disabled, không đặt mật khẩu cho tài khoản demo, token mất hiệu lực khi email đã đổi.
        var user = await LockUserAsync(connection, transaction, token.UserId, cancellationToken);
        if (user is null || user.Status != UserStatuses.Active || user.IsDemo
            || !string.Equals(user.Email, token.Email, StringComparison.Ordinal))
        {
            return false;
        }

        await ConsumeAsync(connection, transaction, token.Id, now, cancellationToken);
        await using (var update = new MySqlCommand(
                         """
                         UPDATE users
                            SET password_hash = @hash,
                                session_version = session_version + 1,
                                email_verified_at = COALESCE(email_verified_at, @now),
                                updated_at = @now
                          WHERE id = @userId AND status = 'active'
                         """,
                         connection, transaction))
        {
            update.Parameters.Add("@hash", MySqlDbType.VarChar).Value = newPasswordHash;
            update.Parameters.Add("@now", MySqlDbType.DateTime).Value = now;
            update.Parameters.Add("@userId", MySqlDbType.Int64).Value = token.UserId;
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                return false;
            }
        }

        await RevokeOpenTokensAsync(
            connection, transaction, token.UserId, AccountTokenPurposes.PasswordReset, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<long?> ChangePasswordAsync(
        long userId, string expectedCurrentHash, string newPasswordHash, DateTime now, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Khóa token đặt lại còn mở trước users (cùng thứ tự với ResetPasswordAsync).
        await using (var lockTokens = new MySqlCommand(
                         """
                         SELECT id FROM user_tokens
                          WHERE user_id = @userId AND purpose = @purpose AND consumed_at IS NULL AND revoked_at IS NULL
                          ORDER BY id
                            FOR UPDATE
                         """,
                         connection, transaction))
        {
            lockTokens.Parameters.Add("@userId", MySqlDbType.Int64).Value = userId;
            lockTokens.Parameters.Add("@purpose", MySqlDbType.VarChar).Value = AccountTokenPurposes.PasswordReset;
            await using var reader = await lockTokens.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
            }
        }

        // So sánh hash theo nhị phân: collation mặc định của cột không phân biệt hoa thường.
        await using (var update = new MySqlCommand(
                         """
                         UPDATE users
                            SET password_hash = @newHash, session_version = session_version + 1, updated_at = @now
                          WHERE id = @userId AND status = 'active' AND password_hash COLLATE utf8mb4_bin = @expectedHash
                         """,
                         connection, transaction))
        {
            update.Parameters.Add("@newHash", MySqlDbType.VarChar).Value = newPasswordHash;
            update.Parameters.Add("@now", MySqlDbType.DateTime).Value = now;
            update.Parameters.Add("@userId", MySqlDbType.Int64).Value = userId;
            update.Parameters.Add("@expectedHash", MySqlDbType.VarChar).Value = expectedCurrentHash;
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                return null;
            }
        }

        await RevokeOpenTokensAsync(connection, transaction, userId, AccountTokenPurposes.PasswordReset, now, cancellationToken);

        long version;
        await using (var read = new MySqlCommand("SELECT session_version FROM users WHERE id = @userId", connection, transaction))
        {
            read.Parameters.Add("@userId", MySqlDbType.Int64).Value = userId;
            version = Convert.ToInt64(await read.ExecuteScalarAsync(cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        return version;
    }

    private sealed record LockedToken(long Id, long UserId, string Email);

    /// <summary>Khóa dòng token theo hash; null nếu không có, sai mục đích, đã dùng, đã thu hồi hoặc hết hạn.</summary>
    private static async Task<LockedToken?> LockUsableTokenAsync(
        MySqlConnection connection, MySqlTransaction transaction, byte[] tokenHash, string purpose, DateTime now,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            """
            SELECT id, user_id, purpose, email, expires_at, consumed_at, revoked_at
              FROM user_tokens
             WHERE token_hash = @tokenHash
               FOR UPDATE
            """,
            connection, transaction);
        command.Parameters.Add("@tokenHash", MySqlDbType.Binary).Value = tokenHash;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var usable = reader.GetString(reader.GetOrdinal("purpose")) == purpose
                     && reader.IsDBNull(reader.GetOrdinal("consumed_at"))
                     && reader.IsDBNull(reader.GetOrdinal("revoked_at"))
                     && reader.GetDateTime(reader.GetOrdinal("expires_at")) > now;
        return usable
            ? new LockedToken(
                reader.GetInt64(reader.GetOrdinal("id")),
                reader.GetInt64(reader.GetOrdinal("user_id")),
                reader.GetString(reader.GetOrdinal("email")))
            : null;
    }

    private static async Task<UserAccount?> LockUserAsync(
        MySqlConnection connection, MySqlTransaction transaction, long userId, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            $"SELECT {MySqlUserAccountStore.AccountColumns} FROM users WHERE id = @userId FOR UPDATE", connection, transaction);
        command.Parameters.Add("@userId", MySqlDbType.Int64).Value = userId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MySqlUserAccountStore.ReadAccount(reader) : null;
    }

    private static async Task ConsumeAsync(
        MySqlConnection connection, MySqlTransaction transaction, long tokenId, DateTime now, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            "UPDATE user_tokens SET consumed_at = @now WHERE id = @id AND consumed_at IS NULL AND revoked_at IS NULL",
            connection, transaction);
        command.Parameters.Add("@now", MySqlDbType.DateTime).Value = now;
        command.Parameters.Add("@id", MySqlDbType.Int64).Value = tokenId;
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            // Dòng đã bị khóa FOR UPDATE ở trên nên không thể xảy ra; dừng thay vì ghi tiếp.
            throw new InvalidOperationException("Token không còn ở trạng thái dùng được.");
        }
    }

    private static async Task RevokeOpenTokensAsync(
        MySqlConnection connection, MySqlTransaction transaction, long userId, string purpose, DateTime now,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            """
            UPDATE user_tokens SET revoked_at = @now
             WHERE user_id = @userId AND purpose = @purpose AND consumed_at IS NULL AND revoked_at IS NULL
            """,
            connection, transaction);
        command.Parameters.Add("@now", MySqlDbType.DateTime).Value = now;
        command.Parameters.Add("@userId", MySqlDbType.Int64).Value = userId;
        command.Parameters.Add("@purpose", MySqlDbType.VarChar).Value = purpose;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

using DeCakes.Application.Accounts;
using DeCakes.Application.Admin;
using DeCakes.Infrastructure.Persistence;
using MySqlConnector;

namespace DeCakes.Infrastructure.Admin;

/// <summary>Operator-only INSERT. UNIQUE email handles duplicates and races; never UPDATE an existing user.</summary>
public sealed class MySqlAdminProvisioningStore(IMySqlConnectionFactory connectionFactory) : IAdminProvisioningStore
{
    public async Task<long> CreateAdminAsync(string email, string fullName, string passwordHash, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            INSERT INTO users (email, full_name, password_hash, system_role, status, is_demo)
            VALUES (@email, @fullName, @hash, 'admin', 'active', 0)
            """, connection);
        command.Parameters.Add("@email", MySqlDbType.VarChar).Value = email;
        command.Parameters.Add("@fullName", MySqlDbType.VarChar).Value = fullName;
        command.Parameters.Add("@hash", MySqlDbType.VarChar).Value = passwordHash;
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            throw new DuplicateEmailException(ex);
        }

        return command.LastInsertedId;
    }
}

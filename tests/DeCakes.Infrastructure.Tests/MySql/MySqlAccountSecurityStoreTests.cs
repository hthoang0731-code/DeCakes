using System.Security.Cryptography;
using DeCakes.Application.Accounts;
using DeCakes.Domain.Accounts;
using DeCakes.Infrastructure.Accounts;
using MySqlConnector;

namespace DeCakes.Infrastructure.Tests.MySql;

/// <summary>
/// MySqlAccountSecurityStore và migration đề xuất V003 trên MySQL 8.4 kiểm thử (database decakes_test_* tạm, tài khoản có
/// quyền backend + grants V003). Bị bỏ qua khi chưa cấu hình DECAKES_TEST_MYSQL_ADMIN. Không dùng Aiven.
/// </summary>
[Collection(MySqlCollection.Name)]
public class MySqlAccountSecurityStoreTests(MySqlTestDatabase database)
{
    private static readonly DateTime Now = new(2026, 10, 22, 1, 0, 0, 123, DateTimeKind.Utc);
    private static CancellationToken Ct => CancellationToken.None;

    private MySqlAccountSecurityStore Store => new(database.Factory);

    private MySqlUserAccountStore Users => new(database.Factory);

    private static string UniqueEmail() => $"s-{Guid.NewGuid():N}@example.com";

    private Task<long> SeedUserAsync(string email, string status = "active", bool isDemo = false, string hash = "HASH-cu") =>
        database.AdminExecuteAsync(
            "INSERT INTO users (email, password_hash, full_name, system_role, status, is_demo) VALUES (@e, @h, 'Người dùng', 'customer', @s, @d)",
            ("@e", email), ("@h", hash), ("@s", status), ("@d", isDemo));

    private async Task<(string Token, byte[] Hash)> IssueAsync(long userId, string email, string purpose, DateTime? at = null, TimeSpan? lifetime = null)
    {
        var (token, hash) = AccountTokens.Create();
        var created = at ?? Now;
        await Store.IssueTokenAsync(new NewAccountToken(userId, purpose, hash, email, created, created + (lifetime ?? TimeSpan.FromHours(1))), Ct);
        return (token, hash);
    }

    private async Task<Dictionary<string, object?>> UserRowAsync(long id)
    {
        await using var connection = await database.AdminFactory.OpenConnectionAsync(Ct);
        await using var command = MySqlTestDatabase.Command(connection,
            "SELECT email, password_hash, status, email_verified_at, session_version, updated_at FROM users WHERE id = @id", ("@id", id));
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        return Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, i => reader.IsDBNull(i) ? null : reader.GetValue(i));
    }

    private async Task<List<(long Id, string Purpose, bool Consumed, bool Revoked)>> TokensAsync(long userId)
    {
        await using var connection = await database.AdminFactory.OpenConnectionAsync(Ct);
        await using var command = MySqlTestDatabase.Command(connection,
            "SELECT id, purpose, consumed_at IS NOT NULL, revoked_at IS NOT NULL FROM user_tokens WHERE user_id = @u ORDER BY id", ("@u", userId));
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<(long, string, bool, bool)>();
        while (await reader.ReadAsync(Ct)) rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2) == 1, reader.GetInt64(3) == 1));
        return rows;
    }

    [MySqlFact]
    public async Task Migration_adds_unverified_default_and_records_version_three()
    {
        var existing = await SeedUserAsync(UniqueEmail());
        var registered = await Users.CreateCustomerAsync(new NewCustomer(UniqueEmail(), "H", "Khách", null), Ct);

        Assert.Equal(1L, Convert.ToInt64(await database.AdminScalarAsync("SELECT COUNT(*) FROM schema_migrations WHERE version = 3")));
        foreach (var id in new[] { existing, registered })
        {
            var row = await UserRowAsync(id);
            Assert.Null(row["email_verified_at"]);
            Assert.Equal(0L, Convert.ToInt64(row["session_version"]));
            var account = await Users.FindByIdAsync(id, Ct);
            Assert.False(account!.EmailVerified);
            Assert.Equal(0, account.SessionVersion);
        }
    }

    [MySqlFact]
    public async Task Issue_stores_only_digest_revokes_previous_same_purpose_and_reports_stats()
    {
        var email = UniqueEmail();
        var id = await SeedUserAsync(email);
        var (token, hash) = await IssueAsync(id, email, AccountTokenPurposes.PasswordReset);
        await IssueAsync(id, email, AccountTokenPurposes.EmailVerification);
        await IssueAsync(id, email, AccountTokenPurposes.PasswordReset, Now.AddSeconds(90));

        var stored = (byte[])(await database.AdminScalarAsync("SELECT token_hash FROM user_tokens WHERE user_id = @u ORDER BY id LIMIT 1", ("@u", id)))!;
        Assert.Equal(hash, stored);
        Assert.Equal(SHA256.HashData(Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/') + "=")), stored);
        Assert.Equal(
            [(AccountTokenPurposes.PasswordReset, false, true), (AccountTokenPurposes.EmailVerification, false, false), (AccountTokenPurposes.PasswordReset, false, false)],
            (await TokensAsync(id)).Select(t => (t.Purpose, t.Consumed, t.Revoked)));

        var stats = await Store.GetIssueStatsAsync(id, AccountTokenPurposes.PasswordReset, Now, Ct);
        Assert.Equal(2, stats.Count);
        Assert.Equal(Now.AddSeconds(90), stats.LastIssuedAt);
        Assert.Equal(1, (await Store.GetIssueStatsAsync(id, AccountTokenPurposes.PasswordReset, Now.AddSeconds(1), Ct)).Count);
    }

    [MySqlFact]
    public async Task Confirm_marks_verified_once_and_rejects_reuse_wrong_purpose_and_expiry()
    {
        var email = UniqueEmail();
        var id = await SeedUserAsync(email);
        var (_, resetHash) = await IssueAsync(id, email, AccountTokenPurposes.PasswordReset);
        var (_, verifyHash) = await IssueAsync(id, email, AccountTokenPurposes.EmailVerification);

        Assert.False(await Store.ConfirmEmailAsync(resetHash, Now, Ct));
        Assert.False(await Store.ConfirmEmailAsync(verifyHash, Now.AddHours(1), Ct)); // expires_at <= now
        Assert.False(await Store.ConfirmEmailAsync(SHA256.HashData([1, 2, 3]), Now, Ct));
        Assert.Null((await UserRowAsync(id))["email_verified_at"]);

        Assert.True(await Store.ConfirmEmailAsync(verifyHash, Now.AddMinutes(5), Ct));
        Assert.Equal(Now.AddMinutes(5), (DateTime)(await UserRowAsync(id))["email_verified_at"]!);
        Assert.False(await Store.ConfirmEmailAsync(verifyHash, Now.AddMinutes(6), Ct));
        Assert.True((await Users.FindByIdAsync(id, Ct))!.EmailVerified);
    }

    [MySqlFact]
    public async Task Confirm_and_reset_fail_when_user_changed_after_issue_and_leave_rows_untouched()
    {
        foreach (var change in new[] { "email-changed", "disabled" })
        {
            await AssertUserChangeBlocksTokensAsync(change);
        }
    }

    private async Task AssertUserChangeBlocksTokensAsync(string change)
    {
        var email = UniqueEmail();
        var id = await SeedUserAsync(email);
        var (_, verifyHash) = await IssueAsync(id, email, AccountTokenPurposes.EmailVerification);
        var (_, resetHash) = await IssueAsync(id, email, AccountTokenPurposes.PasswordReset);
        await database.AdminExecuteAsync(change == "disabled"
            ? "UPDATE users SET status = 'disabled' WHERE id = @id"
            : "UPDATE users SET email = CONCAT('doi-', email) WHERE id = @id", ("@id", id));
        var before = await UserRowAsync(id);

        Assert.False(await Store.ConfirmEmailAsync(verifyHash, Now, Ct));
        Assert.False(await Store.ResetPasswordAsync(resetHash, "HASH-moi", Now, Ct));

        Assert.Equal(before, await UserRowAsync(id));
        Assert.All(await TokensAsync(id), t => Assert.False(t.Consumed));
    }

    [MySqlFact]
    public async Task Reset_replaces_hash_bumps_session_version_verifies_and_revokes_other_reset_tokens_atomically()
    {
        var email = UniqueEmail();
        var id = await SeedUserAsync(email);
        var (_, older) = await IssueAsync(id, email, AccountTokenPurposes.PasswordReset);
        var (_, hash) = await IssueAsync(id, email, AccountTokenPurposes.PasswordReset, Now.AddSeconds(1));
        // Mở lại token cũ (IssueTokenAsync đã thu hồi) để kiểm tra reset tự thu hồi các token đặt lại khác.
        await database.AdminExecuteAsync("UPDATE user_tokens SET revoked_at = NULL WHERE user_id = @u", ("@u", id));

        Assert.True(await Store.ResetPasswordAsync(hash, "HASH-moi", Now.AddMinutes(1), Ct));

        var row = await UserRowAsync(id);
        Assert.Equal("HASH-moi", row["password_hash"]);
        Assert.Equal(1L, Convert.ToInt64(row["session_version"]));
        Assert.Equal(Now.AddMinutes(1), row["email_verified_at"]);
        Assert.Equal([(false, true), (true, false)], (await TokensAsync(id)).Select(t => (t.Consumed, t.Revoked)));
        Assert.False(await Store.ResetPasswordAsync(hash, "HASH-lan-hai", Now.AddMinutes(2), Ct));
        Assert.False(await Store.ResetPasswordAsync(older, "HASH-lan-hai", Now.AddMinutes(2), Ct));
        Assert.Equal("HASH-moi", (await UserRowAsync(id))["password_hash"]);
    }

    [MySqlFact]
    public async Task Reset_does_not_reopen_disabled_or_demo_accounts()
    {
        foreach (var (status, demo) in new[] { ("disabled", false), ("active", true) })
        {
            var email = UniqueEmail();
            var id = await SeedUserAsync(email, status, demo);
            var (_, hash) = await IssueAsync(id, email, AccountTokenPurposes.PasswordReset);
            var before = await UserRowAsync(id);

            Assert.False(await Store.ResetPasswordAsync(hash, "HASH-moi", Now, Ct));
            Assert.Equal(before, await UserRowAsync(id));
            Assert.Equal(status, (await UserRowAsync(id))["status"]);
        }
    }

    [MySqlFact]
    public async Task Concurrent_uses_of_one_token_succeed_exactly_once()
    {
        var email = UniqueEmail();
        var id = await SeedUserAsync(email);
        var (_, verify) = await IssueAsync(id, email, AccountTokenPurposes.EmailVerification);
        var (_, reset) = await IssueAsync(id, email, AccountTokenPurposes.PasswordReset);

        var confirms = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => Store.ConfirmEmailAsync(verify, Now, Ct))));
        var resets = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Task.Run(() => Store.ResetPasswordAsync(reset, $"HASH-{i}", Now, Ct))));

        Assert.Equal(1, confirms.Count(ok => ok));
        Assert.Equal(1, resets.Count(ok => ok));
        var row = await UserRowAsync(id);
        Assert.Equal(1L, Convert.ToInt64(row["session_version"]));
        Assert.Equal($"HASH-{Array.IndexOf(resets, true)}", row["password_hash"]);
    }

    [MySqlFact]
    public async Task Change_password_requires_exact_current_hash_and_active_user_and_revokes_reset_tokens()
    {
        var email = UniqueEmail();
        var id = await SeedUserAsync(email, hash: "HashCu");
        var (_, reset) = await IssueAsync(id, email, AccountTokenPurposes.PasswordReset);

        Assert.Null(await Store.ChangePasswordAsync(id, "HASHCU", "HASH-moi", Now, Ct)); // so sánh nhị phân
        Assert.Null(await Store.ChangePasswordAsync(id, "khac", "HASH-moi", Now, Ct));
        Assert.Equal("HashCu", (await UserRowAsync(id))["password_hash"]);

        Assert.Equal((long?)1, await Store.ChangePasswordAsync(id, "HashCu", "HASH-moi", Now, Ct));
        Assert.Equal(1L, (await Store.FindCredentialsByIdAsync(id, Ct))!.Account.SessionVersion);
        Assert.Equal("HASH-moi", (await Store.FindCredentialsByIdAsync(id, Ct))!.PasswordHash);
        Assert.False(await Store.ResetPasswordAsync(reset, "HASH-ke-gian", Now, Ct));

        await database.AdminExecuteAsync("UPDATE users SET status = 'disabled' WHERE id = @id", ("@id", id));
        Assert.Null(await Store.ChangePasswordAsync(id, "HASH-moi", "HASH-3", Now, Ct));
    }

    [MySqlFact]
    public async Task Backend_account_cannot_delete_tokens()
    {
        await using var connection = await database.Factory.OpenConnectionAsync(Ct);
        await using var command = new MySqlCommand("DELETE FROM user_tokens WHERE id = -1", connection);

        var error = await Assert.ThrowsAsync<MySqlException>(() => command.ExecuteNonQueryAsync(Ct));
        Assert.Equal(MySqlErrorCode.TableAccessDenied, error.ErrorCode);
    }
}

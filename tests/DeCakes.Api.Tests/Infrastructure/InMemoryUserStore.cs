using DeCakes.Application.Accounts;
using DeCakes.Domain.Accounts;

namespace DeCakes.Api.Tests.Infrastructure;

/// <summary>
/// Store user và token tài khoản trong bộ nhớ, an toàn đa luồng. Email duy nhất không phân biệt hoa thường để mô phỏng
/// UNIQUE uq_users_email; kiểm tra trùng nằm trong khóa giống ràng buộc của database. Mỗi thao tác token/mật khẩu chạy
/// trọn trong một khóa, mô phỏng transaction có khóa dòng của store MySQL (không chứng minh SQL).
/// </summary>
public sealed class InMemoryUserStore : IUserAccountStore, IAccountSecurityStore
{
    private readonly object _gate = new();
    private readonly Dictionary<long, StoredUser> _users = [];
    private readonly List<StoredToken> _tokens = [];
    private long _nextId = 1;

    /// <summary>Mô phỏng sự cố database: mọi thao tác ném lỗi.</summary>
    public bool Fail { get; set; }

    public sealed record StoredUser(UserAccount Account, string? PasswordHash, bool IsDemo);

    public sealed record StoredToken(
        long Id, long UserId, string Purpose, byte[] Hash, string Email, DateTime CreatedAt, DateTime ExpiresAt,
        DateTime? ConsumedAt, DateTime? RevokedAt);

    public long Add(
        string email, string? hash, string role = SystemRoles.Customer, string status = UserStatuses.Active,
        bool emailVerified = true, bool isDemo = false)
    {
        lock (_gate)
        {
            var id = _nextId++;
            _users[id] = new StoredUser(
                new UserAccount(id, email, "Người dùng thử", null, role, status, emailVerified, 0, isDemo), hash, isDemo);
            return id;
        }
    }

    public StoredUser Get(long id)
    {
        lock (_gate) return _users[id];
    }

    public IReadOnlyList<StoredToken> Tokens(long userId)
    {
        lock (_gate) return _tokens.Where(t => t.UserId == userId).ToList();
    }

    public StoredUser? FindStored(string email)
    {
        lock (_gate) return _users.Values.FirstOrDefault(u => string.Equals(u.Account.Email, email, StringComparison.OrdinalIgnoreCase));
    }

    public void Update(long id, Func<UserAccount, UserAccount> change)
    {
        lock (_gate) _users[id] = _users[id] with { Account = change(_users[id].Account) };
    }

    public void SetHash(long id, string? hash)
    {
        lock (_gate) _users[id] = _users[id] with { PasswordHash = hash };
    }

    public void Remove(long id)
    {
        lock (_gate) _users.Remove(id);
    }

    public Task<UserCredentials?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        var user = FindStored(normalizedEmail);
        return Task.FromResult(user is null ? null : new UserCredentials(user.Account, user.PasswordHash));
    }

    public Task<UserAccount?> FindByIdAsync(long id, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        lock (_gate) return Task.FromResult(_users.TryGetValue(id, out var user) ? user.Account : null);
    }

    public async Task<long> CreateCustomerAsync(NewCustomer customer, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        // Nhường luồng để hai lần đăng ký đồng thời thực sự chen nhau trước khi tới ràng buộc.
        await Task.Yield();
        lock (_gate)
        {
            if (_users.Values.Any(u => string.Equals(u.Account.Email, customer.Email, StringComparison.OrdinalIgnoreCase)))
            {
                throw new DuplicateEmailException();
            }

            var id = _nextId++;
            _users[id] = new StoredUser(
                new UserAccount(id, customer.Email, customer.FullName, customer.Phone, SystemRoles.Customer, UserStatuses.Active),
                customer.PasswordHash,
                IsDemo: false);
            return id;
        }
    }

    public Task<bool> ReplacePasswordHashAsync(long id, string expectedCurrentHash, string newHash, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            if (!_users.TryGetValue(id, out var user) || !string.Equals(user.PasswordHash, expectedCurrentHash, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            _users[id] = user with { PasswordHash = newHash };
            return Task.FromResult(true);
        }
    }

    public Task<UserCredentials?> FindCredentialsByIdAsync(long userId, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            return Task.FromResult(_users.TryGetValue(userId, out var user) ? new UserCredentials(user.Account, user.PasswordHash) : null);
        }
    }

    public Task<TokenIssueStats> GetIssueStatsAsync(long userId, string purpose, DateTime since, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            var issued = _tokens.Where(t => t.UserId == userId && t.Purpose == purpose && t.CreatedAt >= since).ToList();
            return Task.FromResult(new TokenIssueStats(issued.Count, issued.Count == 0 ? null : issued.Max(t => t.CreatedAt)));
        }
    }

    public Task IssueTokenAsync(NewAccountToken token, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            RevokeOpen(token.UserId, token.Purpose, token.CreatedAt);
            _tokens.Add(new StoredToken(
                _tokens.Count + 1, token.UserId, token.Purpose, token.TokenHash, token.Email, token.CreatedAt, token.ExpiresAt, null, null));
            return Task.CompletedTask;
        }
    }

    public async Task<bool> ConfirmEmailAsync(byte[] tokenHash, DateTime now, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        await Task.Yield();
        lock (_gate)
        {
            if (!TryUsable(tokenHash, AccountTokenPurposes.EmailVerification, now, out var index, out var user)) return false;
            _tokens[index] = _tokens[index] with { ConsumedAt = now };
            _users[user.Account.Id] = user with { Account = user.Account with { EmailVerified = true } };
            return true;
        }
    }

    public async Task<bool> ResetPasswordAsync(byte[] tokenHash, string newPasswordHash, DateTime now, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        await Task.Yield();
        lock (_gate)
        {
            if (!TryUsable(tokenHash, AccountTokenPurposes.PasswordReset, now, out var index, out var user) || user.IsDemo) return false;
            _tokens[index] = _tokens[index] with { ConsumedAt = now };
            RevokeOpen(user.Account.Id, AccountTokenPurposes.PasswordReset, now);
            _users[user.Account.Id] = user with
            {
                PasswordHash = newPasswordHash,
                Account = user.Account with { EmailVerified = true, SessionVersion = user.Account.SessionVersion + 1 },
            };
            return true;
        }
    }

    public Task<long?> ChangePasswordAsync(
        long userId, string expectedCurrentHash, string newPasswordHash, DateTime now, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            if (!_users.TryGetValue(userId, out var user)
                || user.Account.Status != UserStatuses.Active
                || !string.Equals(user.PasswordHash, expectedCurrentHash, StringComparison.Ordinal))
            {
                return Task.FromResult<long?>(null);
            }

            var version = user.Account.SessionVersion + 1;
            _users[userId] = user with { PasswordHash = newPasswordHash, Account = user.Account with { SessionVersion = version } };
            RevokeOpen(userId, AccountTokenPurposes.PasswordReset, now);
            return Task.FromResult<long?>(version);
        }
    }

    private bool TryUsable(byte[] hash, string purpose, DateTime now, out int index, out StoredUser user)
    {
        user = null!;
        index = _tokens.FindIndex(t => t.Hash.AsSpan().SequenceEqual(hash));
        if (index < 0) return false;
        var token = _tokens[index];
        return token.Purpose == purpose && token.ConsumedAt is null && token.RevokedAt is null && token.ExpiresAt > now
            && _users.TryGetValue(token.UserId, out user!)
            && user.Account.Status == UserStatuses.Active
            && string.Equals(user.Account.Email, token.Email, StringComparison.Ordinal);
    }

    private void RevokeOpen(long userId, string purpose, DateTime now)
    {
        for (var i = 0; i < _tokens.Count; i++)
        {
            var t = _tokens[i];
            if (t.UserId == userId && t.Purpose == purpose && t.ConsumedAt is null && t.RevokedAt is null)
            {
                _tokens[i] = t with { RevokedAt = now };
            }
        }
    }

    private void ThrowIfFailing()
    {
        if (Fail) throw new InvalidOperationException("SIMULATED-DB-FAILURE host=db.internal user=secret-user");
    }
}

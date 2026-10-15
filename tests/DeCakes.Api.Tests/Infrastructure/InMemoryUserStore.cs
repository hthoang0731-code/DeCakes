using DeCakes.Application.Accounts;
using DeCakes.Domain.Accounts;

namespace DeCakes.Api.Tests.Infrastructure;

/// <summary>
/// Store user trong bộ nhớ, an toàn đa luồng. Email duy nhất không phân biệt hoa thường để mô phỏng
/// UNIQUE uq_users_email; kiểm tra trùng nằm trong khóa giống ràng buộc của database.
/// </summary>
public sealed class InMemoryUserStore : IUserAccountStore
{
    private readonly object _gate = new();
    private readonly Dictionary<long, StoredUser> _users = [];
    private long _nextId = 1;

    /// <summary>Mô phỏng sự cố database: mọi thao tác ném lỗi.</summary>
    public bool Fail { get; set; }

    public sealed record StoredUser(UserAccount Account, string? PasswordHash, bool IsDemo);

    public long Add(string email, string? hash, string role = SystemRoles.Customer, string status = UserStatuses.Active)
    {
        lock (_gate)
        {
            var id = _nextId++;
            _users[id] = new StoredUser(new UserAccount(id, email, "Người dùng thử", null, role, status), hash, false);
            return id;
        }
    }

    public StoredUser Get(long id)
    {
        lock (_gate) return _users[id];
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

    private void ThrowIfFailing()
    {
        if (Fail) throw new InvalidOperationException("SIMULATED-DB-FAILURE host=db.internal user=secret-user");
    }
}

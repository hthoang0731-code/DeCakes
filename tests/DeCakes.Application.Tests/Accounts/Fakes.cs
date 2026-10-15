using DeCakes.Application.Accounts;
using DeCakes.Domain.Accounts;

namespace DeCakes.Application.Tests.Accounts;

/// <summary>Store trong bộ nhớ; email duy nhất không phân biệt hoa thường, giống UNIQUE của database.</summary>
internal sealed class FakeUserStore : IUserAccountStore
{
    private readonly List<(UserAccount Account, string? Hash)> _users = [];

    public int FindByEmailCalls { get; private set; }

    public List<(long Id, string Expected, string New)> HashReplacements { get; } = [];

    public bool ThrowDuplicateOnCreate { get; set; }

    public NewCustomer? LastCreated { get; private set; }

    public long Add(string email, string? hash, string role = SystemRoles.Customer, string status = UserStatuses.Active)
    {
        var id = _users.Count + 1;
        _users.Add((new UserAccount(id, email, "Người dùng", null, role, status), hash));
        return id;
    }

    public string? HashOf(long id) => _users.Single(u => u.Account.Id == id).Hash;

    public Task<UserCredentials?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken)
    {
        FindByEmailCalls++;
        var match = _users.FirstOrDefault(u => string.Equals(u.Account.Email, normalizedEmail, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(match.Account is null ? null : new UserCredentials(match.Account, match.Hash));
    }

    public Task<UserAccount?> FindByIdAsync(long id, CancellationToken cancellationToken) =>
        Task.FromResult<UserAccount?>(_users.FirstOrDefault(u => u.Account.Id == id).Account);

    public Task<long> CreateCustomerAsync(NewCustomer customer, CancellationToken cancellationToken)
    {
        if (ThrowDuplicateOnCreate
            || _users.Any(u => string.Equals(u.Account.Email, customer.Email, StringComparison.OrdinalIgnoreCase)))
        {
            throw new DuplicateEmailException();
        }

        LastCreated = customer;
        var id = _users.Count + 1;
        _users.Add((new UserAccount(id, customer.Email, customer.FullName, customer.Phone, SystemRoles.Customer, UserStatuses.Active), customer.PasswordHash));
        return Task.FromResult((long)id);
    }

    public Task<bool> ReplacePasswordHashAsync(long id, string expectedCurrentHash, string newHash, CancellationToken cancellationToken)
    {
        HashReplacements.Add((id, expectedCurrentHash, newHash));
        var index = _users.FindIndex(u => u.Account.Id == id && u.Hash == expectedCurrentHash);
        if (index < 0) return Task.FromResult(false);
        _users[index] = (_users[index].Account, newHash);
        return Task.FromResult(true);
    }
}

/// <summary>Hasher giả: hash = "H(" + mật khẩu + ")". Ghi lại mọi lần xác minh.</summary>
internal sealed class FakeHasher : IPasswordHashService
{
    public List<string> HashedPasswords { get; } = [];

    public List<(string? Hash, string Password)> Verifications { get; } = [];

    public bool RehashNeeded { get; set; }

    public string Hash(string password)
    {
        HashedPasswords.Add(password);
        return $"H({password})";
    }

    public PasswordCheck Verify(string? hashedPassword, string password)
    {
        Verifications.Add((hashedPassword, password));
        if (hashedPassword is null || hashedPassword != $"H({password})") return PasswordCheck.Failed;
        return RehashNeeded ? PasswordCheck.SuccessRehashNeeded : PasswordCheck.Success;
    }
}

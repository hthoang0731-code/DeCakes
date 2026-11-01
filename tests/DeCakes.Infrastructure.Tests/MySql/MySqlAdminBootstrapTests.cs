using DeCakes.AdminBootstrap;
using DeCakes.Application.Accounts;
using DeCakes.Application.Admin;
using DeCakes.Infrastructure.Accounts;
using DeCakes.Infrastructure.Admin;

namespace DeCakes.Infrastructure.Tests.MySql;

public sealed class MySqlAdminBootstrapTests : IAsyncLifetime
{
    // Provisioning tests retain the source backend grants, separate from the SELECT-only read fixture.
    private readonly MySqlTestDatabase _database = new();
    public Task InitializeAsync() => _database.InitializeAsync();
    public Task DisposeAsync() => _database.DisposeAsync();

    [MySqlFact]
    public async Task Console_creates_only_active_real_admin_with_verifiable_hash_and_no_secret_output()
    {
        const string password = "  Local-admin-password!  ";
        var prompt = new Prompt(" ADMIN@EXAMPLE.TEST ", password);
        var hasher = new AspNetPasswordHashService();
        var result = await BootstrapConsole.RunAsync([], prompt,
            () => new AdminBootstrapService(new MySqlAdminProvisioningStore(_database.Factory), hasher));
        Assert.Equal(0, result);
        var user = await new MySqlUserAccountStore(_database.Factory).FindByEmailAsync("admin@example.test", CancellationToken.None);
        Assert.NotNull(user);
        Assert.Equal("admin", user.Account.Role);
        Assert.Equal("active", user.Account.Status);
        Assert.Equal(0L, Convert.ToInt64(await _database.AdminScalarAsync("SELECT is_demo FROM users WHERE id=@id", ("@id", user.Account.Id))));
        Assert.Equal(PasswordCheck.Success, hasher.Verify(user.PasswordHash, password));
        Assert.Equal(PasswordCheck.Failed, hasher.Verify(user.PasswordHash, password.Trim()));
        Assert.DoesNotContain(password, prompt.Output);
        Assert.DoesNotContain(user.PasswordHash!, prompt.Output);
    }

    [MySqlFact]
    public async Task Existing_customer_bakery_and_admin_are_never_changed_even_with_collation_duplicates()
    {
        var store = new MySqlAdminProvisioningStore(_database.Factory);
        foreach (var role in new[] { "customer", "bakery", "admin" })
        {
            var id = await _database.AdminExecuteAsync("""
                INSERT INTO users (email,full_name,password_hash,system_role,status,is_demo)
                VALUES (@e,'Original',NULL,@role,'disabled',1)
                """, ("@e", role + "@example.test"), ("@role", role));
            var prompt = new Prompt(role.ToUpperInvariant() + "@EXAMPLE.TEST", "Replacement-password!");
            Assert.Equal(1, await BootstrapConsole.RunAsync([], prompt, () => new AdminBootstrapService(store, new AspNetPasswordHashService())));
            var user = await new MySqlUserAccountStore(_database.Factory).FindByIdAsync(id, CancellationToken.None);
            Assert.Equal(role, user!.Role);
            Assert.Equal("disabled", user.Status);
            Assert.Equal("Original", user.FullName);
            Assert.Null(await _database.AdminScalarAsync("SELECT password_hash FROM users WHERE id=@id", ("@id", id)));
            Assert.Equal(1L, Convert.ToInt64(await _database.AdminScalarAsync("SELECT is_demo FROM users WHERE id=@id", ("@id", id))));
        }
        Assert.Equal(3L, Convert.ToInt64(await _database.AdminScalarAsync("SELECT COUNT(*) FROM users")));
    }

    [MySqlFact]
    public async Task Concurrent_duplicate_insert_creates_exactly_one_account()
    {
        var store = new MySqlAdminProvisioningStore(_database.Factory);
        var service = new AdminBootstrapService(store, new AspNetPasswordHashService());
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => service.CreateAsync("race@example.test", "Admin", "Temporary-password!", CancellationToken.None)));
        Assert.Single(results, result => result == AdminBootstrapOutcome.Created);
        Assert.Equal(3, results.Count(result => result == AdminBootstrapOutcome.EmailTaken));
        Assert.Equal(1L, Convert.ToInt64(await _database.AdminScalarAsync("SELECT COUNT(*) FROM users")));
    }

    private sealed class Prompt(string email, string password) : IOperatorPrompt
    {
        private int _reads;
        public bool IsInteractive => true;
        public string Output = "";
        public string Read(string prompt) => ++_reads == 1 ? email : "Admin local";
        public string ReadHidden(string prompt) => password;
        public void Write(string message) => Output += message;
    }
}

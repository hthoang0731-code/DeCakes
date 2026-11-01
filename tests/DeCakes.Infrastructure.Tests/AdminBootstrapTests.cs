using DeCakes.AdminBootstrap;
using DeCakes.Application.Accounts;
using DeCakes.Application.Admin;
using DeCakes.Infrastructure.Accounts;

namespace DeCakes.Infrastructure.Tests;

public sealed class AdminBootstrapTests
{
    private const string Password = "  Untrimmed-password!  ";

    [Fact]
    public async Task Uses_standard_hasher_and_normalization_without_password_trimming()
    {
        var store = new Store();
        var hasher = new AspNetPasswordHashService();
        Assert.Equal(AdminBootstrapOutcome.Created, await new AdminBootstrapService(store, hasher).CreateAsync(
            " NEW@EXAMPLE.TEST ", " Quản trị ", Password, CancellationToken.None));
        Assert.Equal("new@example.test", store.Email);
        Assert.Equal("Quản trị", store.Name);
        Assert.Equal(PasswordCheck.Success, hasher.Verify(store.Hash, Password));
        Assert.Equal(PasswordCheck.Failed, hasher.Verify(store.Hash, Password.Trim()));
    }

    [Fact]
    public async Task Duplicate_email_stops_and_invalid_inputs_do_not_write()
    {
        var store = new Store { Duplicate = true };
        var service = new AdminBootstrapService(store, new AspNetPasswordHashService());
        Assert.Equal(AdminBootstrapOutcome.EmailTaken, await service.CreateAsync("old@example.test", "Admin", Password, CancellationToken.None));
        Assert.Equal(1, store.Calls);
        foreach (var input in new (string?, string?, string?)[] {
            (null, "Admin", Password), ("bad", "Admin", Password), ("a@b.test", "", Password),
            ("a@b.test", "Admin", "short"), ("a@b.test", "Admin", new string('x', 129)),
            ("a@b.test", new string('x', 151), Password), ("a@b.test", "bad\nname", Password) })
            Assert.Equal(AdminBootstrapOutcome.InvalidInput, await service.CreateAsync(input.Item1, input.Item2, input.Item3, CancellationToken.None));
        Assert.Equal(1, store.Calls);
    }

    [Fact]
    public async Task Console_hides_password_and_error_details_and_refuses_args_or_redirection()
    {
        var store = new Store();
        var prompt = new Prompt();
        AdminBootstrapService Service() => new(store, new AspNetPasswordHashService());
        Assert.Equal(0, await BootstrapConsole.RunAsync([], prompt, Service));
        Assert.Equal(2, prompt.HiddenReads);
        Assert.DoesNotContain(Password, prompt.Output);
        Assert.DoesNotContain(store.Hash!, prompt.Output);
        var args = new[] { "secret-argument" };
        Assert.Equal(1, await BootstrapConsole.RunAsync(args, prompt, Service));
        Assert.DoesNotContain(args[0], prompt.Output);
        prompt.Interactive = false;
        Assert.Equal(1, await BootstrapConsole.RunAsync([], prompt, Service));
        Assert.Equal(1, store.Calls);
        prompt.Interactive = true;
        Assert.Equal(1, await BootstrapConsole.RunAsync([], prompt, () => throw new InvalidOperationException("PASSWORD-SECRET-CONNECTION")));
        Assert.DoesNotContain("PASSWORD-SECRET-CONNECTION", prompt.Output);
    }

    [Fact]
    public async Task Confirmation_mismatch_does_not_create_service()
    {
        var prompt = new Prompt { Confirmation = "mismatch" };
        Assert.Equal(1, await BootstrapConsole.RunAsync([], prompt, () => throw new Exception("Must not be called")));
    }

    private sealed class Store : IAdminProvisioningStore
    {
        public string? Email, Name, Hash;
        public int Calls;
        public bool Duplicate;
        public Task<long> CreateAdminAsync(string email, string fullName, string passwordHash, CancellationToken cancellationToken)
        {
            Calls++;
            if (Duplicate) throw new DuplicateEmailException();
            Email = email; Name = fullName; Hash = passwordHash;
            return Task.FromResult(1L);
        }
    }

    private sealed class Prompt : IOperatorPrompt
    {
        private int _reads;
        public int HiddenReads;
        public bool Interactive = true;
        public string Confirmation = Password;
        public bool IsInteractive => Interactive;
        public string Output = "";
        public string Read(string prompt) => ++_reads % 2 == 1 ? "new@example.test" : "Admin";
        public string ReadHidden(string prompt) => ++HiddenReads % 2 == 1 ? Password : Confirmation;
        public void Write(string message) => Output += message;
    }
}

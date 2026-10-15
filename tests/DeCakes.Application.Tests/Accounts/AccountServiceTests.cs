using DeCakes.Application.Accounts;
using DeCakes.Domain.Accounts;

namespace DeCakes.Application.Tests.Accounts;

public class AccountServiceTests
{
    private const string ValidPassword = "mat-khau-du-dai-1";

    private readonly FakeUserStore _store = new();
    private readonly FakeHasher _hasher = new();
    private readonly AccountService _service;

    public AccountServiceTests() => _service = new AccountService(_store, _hasher);

    private static CancellationToken Ct => CancellationToken.None;

    // ---------- Đăng ký ----------

    [Fact]
    public async Task Register_valid_creates_customer_with_normalized_email_and_untrimmed_password()
    {
        const string password = "  co khoang trang  ";

        var result = await _service.RegisterAsync(new RegisterRequest("  Alice@Example.COM ", password, "  Nguyễn Văn A ", " 0901 234 567 "), Ct);

        var created = Assert.IsType<RegisterResult.Created>(result);
        Assert.Equal("alice@example.com", created.User.Email);
        Assert.Equal("Nguyễn Văn A", created.User.FullName);
        Assert.Equal("0901 234 567", created.User.Phone);
        Assert.Equal(SystemRoles.Customer, created.User.Role);
        Assert.Equal([password], _hasher.HashedPasswords);
        Assert.Equal($"H({password})", _store.LastCreated!.PasswordHash);
    }

    [Fact]
    public async Task Register_without_phone_stores_null()
    {
        var result = await _service.RegisterAsync(new RegisterRequest("a@example.com", ValidPassword, "A", "   "), Ct);

        Assert.Null(Assert.IsType<RegisterResult.Created>(result).User.Phone);
    }

    public static TheoryData<string?, string?, string?, string?, string> InvalidRegistrations()
    {
        var ok = ValidPassword;
        return new()
        {
            { null, ok, "A", null, "email" },
            { "khong-co-at", ok, "A", null, "email" },
            { "a@", ok, "A", null, "email" },
            { "@example.com", ok, "A", null, "email" },
            { "a b@example.com", ok, "A", null, "email" },
            { "Tên <a@example.com>", ok, "A", null, "email" },
            { new string('a', 244) + "@example.com", ok, "A", null, "email" }, // 256 ký tự
            { "a@example.com", null, "A", null, "password" },
            { "a@example.com", new string('x', 11), "A", null, "password" },
            { "a@example.com", new string('x', 129), "A", null, "password" },
            { "a@example.com", ok, null, null, "fullName" },
            { "a@example.com", ok, "   ", null, "fullName" },
            { "a@example.com", ok, new string('n', 151), null, "fullName" },
            { "a@example.com", ok, "A", new string('1', 21), "phone" },
            { "a@example.com", ok, "A", "abc", "phone" },
        };
    }

    [Theory]
    [MemberData(nameof(InvalidRegistrations))]
    public async Task Register_rejects_invalid_input(string? email, string? password, string? fullName, string? phone, string field)
    {
        var result = await _service.RegisterAsync(new RegisterRequest(email, password, fullName, phone), Ct);

        var failed = Assert.IsType<RegisterResult.ValidationFailed>(result);
        Assert.Contains(field, failed.Errors.Keys);
        Assert.Null(_store.LastCreated);
        Assert.Empty(_hasher.HashedPasswords);
    }

    [Theory]
    [InlineData(12)]
    [InlineData(128)]
    public async Task Register_accepts_password_length_boundaries(int length)
    {
        var result = await _service.RegisterAsync(new RegisterRequest("a@example.com", new string('x', length), "A", null), Ct);

        Assert.IsType<RegisterResult.Created>(result);
    }

    [Fact]
    public async Task Register_accepts_email_of_exactly_255_characters()
    {
        var email = new string('a', 243) + "@example.com";
        Assert.Equal(255, email.Length);

        Assert.IsType<RegisterResult.Created>(await _service.RegisterAsync(new RegisterRequest(email, ValidPassword, "A", null), Ct));
    }

    [Fact]
    public async Task Register_duplicate_email_reported_by_store_returns_email_taken()
    {
        _store.ThrowDuplicateOnCreate = true;

        var result = await _service.RegisterAsync(new RegisterRequest("a@example.com", ValidPassword, "A", null), Ct);

        Assert.IsType<RegisterResult.EmailTaken>(result);
    }

    [Fact]
    public async Task Register_same_email_with_different_case_is_taken()
    {
        await _service.RegisterAsync(new RegisterRequest("a@example.com", ValidPassword, "A", null), Ct);

        var result = await _service.RegisterAsync(new RegisterRequest(" A@EXAMPLE.com", ValidPassword, "B", null), Ct);

        Assert.IsType<RegisterResult.EmailTaken>(result);
    }

    // ---------- Đăng nhập ----------

    [Fact]
    public async Task Login_success_returns_user_without_rehash()
    {
        var id = _store.Add("a@example.com", $"H({ValidPassword})");

        var result = await _service.LoginAsync(new LoginRequest(" A@Example.com ", ValidPassword), Ct);

        Assert.Equal(id, Assert.IsType<LoginResult.Succeeded>(result).User.Id);
        Assert.Empty(_store.HashReplacements);
    }

    [Fact]
    public async Task Login_unknown_user_still_runs_password_verification()
    {
        var result = await _service.LoginAsync(new LoginRequest("khong-ton-tai@example.com", ValidPassword), Ct);

        Assert.IsType<LoginResult.InvalidCredentials>(result);
        Assert.Equal([(null, ValidPassword)], _hasher.Verifications);
    }

    [Fact]
    public async Task Login_user_without_hash_fails_after_verification()
    {
        _store.Add("demo@example.com", hash: null);

        var result = await _service.LoginAsync(new LoginRequest("demo@example.com", ValidPassword), Ct);

        Assert.IsType<LoginResult.InvalidCredentials>(result);
        Assert.Single(_hasher.Verifications);
    }

    [Fact]
    public async Task Login_wrong_password_fails()
    {
        _store.Add("a@example.com", $"H({ValidPassword})");

        Assert.IsType<LoginResult.InvalidCredentials>(await _service.LoginAsync(new LoginRequest("a@example.com", ValidPassword + "x"), Ct));
    }

    [Fact]
    public async Task Login_does_not_trim_password()
    {
        _store.Add("a@example.com", $"H({ValidPassword})");

        Assert.IsType<LoginResult.InvalidCredentials>(await _service.LoginAsync(new LoginRequest("a@example.com", " " + ValidPassword), Ct));
    }

    [Fact]
    public async Task Login_disabled_user_with_correct_password_fails()
    {
        _store.Add("a@example.com", $"H({ValidPassword})", status: UserStatuses.Disabled);

        Assert.IsType<LoginResult.InvalidCredentials>(await _service.LoginAsync(new LoginRequest("a@example.com", ValidPassword), Ct));
    }

    [Fact]
    public async Task Login_with_rehash_needed_replaces_hash_conditionally()
    {
        var id = _store.Add("a@example.com", $"H({ValidPassword})");
        _hasher.RehashNeeded = true;

        var result = await _service.LoginAsync(new LoginRequest("a@example.com", ValidPassword), Ct);

        Assert.IsType<LoginResult.Succeeded>(result);
        var replacement = Assert.Single(_store.HashReplacements);
        Assert.Equal((id, $"H({ValidPassword})", $"H({ValidPassword})"), replacement);
    }

    [Fact]
    public async Task Login_password_longer_than_limit_fails_without_lookup()
    {
        _store.Add("a@example.com", $"H({ValidPassword})");

        var result = await _service.LoginAsync(new LoginRequest("a@example.com", new string('x', 129)), Ct);

        Assert.IsType<LoginResult.InvalidCredentials>(result);
        Assert.Equal(0, _store.FindByEmailCalls);
    }

    [Theory]
    [InlineData(null, ValidPassword)]
    [InlineData("  ", ValidPassword)]
    [InlineData("a@example.com", null)]
    [InlineData("a@example.com", "")]
    public async Task Login_missing_fields_is_validation_error(string? email, string? password)
    {
        Assert.IsType<LoginResult.ValidationFailed>(await _service.LoginAsync(new LoginRequest(email, password), Ct));
    }

    // ---------- Kiểm tra user theo id ----------

    [Fact]
    public async Task GetActiveUser_returns_null_for_disabled_or_missing_user()
    {
        var active = _store.Add("a@example.com", "h");
        var disabled = _store.Add("b@example.com", "h", status: UserStatuses.Disabled);

        Assert.NotNull(await _service.GetActiveUserAsync(active, Ct));
        Assert.Null(await _service.GetActiveUserAsync(disabled, Ct));
        Assert.Null(await _service.GetActiveUserAsync(999, Ct));
    }
}

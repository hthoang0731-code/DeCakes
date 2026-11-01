using DeCakes.Application.Accounts;
using DeCakes.Domain.Accounts;

namespace DeCakes.Application.Tests.Accounts;

public class AccountTokensTests
{
    [Fact]
    public void Created_token_round_trips_to_the_same_digest_and_tokens_are_unique()
    {
        var (token, hash) = AccountTokens.Create();
        var (other, otherHash) = AccountTokens.Create();

        Assert.Equal(AccountTokens.TokenLength, token.Length);
        Assert.True(AccountTokens.TryHash(token, out var again));
        Assert.Equal(hash, again);
        Assert.Equal(32, hash.Length);
        Assert.NotEqual(token, other);
        Assert.NotEqual(hash, otherHash);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ngan")]
    [InlineData("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA ")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Malformed_tokens_are_rejected_without_throwing(string? token)
    {
        Assert.False(AccountTokens.TryHash(token, out _));
    }
}

public class AccountSecurityServiceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string Password = "mat-khau-du-dai-1";

    private readonly FakeUserStore _users = new();
    private readonly FakeSecurityStore _store;
    private readonly FakeHasher _hasher = new();
    private readonly FakeEmailSender _email = new();
    private readonly FixedTime _time = new(new DateTimeOffset(2026, 10, 22, 1, 0, 0, TimeSpan.Zero));
    private readonly AccountSecurityService _service;

    public AccountSecurityServiceTests()
    {
        _store = new FakeSecurityStore(_users);
        _service = new AccountSecurityService(_users, _store, _hasher, _email, _time);
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    [Fact]
    public async Task Forgot_password_issues_reset_token_with_server_side_lifetime_and_sends_raw_token_only_by_email()
    {
        var id = _users.Add("a@example.com", "H(x)");

        var result = await _service.ForgotPasswordAsync(new(" A@Example.com "), Ct);

        Assert.Equal(new ForgotPasswordResult.Accepted(EmailDispatchOutcome.Sent), result);
        var issued = Assert.Single(_store.Issued);
        Assert.Equal((id, AccountTokenPurposes.PasswordReset, "a@example.com"), (issued.UserId, issued.Purpose, issued.Email));
        Assert.Equal(Now + AccountSecurityService.PasswordResetLifetime, issued.ExpiresAt);
        var sent = Assert.Single(_email.Sent);
        Assert.True(AccountTokens.TryHash(sent.Token, out var digest));
        Assert.Equal(issued.TokenHash, digest);
    }

    public static TheoryData<string> IneligibleAccounts() => ["unknown", "disabled", "demo", "unknown-role"];

    [Theory]
    [MemberData(nameof(IneligibleAccounts))]
    public async Task Forgot_password_does_not_issue_for_ineligible_accounts(string scenario)
    {
        switch (scenario)
        {
            case "disabled": _users.Add("a@example.com", "H(x)", status: UserStatuses.Disabled); break;
            case "demo": _users.AddAccount(new UserAccount(0, "a@example.com", "Demo", null, SystemRoles.Customer, UserStatuses.Active, IsDemo: true)); break;
            case "unknown-role": _users.Add("a@example.com", "H(x)", role: "owner"); break;
        }

        var result = await _service.ForgotPasswordAsync(new("a@example.com"), Ct);

        Assert.Equal(new ForgotPasswordResult.Accepted(EmailDispatchOutcome.NotEligible), result);
        Assert.Empty(_store.Issued);
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task Forgot_password_rejects_invalid_email_before_lookup()
    {
        var result = await _service.ForgotPasswordAsync(new("khong hop le"), Ct);

        Assert.IsType<ForgotPasswordResult.ValidationFailed>(result);
        Assert.Equal(0, _users.FindByEmailCalls);
    }

    [Theory]
    [InlineData(59, 1, EmailDispatchOutcome.Throttled)]
    [InlineData(61, 1, EmailDispatchOutcome.Sent)]
    [InlineData(3600, AccountSecurityService.MaxIssuesPerWindow, EmailDispatchOutcome.Throttled)]
    [InlineData(3600, AccountSecurityService.MaxIssuesPerWindow - 1, EmailDispatchOutcome.Sent)]
    public async Task Issuing_is_throttled_by_cooldown_and_hourly_cap(int secondsSinceLast, int issuedInWindow, EmailDispatchOutcome expected)
    {
        _users.Add("a@example.com", "H(x)");
        _store.Stats = new TokenIssueStats(issuedInWindow, Now - TimeSpan.FromSeconds(secondsSinceLast));

        var result = await _service.ForgotPasswordAsync(new("a@example.com"), Ct);

        Assert.Equal(new ForgotPasswordResult.Accepted(expected), result);
        Assert.Equal(Now - AccountSecurityService.IssueWindow, _store.StatsSince);
        Assert.Equal(expected == EmailDispatchOutcome.Sent ? 1 : 0, _store.Issued.Count);
    }

    [Fact]
    public async Task Verification_reports_delivery_failure_after_issuing_and_skips_verified_users()
    {
        var unverified = _users.Add("a@example.com", "H(x)");
        var verified = _users.AddAccount(new UserAccount(0, "b@example.com", "B", null, SystemRoles.Customer, UserStatuses.Active, EmailVerified: true));
        _email.Fail = true;

        Assert.Equal(EmailDispatchOutcome.DeliveryFailed, await _service.SendEmailVerificationAsync(unverified, Ct));
        Assert.Equal(AccountTokenPurposes.EmailVerification, Assert.Single(_store.Issued).Purpose);
        Assert.Equal(EmailDispatchOutcome.AlreadyVerified, await _service.SendEmailVerificationAsync(verified, Ct));
        Assert.Equal(EmailDispatchOutcome.NotEligible, await _service.SendEmailVerificationAsync(999, Ct));
        Assert.Single(_store.Issued);
    }

    [Fact]
    public async Task Reset_validates_password_and_token_format_before_touching_the_store()
    {
        var (token, hash) = AccountTokens.Create();

        Assert.IsType<ResetPasswordResult.ValidationFailed>(await _service.ResetPasswordAsync(new(token, "ngan"), Ct));
        Assert.IsType<ResetPasswordResult.InvalidToken>(await _service.ResetPasswordAsync(new("sai", Password), Ct));
        Assert.Empty(_store.ResetCalls);

        _store.ResetSucceeds = true;
        Assert.IsType<ResetPasswordResult.Succeeded>(await _service.ResetPasswordAsync(new(token, Password), Ct));
        var call = Assert.Single(_store.ResetCalls);
        Assert.Equal(hash, call.Hash);
        Assert.Equal(($"H({Password})", Now), (call.NewHash, call.Now));
    }

    [Fact]
    public async Task Reset_reports_invalid_token_when_store_rejects()
    {
        var (token, _) = AccountTokens.Create();

        Assert.IsType<ResetPasswordResult.InvalidToken>(await _service.ResetPasswordAsync(new(token, Password), Ct));
    }

    [Fact]
    public async Task Change_password_verifies_current_password_and_passes_expected_hash()
    {
        var id = _users.Add("a@example.com", "H(cu-du-dai-12345)");
        _store.NextVersion = 4;

        var wrong = await _service.ChangePasswordAsync(id, new("sai-mat-khau-12345", Password), Ct);
        Assert.IsType<ChangePasswordResult.CurrentPasswordInvalid>(wrong);
        Assert.Empty(_store.ChangeCalls);

        var ok = await _service.ChangePasswordAsync(id, new("cu-du-dai-12345", Password), Ct);
        var succeeded = Assert.IsType<ChangePasswordResult.Succeeded>(ok);
        Assert.Equal(4, succeeded.SessionVersion);
        Assert.Equal((id, "H(cu-du-dai-12345)", $"H({Password})"), Assert.Single(_store.ChangeCalls));
    }

    [Fact]
    public async Task Change_password_reports_conflict_when_hash_changed_concurrently()
    {
        var id = _users.Add("a@example.com", "H(cu-du-dai-12345)");
        _store.NextVersion = null;

        Assert.IsType<ChangePasswordResult.Conflict>(await _service.ChangePasswordAsync(id, new("cu-du-dai-12345", Password), Ct));
    }

    [Theory]
    [InlineData(null, Password, "currentPassword")]
    [InlineData("cu-du-dai-12345", "ngan", "newPassword")]
    [InlineData(Password, Password, "newPassword")]
    public async Task Change_password_validates_input_before_verifying(string? current, string? next, string field)
    {
        var result = await _service.ChangePasswordAsync(1, new(current, next), Ct);

        Assert.Contains(field, Assert.IsType<ChangePasswordResult.ValidationFailed>(result).Errors.Keys);
        Assert.Empty(_hasher.Verifications);
    }

    private sealed class FakeSecurityStore(FakeUserStore users) : IAccountSecurityStore
    {
        public List<NewAccountToken> Issued { get; } = [];
        public List<(byte[] Hash, string NewHash, DateTime Now)> ResetCalls { get; } = [];
        public List<(long UserId, string Expected, string New)> ChangeCalls { get; } = [];
        public TokenIssueStats Stats { get; set; } = new(0, null);
        public DateTime? StatsSince { get; private set; }
        public bool ResetSucceeds { get; set; }
        public long? NextVersion { get; set; } = 1;

        public async Task<UserCredentials?> FindCredentialsByIdAsync(long userId, CancellationToken cancellationToken) =>
            await users.FindByIdAsync(userId, cancellationToken) is { } account ? new UserCredentials(account, users.HashOf(userId)) : null;

        public Task<TokenIssueStats> GetIssueStatsAsync(long userId, string purpose, DateTime since, CancellationToken cancellationToken)
        {
            StatsSince = since;
            return Task.FromResult(Stats);
        }

        public Task IssueTokenAsync(NewAccountToken token, CancellationToken cancellationToken)
        {
            Issued.Add(token);
            return Task.CompletedTask;
        }

        public Task<bool> ConfirmEmailAsync(byte[] tokenHash, DateTime now, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> ResetPasswordAsync(byte[] tokenHash, string newPasswordHash, DateTime now, CancellationToken cancellationToken)
        {
            ResetCalls.Add((tokenHash, newPasswordHash, now));
            return Task.FromResult(ResetSucceeds);
        }

        public Task<long?> ChangePasswordAsync(long userId, string expectedCurrentHash, string newPasswordHash, DateTime now, CancellationToken cancellationToken)
        {
            ChangeCalls.Add((userId, expectedCurrentHash, newPasswordHash));
            return Task.FromResult(NextVersion);
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeEmailSender : IAccountEmailSender
    {
        public List<(string Kind, string To, string Token)> Sent { get; } = [];
        public bool Fail { get; set; }

        public Task SendEmailVerificationAsync(string toEmail, string fullName, string token, DateTime expiresAt, CancellationToken cancellationToken) =>
            Record("verify", toEmail, token);

        public Task SendPasswordResetAsync(string toEmail, string fullName, string token, DateTime expiresAt, CancellationToken cancellationToken) =>
            Record("reset", toEmail, token);

        public Task SendStaffInvitationAsync(string toEmail, string bakeryName, string token, DateTime expiresAt, CancellationToken cancellationToken) =>
            Record("invite", toEmail, token);

        private Task Record(string kind, string to, string token)
        {
            if (Fail) throw new EmailDeliveryException("Chưa cấu hình gửi email.");
            Sent.Add((kind, to, token));
            return Task.CompletedTask;
        }
    }
}

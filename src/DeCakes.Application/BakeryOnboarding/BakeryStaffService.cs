using DeCakes.Application.Accounts;

namespace DeCakes.Application.BakeryOnboarding;

/// <summary>
/// Owner mời/gỡ staff của chính tiệm; người được mời nhận lời mời bằng tài khoản bakery đúng email hoặc tạo tài khoản
/// mới với mật khẩu tự đặt. Owner không bao giờ biết mật khẩu của staff. Staff không quản lý thành viên.
/// </summary>
public interface IBakeryStaffService
{
    Task<(OwnerActionOutcome Outcome, IReadOnlyList<BakeryMemberDto> Members)> ListMembersAsync(
        long ownerUserId, long bakeryId, CancellationToken cancellationToken);

    Task<RemoveMemberOutcome> RemoveStaffAsync(long ownerUserId, long bakeryId, long memberUserId, CancellationToken cancellationToken);

    Task<InviteStaffResult> InviteAsync(long ownerUserId, long bakeryId, InviteStaffRequest request, CancellationToken cancellationToken);

    Task<(OwnerActionOutcome Outcome, IReadOnlyList<BakeryInvitationDto> Invitations)> ListInvitationsAsync(
        long ownerUserId, long bakeryId, CancellationToken cancellationToken);

    Task<RevokeInvitationOutcome> RevokeInvitationAsync(long ownerUserId, long bakeryId, long invitationId, CancellationToken cancellationToken);

    Task<AcceptInvitationResult> AcceptAsync(long userId, AcceptInvitationRequest request, CancellationToken cancellationToken);

    Task<AcceptNewAccountResult> AcceptWithNewAccountAsync(AcceptInvitationNewAccountRequest request, CancellationToken cancellationToken);
}

/// <summary><c>Errors</c> khác null: dữ liệu không hợp lệ. <c>EmailDelivered</c> false: lời mời đã tạo nhưng email chưa gửi được.</summary>
public sealed record InviteStaffResult(
    CreateInvitationStatus Status,
    BakeryInvitationDto? Invitation = null,
    bool EmailDelivered = false,
    IReadOnlyDictionary<string, string[]>? Errors = null);

public sealed record AcceptNewAccountResult(AcceptInvitationResult Result, IReadOnlyDictionary<string, string[]>? Errors = null);

public sealed class BakeryStaffService(
    IBakeryOnboardingStore store,
    IPasswordHashService passwordHasher,
    IAccountEmailSender emailSender,
    TimeProvider time) : IBakeryStaffService
{
    public static readonly TimeSpan InvitationLifetime = TimeSpan.FromDays(7);

    public Task<(OwnerActionOutcome Outcome, IReadOnlyList<BakeryMemberDto> Members)> ListMembersAsync(
        long ownerUserId, long bakeryId, CancellationToken cancellationToken) =>
        store.ListMembersAsync(ownerUserId, bakeryId, cancellationToken);

    public Task<RemoveMemberOutcome> RemoveStaffAsync(
        long ownerUserId, long bakeryId, long memberUserId, CancellationToken cancellationToken) =>
        store.RemoveStaffAsync(ownerUserId, bakeryId, memberUserId, Now(), cancellationToken);

    public async Task<InviteStaffResult> InviteAsync(
        long ownerUserId, long bakeryId, InviteStaffRequest request, CancellationToken cancellationToken)
    {
        var email = AccountService.NormalizeEmail(request.Email);
        if (email is null || !AccountService.IsValidEmail(email))
        {
            return new InviteStaffResult(CreateInvitationStatus.Created, Errors: new Dictionary<string, string[]> { ["email"] = ["Email không hợp lệ."] });
        }

        var now = Now();
        var (token, hash) = AccountTokens.Create();
        var created = await store.CreateInvitationAsync(
            new NewBakeryInvitation(ownerUserId, bakeryId, email, hash, now, now + InvitationLifetime), cancellationToken);
        if (created.Status != CreateInvitationStatus.Created)
        {
            return new InviteStaffResult(created.Status);
        }

        try
        {
            await emailSender.SendStaffInvitationAsync(email, created.BakeryName!, token, created.Invitation!.ExpiresAt, cancellationToken);
            return new InviteStaffResult(CreateInvitationStatus.Created, created.Invitation, EmailDelivered: true);
        }
        catch (EmailDeliveryException)
        {
            // Lời mời vẫn hợp lệ nhưng chưa tới người nhận; owner mời lại sẽ thu hồi lời mời này.
            return new InviteStaffResult(CreateInvitationStatus.Created, created.Invitation, EmailDelivered: false);
        }
    }

    public Task<(OwnerActionOutcome Outcome, IReadOnlyList<BakeryInvitationDto> Invitations)> ListInvitationsAsync(
        long ownerUserId, long bakeryId, CancellationToken cancellationToken) =>
        store.ListInvitationsAsync(ownerUserId, bakeryId, Now(), cancellationToken);

    public Task<RevokeInvitationOutcome> RevokeInvitationAsync(
        long ownerUserId, long bakeryId, long invitationId, CancellationToken cancellationToken) =>
        store.RevokeInvitationAsync(ownerUserId, bakeryId, invitationId, Now(), cancellationToken);

    public async Task<AcceptInvitationResult> AcceptAsync(long userId, AcceptInvitationRequest request, CancellationToken cancellationToken) =>
        AccountTokens.TryHash(request.Token, out var hash)
            ? await store.AcceptInvitationAsync(userId, hash, Now(), cancellationToken)
            : new AcceptInvitationResult(AcceptInvitationStatus.InvalidToken);

    public async Task<AcceptNewAccountResult> AcceptWithNewAccountAsync(
        AcceptInvitationNewAccountRequest request, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (!AccountService.IsValidPassword(request.Password))
        {
            errors["password"] = [AccountService.PasswordRuleMessage];
        }

        var fullName = AccountService.NormalizeFullName(request.FullName, errors);
        var phone = AccountService.NormalizePhone(request.Phone, "phone", errors);
        if (errors.Count > 0)
        {
            return new AcceptNewAccountResult(new AcceptInvitationResult(AcceptInvitationStatus.InvalidToken), errors);
        }

        if (!AccountTokens.TryHash(request.Token, out var hash))
        {
            return new AcceptNewAccountResult(new AcceptInvitationResult(AcceptInvitationStatus.InvalidToken));
        }

        var result = await store.AcceptInvitationWithNewAccountAsync(
            hash, new NewInvitedUser(passwordHasher.Hash(request.Password!), fullName!, phone), Now(), cancellationToken);
        return new AcceptNewAccountResult(result);
    }

    private DateTime Now() => time.GetUtcNow().UtcDateTime;
}

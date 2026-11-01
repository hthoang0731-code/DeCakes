using DeCakes.Application.Accounts;
using DeCakes.Application.BakeryOnboarding;
using DeCakes.Domain.Accounts;

namespace DeCakes.Api.Tests.Infrastructure;

/// <summary>
/// Store onboarding giả cho kiểm thử HTTP: trả kết quả được cấu hình và ghi lại tham số (user id, bakery id) mà API
/// truyền xuống. Không mô phỏng SQL; quy tắc dữ liệu được kiểm thử trên MySQL (MySqlBakeryOnboardingStoreTests).
/// </summary>
public sealed class ScriptedBakeryOnboardingStore(InMemoryUserStore users) : IBakeryOnboardingStore
{
    private readonly object _gate = new();

    public List<(string Method, long UserId, long BakeryId)> Calls { get; } = [];

    public NewBakeryOwner? LastOwner { get; private set; }

    public string? LastReviewEvent { get; private set; }

    public string? LastReviewReason { get; private set; }

    public long NextBakeryId { get; set; } = 100;

    public BakeryProfileRecord? Profile { get; set; }

    public OwnerActionOutcome OwnerOutcome { get; set; } = OwnerActionOutcome.Succeeded;

    public SubmitProfileResult SubmitResult { get; set; } = new(SubmitProfileStatus.Submitted, []);

    public ReviewOutcome ReviewResult { get; set; } = ReviewOutcome.Succeeded;

    public RemoveMemberOutcome RemoveResult { get; set; } = RemoveMemberOutcome.Removed;

    public CreateInvitationStatus InviteStatus { get; set; } = CreateInvitationStatus.Created;

    public AcceptInvitationResult AcceptResult { get; set; } = new(AcceptInvitationStatus.Accepted, 100, 0);

    public int Count(string method)
    {
        lock (_gate) return Calls.Count(c => c.Method == method);
    }

    private void Record(string method, long userId, long bakeryId)
    {
        lock (_gate) Calls.Add((method, userId, bakeryId));
    }

    public Task<(long UserId, long BakeryId)> CreateOwnerAsync(NewBakeryOwner owner, CancellationToken cancellationToken)
    {
        if (users.FindStored(owner.Email) is not null) throw new DuplicateEmailException();
        LastOwner = owner;
        var userId = users.Add(owner.Email, owner.PasswordHash, SystemRoles.Bakery, emailVerified: false);
        Record(nameof(CreateOwnerAsync), userId, NextBakeryId);
        return Task.FromResult((userId, NextBakeryId));
    }

    public Task<BakeryProfileRecord?> FindProfileAsync(long userId, long bakeryId, CancellationToken cancellationToken)
    {
        Record(nameof(FindProfileAsync), userId, bakeryId);
        return Task.FromResult(Profile);
    }

    public Task<OwnerActionOutcome> UpdateDraftProfileAsync(long userId, long bakeryId, BakeryProfileValues values, DateTime now, CancellationToken cancellationToken)
    {
        Record(nameof(UpdateDraftProfileAsync), userId, bakeryId);
        return Task.FromResult(OwnerOutcome);
    }

    public Task<SubmitProfileResult> SubmitAsync(long userId, long bakeryId, DateTime now, CancellationToken cancellationToken)
    {
        Record(nameof(SubmitAsync), userId, bakeryId);
        return Task.FromResult(SubmitResult);
    }

    public Task<IReadOnlyList<ReviewEventDto>?> ListReviewEventsForMemberAsync(long userId, long bakeryId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ReviewEventDto>?>(Profile is null ? null : []);

    public Task<IReadOnlyList<SubscriptionPlanDto>> ListActivePlansAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SubscriptionPlanDto>>([new SubscriptionPlanDto(1, "co-ban", "Cơ bản", null, null, "VND")]);

    public Task<OwnerActionOutcome> SelectPlanAsync(long userId, long bakeryId, long planId, DateTime now, CancellationToken cancellationToken)
    {
        Record(nameof(SelectPlanAsync), userId, bakeryId);
        return Task.FromResult(OwnerOutcome);
    }

    public Task<ReviewOutcome> ReviewAsync(
        long adminUserId, long bakeryId, string eventType, string? reason, DateTime now, TimeSpan trialLength, CancellationToken cancellationToken)
    {
        Record(nameof(ReviewAsync), adminUserId, bakeryId);
        (LastReviewEvent, LastReviewReason) = (eventType, reason);
        return Task.FromResult(ReviewResult);
    }

    public Task<IReadOnlyList<AdminReviewEventDto>?> ListReviewEventsAsync(long bakeryId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AdminReviewEventDto>?>([]);

    public Task<(OwnerActionOutcome Outcome, IReadOnlyList<BakeryMemberDto> Members)> ListMembersAsync(
        long ownerUserId, long bakeryId, CancellationToken cancellationToken)
    {
        Record(nameof(ListMembersAsync), ownerUserId, bakeryId);
        return Task.FromResult<(OwnerActionOutcome, IReadOnlyList<BakeryMemberDto>)>((OwnerOutcome, []));
    }

    public Task<RemoveMemberOutcome> RemoveStaffAsync(long ownerUserId, long bakeryId, long memberUserId, DateTime now, CancellationToken cancellationToken)
    {
        Record(nameof(RemoveStaffAsync), ownerUserId, bakeryId);
        return Task.FromResult(RemoveResult);
    }

    public Task<CreateInvitationResult> CreateInvitationAsync(NewBakeryInvitation invitation, CancellationToken cancellationToken)
    {
        Record(nameof(CreateInvitationAsync), invitation.OwnerUserId, invitation.BakeryId);
        return Task.FromResult(InviteStatus == CreateInvitationStatus.Created
            ? new CreateInvitationResult(InviteStatus, new BakeryInvitationDto(1, invitation.Email, invitation.CreatedAt, invitation.ExpiresAt), "Tiệm A")
            : new CreateInvitationResult(InviteStatus, null, null));
    }

    public Task<(OwnerActionOutcome Outcome, IReadOnlyList<BakeryInvitationDto> Invitations)> ListInvitationsAsync(
        long ownerUserId, long bakeryId, DateTime now, CancellationToken cancellationToken) =>
        Task.FromResult<(OwnerActionOutcome, IReadOnlyList<BakeryInvitationDto>)>((OwnerOutcome, []));

    public Task<RevokeInvitationOutcome> RevokeInvitationAsync(long ownerUserId, long bakeryId, long invitationId, DateTime now, CancellationToken cancellationToken) =>
        Task.FromResult(RevokeInvitationOutcome.Revoked);

    public Task<AcceptInvitationResult> AcceptInvitationAsync(long userId, byte[] tokenHash, DateTime now, CancellationToken cancellationToken)
    {
        Record(nameof(AcceptInvitationAsync), userId, 0);
        return Task.FromResult(AcceptResult with { UserId = userId });
    }

    public Task<AcceptInvitationResult> AcceptInvitationWithNewAccountAsync(byte[] tokenHash, NewInvitedUser user, DateTime now, CancellationToken cancellationToken)
    {
        Record(nameof(AcceptInvitationWithNewAccountAsync), 0, 0);
        return Task.FromResult(AcceptResult with { UserId = 55 });
    }
}

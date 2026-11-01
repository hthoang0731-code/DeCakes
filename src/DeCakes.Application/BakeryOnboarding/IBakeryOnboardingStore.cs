namespace DeCakes.Application.BakeryOnboarding;

/// <summary>
/// Onboarding tiệm: tài khoản chủ tiệm, hồ sơ, gói đã chọn, review, thành viên và lời mời. Mọi thao tác theo tiệm tự
/// kiểm tra tư cách thành viên của <c>userId</c> trong cùng truy vấn/transaction (user active, role bakery, membership
/// active; thao tác quản lý cần member_role owner): bakeryId từ route không phải bằng chứng có quyền. Mỗi thao tác ghi
/// nhiều bảng là một transaction của ứng dụng (không liên quan 6 procedure nghiệp vụ).
/// </summary>
public interface IBakeryOnboardingStore
{
    /// <summary>
    /// Tạo user bakery (active, chưa xác minh email), tiệm draft và owner membership trong một transaction. Slug =
    /// <paramref name="owner"/>.SlugBase + hậu tố ngẫu nhiên, thử lại khi trùng. Ném DuplicateEmailException khi email
    /// đã có (không đổi tài khoản đang có).
    /// </summary>
    Task<(long UserId, long BakeryId)> CreateOwnerAsync(NewBakeryOwner owner, CancellationToken cancellationToken);

    Task<BakeryProfileRecord?> FindProfileAsync(long userId, long bakeryId, CancellationToken cancellationToken);

    /// <summary>Chỉ owner, chỉ khi hồ sơ draft (kiểm tra và ghi trong cùng câu lệnh/khóa).</summary>
    Task<OwnerActionOutcome> UpdateDraftProfileAsync(
        long userId, long bakeryId, BakeryProfileValues values, DateTime now, CancellationToken cancellationToken);

    /// <summary>
    /// Khóa dòng tiệm, kiểm tra owner, draft, đủ thông tin và đã chọn gói đang mở, rồi chuyển pending_review và ghi sự
    /// kiện submitted.
    /// </summary>
    Task<SubmitProfileResult> SubmitAsync(long userId, long bakeryId, DateTime now, CancellationToken cancellationToken);

    /// <summary>Null nếu user không phải thành viên active.</summary>
    Task<IReadOnlyList<ReviewEventDto>?> ListReviewEventsForMemberAsync(long userId, long bakeryId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SubscriptionPlanDto>> ListActivePlansAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Owner chọn gói đang mở. Không cho đổi khi tiệm đang có quyền sử dụng (kỳ còn hạn) hoặc hồ sơ bị từ chối.
    /// </summary>
    Task<OwnerActionOutcome> SelectPlanAsync(long userId, long bakeryId, long planId, DateTime now, CancellationToken cancellationToken);

    /// <summary>
    /// Admin xử lý hồ sơ pending_review dưới khóa dòng tiệm. Approve chuyển active và tạo trial nếu tiệm chưa từng có
    /// (một lần/tiệm, UNIQUE ở database); yêu cầu sửa về draft; từ chối sang rejected. Luôn ghi sự kiện.
    /// </summary>
    Task<ReviewOutcome> ReviewAsync(
        long adminUserId, long bakeryId, string eventType, string? reason, DateTime now, TimeSpan trialLength,
        CancellationToken cancellationToken);

    /// <summary>Null nếu tiệm không tồn tại.</summary>
    Task<IReadOnlyList<AdminReviewEventDto>?> ListReviewEventsAsync(long bakeryId, CancellationToken cancellationToken);

    /// <summary>Danh sách thành viên (cả removed); chỉ owner.</summary>
    Task<(OwnerActionOutcome Outcome, IReadOnlyList<BakeryMemberDto> Members)> ListMembersAsync(
        long ownerUserId, long bakeryId, CancellationToken cancellationToken);

    Task<RemoveMemberOutcome> RemoveStaffAsync(
        long ownerUserId, long bakeryId, long memberUserId, DateTime now, CancellationToken cancellationToken);

    /// <summary>Thu hồi lời mời đang mở cùng email rồi tạo lời mời mới. Chỉ owner.</summary>
    Task<CreateInvitationResult> CreateInvitationAsync(NewBakeryInvitation invitation, CancellationToken cancellationToken);

    /// <summary>Lời mời chưa nhận, chưa thu hồi, chưa hết hạn. Chỉ owner.</summary>
    Task<(OwnerActionOutcome Outcome, IReadOnlyList<BakeryInvitationDto> Invitations)> ListInvitationsAsync(
        long ownerUserId, long bakeryId, DateTime now, CancellationToken cancellationToken);

    Task<RevokeInvitationOutcome> RevokeInvitationAsync(
        long ownerUserId, long bakeryId, long invitationId, DateTime now, CancellationToken cancellationToken);

    /// <summary>
    /// User đang đăng nhập nhận lời mời: khóa lời mời, user phải active, role bakery và đúng email. Không đổi role của
    /// customer/admin. Membership removed được kích hoạt lại thành staff.
    /// </summary>
    Task<AcceptInvitationResult> AcceptInvitationAsync(long userId, byte[] tokenHash, DateTime now, CancellationToken cancellationToken);

    /// <summary>Tạo user bakery mới với email của lời mời (đã xác minh qua token) và membership staff, nguyên tử.</summary>
    Task<AcceptInvitationResult> AcceptInvitationWithNewAccountAsync(
        byte[] tokenHash, NewInvitedUser user, DateTime now, CancellationToken cancellationToken);
}

public sealed record NewBakeryOwner(string Email, string PasswordHash, string FullName, string? Phone, string BakeryName, string SlugBase);

public sealed record BakeryProfileValues(string Name, string? Description, string? AddressLine, string? City, string? Phone);

public sealed record BakeryProfileRecord(
    long BakeryId,
    string Name,
    string Slug,
    string? Description,
    string? AddressLine,
    string? City,
    string? Phone,
    string ProfileStatus,
    string MemberRole,
    SelectedPlanDto? SelectedPlan,
    bool SelectedPlanOpen,
    ReviewEventDto? LastDecision);

public sealed record NewBakeryInvitation(
    long OwnerUserId, long BakeryId, string Email, byte[] TokenHash, DateTime CreatedAt, DateTime ExpiresAt);

public sealed record NewInvitedUser(string PasswordHash, string FullName, string? Phone);

public enum OwnerActionOutcome
{
    Succeeded,
    /// <summary>Tiệm không tồn tại hoặc user không phải thành viên active (404, không lộ sự tồn tại).</summary>
    BakeryNotFound,
    /// <summary>Thành viên active nhưng không phải owner.</summary>
    OwnerRequired,
    /// <summary>Hồ sơ không ở trạng thái cho phép thao tác.</summary>
    NotAllowedInStatus,
    PlanNotAvailable,
    /// <summary>Đổi gói khi tiệm đang có quyền sử dụng: chưa hỗ trợ.</summary>
    PlanChangeNotAllowed,
}

public enum SubmitProfileStatus
{
    Submitted,
    BakeryNotFound,
    OwnerRequired,
    NotDraft,
    Incomplete,
}

public sealed record SubmitProfileResult(SubmitProfileStatus Status, IReadOnlyList<string> Missing);

public enum ReviewOutcome
{
    Succeeded,
    BakeryNotFound,
    NotPendingReview,
    PlanNotSelected,
}

public enum RemoveMemberOutcome
{
    Removed,
    BakeryNotFound,
    OwnerRequired,
    MemberNotFound,
    CannotRemoveOwner,
}

public enum CreateInvitationStatus
{
    Created,
    BakeryNotFound,
    OwnerRequired,
    AlreadyMember,
    NotAllowedInStatus,
}

public sealed record CreateInvitationResult(CreateInvitationStatus Status, BakeryInvitationDto? Invitation, string? BakeryName);

public enum RevokeInvitationOutcome
{
    Revoked,
    BakeryNotFound,
    OwnerRequired,
    InvitationNotFound,
}

public enum AcceptInvitationStatus
{
    Accepted,
    /// <summary>Không có, hết hạn, đã dùng, đã thu hồi hoặc không đúng email của user.</summary>
    InvalidToken,
    /// <summary>User đúng email nhưng không phải role bakery (customer/admin): không tự đổi role.</summary>
    RoleNotAllowed,
    /// <summary>Email của lời mời đã có tài khoản: đăng nhập rồi nhận lời mời.</summary>
    AccountExists,
}

public sealed record AcceptInvitationResult(AcceptInvitationStatus Status, long BakeryId = 0, long UserId = 0, string? Email = null);

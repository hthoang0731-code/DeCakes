using DeCakes.Application.Accounts;

namespace DeCakes.Application.BakeryOnboarding;

// Thuộc tính nullable: kiểm tra đầu vào nằm ở service, không phụ thuộc model binding.
public sealed record RegisterBakeryRequest(string? Email, string? Password, string? FullName, string? Phone, string? BakeryName);

public sealed record RegisteredBakeryDto(UserDto User, long BakeryId);

public sealed record BakeryProfileInput(string? Name, string? Description, string? AddressLine, string? City, string? Phone);

public sealed record SelectPlanRequest(long? PlanId);

public sealed record ReviewDecisionRequest(string? Decision, string? Reason);

public sealed record InviteStaffRequest(string? Email);

public sealed record AcceptInvitationRequest(string? Token);

public sealed record AcceptInvitationNewAccountRequest(string? Token, string? Password, string? FullName, string? Phone);

/// <summary>Hồ sơ tiệm theo góc nhìn thành viên active. <c>MissingForSubmit</c>: điều kiện gửi duyệt còn thiếu.</summary>
public sealed record BakeryProfileDto(
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
    ReviewEventDto? LastDecision,
    IReadOnlyList<string> MissingForSubmit);

public sealed record SelectedPlanDto(long PlanId, string Code, string Name);

/// <summary>Lịch sử review cho thành viên tiệm: không có id người xử lý.</summary>
public sealed record ReviewEventDto(string EventType, string? Reason, DateTime CreatedAt);

public sealed record AdminReviewEventDto(long Id, string EventType, string? Reason, long ActorUserId, DateTime CreatedAt);

/// <summary>Giá null = chưa định giá: được chọn, chưa mua được.</summary>
public sealed record SubscriptionPlanDto(long Id, string Code, string Name, decimal? MonthlyPrice, decimal? YearlyPrice, string Currency);

public sealed record BakeryMemberDto(long UserId, string Email, string FullName, string MemberRole, string Status);

public sealed record BakeryInvitationDto(long Id, string Email, DateTime CreatedAt, DateTime ExpiresAt);

/// <summary>Kết quả nhận lời mời: tiệm vừa tham gia.</summary>
public sealed record AcceptedInvitationDto(long BakeryId, long UserId);

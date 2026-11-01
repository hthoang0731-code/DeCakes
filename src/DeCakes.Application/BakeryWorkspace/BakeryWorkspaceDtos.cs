namespace DeCakes.Application.BakeryWorkspace;

/// <summary>Tiệm mà user đăng nhập là thành viên active, kèm vai trò của user trong tiệm.</summary>
public sealed record WorkspaceBakeryDto(
    long BakeryId,
    string Name,
    string ProfileStatus,
    string MemberRole);

/// <summary>
/// Một yêu cầu trong inbox của tiệm. <c>Status</c> là trạng thái lần gửi đến tiệm (request_bakeries.status),
/// <c>RequestStatus</c> là trạng thái của yêu cầu (design_requests.status). Không có thông tin tài khoản khách.
/// </summary>
public sealed record BakeryInboxItemDto(
    long RequestId,
    string Title,
    decimal? BudgetMin,
    decimal BudgetMax,
    string Currency,
    DateTime PickupAt,
    string RequestStatus,
    string Status,
    DateTime SentAt);

/// <summary>Nội dung yêu cầu và trạng thái lần gửi đến tiệm. Chưa có ảnh tham khảo (upload chưa triển khai).</summary>
public sealed record BakeryInboxRequestDto(
    long RequestId,
    string RequestStatus,
    string Title,
    string IdeaDescription,
    decimal? BudgetMin,
    decimal BudgetMax,
    string Currency,
    string? SizeDescription,
    int? Servings,
    string? Flavor,
    string? InscriptionText,
    DateTime PickupAt,
    long? InspiredByCakeId,
    string Status,
    string? DeclineReason,
    DateTime SentAt,
    DateTime? RespondedAt);

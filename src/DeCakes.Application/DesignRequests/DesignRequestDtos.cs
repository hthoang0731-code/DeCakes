namespace DeCakes.Application.DesignRequests;

/// <summary>
/// Nội dung yêu cầu khi tạo hoặc sửa (PUT thay toàn bộ). Mọi trường nullable để kiểm tra tập trung ở service.
/// PickupAt là chuỗi ISO 8601 bắt buộc có Z hoặc offset (ví dụ "2026-10-20T09:30:00+07:00").
/// </summary>
public sealed record DesignRequestInput(
    string? Title,
    string? IdeaDescription,
    decimal? BudgetMin,
    decimal? BudgetMax,
    string? SizeDescription,
    int? Servings,
    string? Flavor,
    string? InscriptionText,
    string? PickupAt,
    long? InspiredByCakeId);

public sealed record DesignRequestDto(
    long Id,
    string Status,
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
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record DesignRequestSummaryDto(
    long Id,
    string Status,
    string Title,
    decimal? BudgetMin,
    decimal BudgetMax,
    string Currency,
    DateTime PickupAt,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount);

/// <summary>Một tiệm đã nhận yêu cầu và trạng thái phản hồi của tiệm.</summary>
public sealed record RequestBakeryDto(
    long BakeryId,
    string BakeryName,
    string Status,
    string? DeclineReason,
    DateTime SentAt,
    DateTime? RespondedAt);

/// <summary>Nội dung đã kiểm tra và chuẩn hóa, sẵn sàng ghi xuống database.</summary>
public sealed record DesignRequestValues(
    string Title,
    string IdeaDescription,
    decimal? BudgetMin,
    decimal BudgetMax,
    string? SizeDescription,
    int? Servings,
    string? Flavor,
    string? InscriptionText,
    DateTime PickupAtUtc,
    long? InspiredByCakeId);

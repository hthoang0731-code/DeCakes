using DeCakes.Domain.Quotes;

namespace DeCakes.Application.Quotes;

/// <summary>
/// Một phiên bản báo giá, không gồm định danh người tạo hoặc thông tin tài khoản khách.
/// IsExpired chỉ mô tả hạn báo giá, không thay thế trạng thái đơn hoặc quyền chọn ở server.
/// </summary>
public sealed record QuoteDto(
    long Id,
    long RequestId,
    long BakeryId,
    string BakeryName,
    long RevisionNo,
    string Status,
    decimal Price,
    string Currency,
    string? SizeDescription,
    int? Servings,
    string Flavor,
    string DecorationDescription,
    string? InscriptionText,
    string? Note,
    DateTime EstimatedPickupAt,
    DateTime ValidUntil,
    long? DesignAssetId,
    DateTime? WithdrawnAt,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    public bool IsCurrent => QuoteStatuses.IsCurrent(Status);

    // Service tính theo TimeProvider khi trả response; store chỉ đọc dữ liệu gốc.
    public bool IsExpired { get; init; }
}

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

/// <summary>
/// Nội dung một phiên bản báo giá khi tạo hoặc sửa (sửa gửi toàn bộ nội dung mới). Mọi trường nullable để kiểm tra
/// tập trung ở service. Hai mốc thời gian là chuỗi ISO 8601 bắt buộc có Z hoặc offset. Tiền tệ luôn là VND.
/// Giá và giờ nhận là cam kết của tiệm, được phép khác ngân sách và giờ khách mong muốn.
/// </summary>
public sealed record QuoteInput(
    decimal? Price,
    string? SizeDescription,
    int? Servings,
    string? Flavor,
    string? DecorationDescription,
    string? InscriptionText,
    string? Note,
    string? EstimatedPickupAt,
    string? ValidUntil,
    long? DesignAssetId);

/// <summary>Nội dung đã kiểm tra và chuẩn hóa; thời gian là UTC, chính xác tới mili giây.</summary>
public sealed record QuoteValues(
    decimal Price,
    string? SizeDescription,
    int? Servings,
    string Flavor,
    string DecorationDescription,
    string? InscriptionText,
    string? Note,
    DateTime EstimatedPickupAtUtc,
    DateTime ValidUntilUtc,
    long? DesignAssetId);

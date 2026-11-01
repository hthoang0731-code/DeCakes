namespace DeCakes.Domain.DesignRequests;

/// <summary>Giá trị hợp lệ của design_requests.status (chk_design_requests_status).</summary>
public static class DesignRequestStatuses
{
    public const string Draft = "draft";
    public const string Open = "open";
    public const string Ordered = "ordered";
    public const string Cancelled = "cancelled";

    public static bool IsKnown(string? status) => status is Draft or Open or Ordered or Cancelled;

    /// <summary>
    /// Quy tắc ứng dụng: chỉ sửa nội dung khi còn draft. Chặt hơn chú thích trong 01_schema.sql (cho sửa draft/open):
    /// sau khi đã gửi tiệm, nội dung không đổi để mọi tiệm báo giá trên cùng một yêu cầu.
    /// </summary>
    public static bool CanEditContent(string status) => status == Draft;
}

/// <summary>Giá trị hợp lệ của request_bakeries.status (chk_request_bakeries_status).</summary>
public static class RequestBakeryStatuses
{
    public const string Pending = "pending";
    public const string Viewed = "viewed";
    public const string Quoted = "quoted";
    public const string Declined = "declined";

    public static bool IsKnown(string? status) => status is Pending or Viewed or Quoted or Declined;
}

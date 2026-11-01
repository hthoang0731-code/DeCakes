namespace DeCakes.Domain.BakeryOnboarding;

/// <summary>
/// Giá trị hợp lệ của bakeries.profile_status sau migrations/V004 (thêm rejected). Trạng thái hồ sơ độc lập với trạng
/// thái user, membership và quyền gói: hết hạn gói không đổi trạng thái hồ sơ.
/// </summary>
public static class BakeryProfileStatuses
{
    public const string Draft = "draft";
    public const string PendingReview = "pending_review";
    public const string Active = "active";
    public const string Suspended = "suspended";

    /// <summary>Admin từ chối: kết thúc, owner không gửi lại hồ sơ đó.</summary>
    public const string Rejected = "rejected";

    public static readonly IReadOnlyList<string> All = [Draft, PendingReview, Active, Suspended, Rejected];
}

/// <summary>bakery_review_events.event_type: lịch sử gửi duyệt và quyết định của admin.</summary>
public static class BakeryReviewEvents
{
    public const string Submitted = "submitted";
    public const string Approved = "approved";
    public const string ChangesRequested = "changes_requested";
    public const string Rejected = "rejected";
}

/// <summary>Quyết định admin gửi qua API (map sang <see cref="BakeryReviewEvents"/>).</summary>
public static class BakeryReviewDecisions
{
    public const string Approve = "approve";
    public const string RequestChanges = "request_changes";
    public const string Reject = "reject";
}

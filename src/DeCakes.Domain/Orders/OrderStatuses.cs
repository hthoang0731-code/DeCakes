namespace DeCakes.Domain.Orders;

/// <summary>Giá trị hợp lệ của orders.status (chk_orders_status). API hiện chỉ chuyển pending_confirmation sang confirmed/rejected_by_bakery.</summary>
public static class OrderStatuses
{
    public const string PendingConfirmation = "pending_confirmation";
    public const string Confirmed = "confirmed";
    public const string RejectedByBakery = "rejected_by_bakery";
    public const string Ready = "ready";
    public const string PickedUp = "picked_up";
    public const string Cancelled = "cancelled";

    public static bool IsKnown(string? status) =>
        status is PendingConfirmation or Confirmed or RejectedByBakery or Ready or PickedUp or Cancelled;
}

/// <summary>Quyết định của tiệm cho đơn chờ xác nhận (tham số p_decision của sp_bakery_decide_order).</summary>
public static class OrderDecisions
{
    public const string Confirm = "confirm";
    public const string Reject = "reject";

    // Lý do từ chối: orders.rejection_reason VARCHAR(500), đếm theo ký tự Unicode.
    public const int RejectionReasonMaxLength = 500;
}

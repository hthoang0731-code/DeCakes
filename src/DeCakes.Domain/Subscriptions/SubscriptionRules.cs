namespace DeCakes.Domain.Subscriptions;

/// <summary>
/// Quy tắc quyền sử dụng theo từng tiệm. Quyền có hiệu lực tại thời điểm t khi tiệm có một kỳ (trial hoặc trả phí) với
/// starts_at &lt;= t &lt; ends_at; tính từ thời điểm hiện tại mỗi lần kiểm tra, không dựa vào job cập nhật trạng thái.
/// </summary>
public static class SubscriptionRules
{
    /// <summary>Miễn phí ban đầu: đúng 30 × 24 giờ kể từ lúc admin duyệt; mỗi tiệm tối đa một lần.</summary>
    public static readonly TimeSpan TrialLength = TimeSpan.FromDays(30);

    /// <summary>
    /// Kỳ trả phí bắt đầu từ max(lúc xác nhận thanh toán, ngày hết hạn hiện tại) để giữ ngày còn lại, kéo dài 1 tháng
    /// hoặc 1 năm theo lịch UTC; ngày không tồn tại kẹp về cuối tháng (31/01 + 1 tháng = 28 hoặc 29/02).
    /// </summary>
    public static (DateTime StartsAt, DateTime EndsAt) PaidPeriod(DateTime paidAt, DateTime? currentAccessEndsAt, string billingCycle)
    {
        var start = currentAccessEndsAt is { } end && end > paidAt ? end : paidAt;
        var finish = billingCycle switch
        {
            BillingCycles.Month => start.AddMonths(1),
            BillingCycles.Year => start.AddYears(1),
            _ => throw new ArgumentOutOfRangeException(nameof(billingCycle), billingCycle, "Chu kỳ không hợp lệ."),
        };
        return (start, finish);
    }
}

public static class BillingCycles
{
    public const string Month = "month";
    public const string Year = "year";

    public static bool IsKnown(string? cycle) => cycle is Month or Year;
}

public static class SubscriptionPeriodKinds
{
    public const string Trial = "trial";
    public const string Paid = "paid";
}

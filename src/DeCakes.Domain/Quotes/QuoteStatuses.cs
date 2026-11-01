namespace DeCakes.Domain.Quotes;

/// <summary>Trạng thái của một phiên bản báo giá; hết hạn được tính riêng từ valid_until.</summary>
public static class QuoteStatuses
{
    public const string Submitted = "submitted";
    public const string Superseded = "superseded";
    public const string Withdrawn = "withdrawn";
    public const string Accepted = "accepted";
    public const string NotSelected = "not_selected";

    public static bool IsKnown(string? status) =>
        status is Submitted or Superseded or Withdrawn or Accepted or NotSelected;

    // Khớp cột generated current_slot; không đồng nghĩa báo giá được phép chọn để tạo đơn.
    public static bool IsCurrent(string status) => status is Submitted or Accepted;
}

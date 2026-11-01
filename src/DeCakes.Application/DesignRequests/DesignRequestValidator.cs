using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DeCakes.Application.DesignRequests;

/// <summary>Kiểm tra và chuẩn hóa nội dung yêu cầu theo giới hạn của bảng design_requests.</summary>
public static partial class DesignRequestValidator
{
    public const int TitleMaxLength = 200;            // VARCHAR(200)
    public const int IdeaDescriptionMaxBytes = 65_535; // TEXT, đếm theo byte UTF-8 (utf8mb4)
    public const int SizeDescriptionMaxLength = 100;  // VARCHAR(100)
    public const int FlavorMaxLength = 200;           // VARCHAR(200)
    public const int InscriptionMaxLength = 200;      // VARCHAR(200)
    public const int ServingsMax = ushort.MaxValue;   // SMALLINT UNSIGNED
    public static readonly decimal MoneyMax = 9_999_999_999.99m; // DECIMAL(12,2)

    public static (DesignRequestValues? Values, Dictionary<string, string[]> Errors) Validate(DesignRequestInput input, DateTimeOffset now)
    {
        var errors = new Dictionary<string, string[]>();

        var title = NormalizeSingleLine(input.Title);
        if (title is null || title.Length > TitleMaxLength || HasControl(title, allowNewLines: false))
        {
            errors["title"] = [$"Tiêu đề là bắt buộc, tối đa {TitleMaxLength} ký tự, không chứa ký tự điều khiển."];
        }

        var idea = Normalize(input.IdeaDescription);
        if (idea is null || Encoding.UTF8.GetByteCount(idea) > IdeaDescriptionMaxBytes || HasControl(idea, allowNewLines: true))
        {
            errors["ideaDescription"] = [$"Mô tả ý tưởng là bắt buộc, tối đa {IdeaDescriptionMaxBytes} byte UTF-8."];
        }

        ValidateMoney(input.BudgetMax, "budgetMax", required: true, errors);
        ValidateMoney(input.BudgetMin, "budgetMin", required: false, errors);
        if (input.BudgetMax is > 0 && input.BudgetMin is { } min && min > input.BudgetMax)
        {
            errors.TryAdd("budgetMin", ["Ngân sách tối thiểu không được lớn hơn ngân sách tối đa."]);
        }

        var size = OptionalSingleLine(input.SizeDescription, SizeDescriptionMaxLength, "sizeDescription", "Mô tả kích thước", errors);
        var flavor = OptionalSingleLine(input.Flavor, FlavorMaxLength, "flavor", "Hương vị", errors);
        var inscription = OptionalSingleLine(input.InscriptionText, InscriptionMaxLength, "inscriptionText", "Chữ trên bánh", errors);

        if (input.Servings is { } servings && (servings < 1 || servings > ServingsMax))
        {
            errors["servings"] = [$"Số phần ăn phải từ 1 đến {ServingsMax}."];
        }

        var pickupAt = ParsePickupAt(input.PickupAt, now, errors);

        if (input.InspiredByCakeId is <= 0)
        {
            errors["inspiredByCakeId"] = ["Mẫu bánh không hợp lệ."];
        }

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        return (new DesignRequestValues(
            title!, idea!, input.BudgetMin, input.BudgetMax!.Value, size, input.Servings, flavor, inscription,
            pickupAt!.Value, input.InspiredByCakeId), errors);
    }

    private static void ValidateMoney(decimal? value, string field, bool required, Dictionary<string, string[]> errors)
    {
        if (value is null)
        {
            if (required) errors[field] = ["Ngân sách tối đa là bắt buộc."];
            return;
        }

        // Không làm tròn: giá trị có hơn 2 chữ số thập phân bị từ chối.
        var v = value.Value;
        var valid = (required ? v > 0 : v >= 0) && v <= MoneyMax && v == decimal.Round(v, 2);
        if (!valid)
        {
            errors[field] = [required
                ? $"Ngân sách tối đa phải lớn hơn 0, tối đa {MoneyMax:0.00} và nhiều nhất 2 chữ số thập phân."
                : $"Ngân sách tối thiểu không âm, tối đa {MoneyMax:0.00} và nhiều nhất 2 chữ số thập phân."];
        }
    }

    private static DateTime? ParsePickupAt(string? raw, DateTimeOffset now, Dictionary<string, string[]> errors)
    {
        const string field = "pickupAt";
        if (string.IsNullOrWhiteSpace(raw))
        {
            errors[field] = ["Thời gian nhận bánh là bắt buộc."];
            return null;
        }

        if (!IsoWithOffset().IsMatch(raw)
            || !DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            errors[field] = ["Thời gian nhận bánh phải theo ISO 8601 và có Z hoặc offset, ví dụ 2026-10-20T09:30:00+07:00."];
            return null;
        }

        var utc = parsed.UtcDateTime;
        if (utc.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            errors[field] = ["Thời gian nhận bánh chỉ được chính xác tới mili giây (DATETIME(3))."];
            return null;
        }

        if (utc <= now.UtcDateTime)
        {
            errors[field] = ["Thời gian nhận bánh phải ở tương lai."];
            return null;
        }

        return utc;
    }

    private static string? OptionalSingleLine(string? raw, int maxLength, string field, string label, Dictionary<string, string[]> errors)
    {
        var value = NormalizeSingleLine(raw);
        if (value is not null && (value.Length > maxLength || HasControl(value, allowNewLines: false)))
        {
            errors[field] = [$"{label} tối đa {maxLength} ký tự, không chứa ký tự điều khiển."];
        }

        return value;
    }

    private static string? Normalize(string? raw)
    {
        var value = raw?.Trim().Normalize(NormalizationForm.FormC);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string? NormalizeSingleLine(string? raw) => Normalize(raw);

    private static bool HasControl(string value, bool allowNewLines) =>
        value.Any(c => char.IsControl(c) && !(allowNewLines && c is '\n' or '\r' or '\t'));

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex IsoWithOffset();
}

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DeCakes.Application.Quotes;

/// <summary>
/// Kiểm tra và chuẩn hóa nội dung báo giá theo giới hạn của bảng quotes, không làm tròn hay tự sửa dữ liệu.
/// VARCHAR(n) của utf8mb4 đếm theo ký tự Unicode; TEXT đếm theo byte UTF-8.
/// Thời gian: bắt buộc now &lt; validUntil &lt;= estimatedPickupAt (quy tắc đã duyệt 01/11/2026).
/// </summary>
public static partial class QuoteValidator
{
    public const int SizeDescriptionMaxLength = 100;    // VARCHAR(100)
    public const int FlavorMaxLength = 200;             // VARCHAR(200)
    public const int InscriptionMaxLength = 200;        // VARCHAR(200)
    public const int TextMaxBytes = 65_535;             // TEXT: decoration_description, note
    public const int ServingsMax = ushort.MaxValue;     // SMALLINT UNSIGNED
    public static readonly decimal PriceMax = 9_999_999_999.99m; // DECIMAL(12,2)

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static (QuoteValues? Values, Dictionary<string, string[]> Errors) Validate(QuoteInput input, DateTimeOffset now)
    {
        var errors = new Dictionary<string, string[]>();

        // Không làm tròn: giá có hơn 2 chữ số thập phân bị từ chối.
        if (input.Price is not { } price || price <= 0 || price > PriceMax || price != decimal.Round(price, 2))
        {
            errors["price"] = [$"Giá là bắt buộc, lớn hơn 0, tối đa {PriceMax.ToString("0.00", CultureInfo.InvariantCulture)} và nhiều nhất 2 chữ số thập phân."];
        }

        var size = Text(input.SizeDescription, "sizeDescription", "Mô tả kích thước", SizeDescriptionMaxLength, multiline: false, errors);
        if (input.Servings is { } servings && (servings < 1 || servings > ServingsMax))
        {
            errors["servings"] = [$"Số phần ăn phải từ 1 đến {ServingsMax}."];
        }
        else if (size is null && input.Servings is null && !errors.ContainsKey("sizeDescription"))
        {
            errors["sizeDescription"] = ["Cần mô tả kích thước hoặc số phần ăn."];
        }

        var flavor = Text(input.Flavor, "flavor", "Hương vị", FlavorMaxLength, multiline: false, errors);
        if (flavor is null) errors.TryAdd("flavor", ["Hương vị là bắt buộc."]);

        var decoration = Text(input.DecorationDescription, "decorationDescription", "Mô tả trang trí", null, multiline: true, errors);
        if (decoration is null) errors.TryAdd("decorationDescription", ["Mô tả trang trí là bắt buộc."]);

        var inscription = Text(input.InscriptionText, "inscriptionText", "Chữ trên bánh", InscriptionMaxLength, multiline: false, errors);
        var note = Text(input.Note, "note", "Ghi chú", null, multiline: true, errors);

        var pickupAt = ParseTime(input.EstimatedPickupAt, "estimatedPickupAt", "Thời gian nhận bánh dự kiến", errors);
        var validUntil = ParseTime(input.ValidUntil, "validUntil", "Hạn báo giá", errors);
        if (pickupAt is { } pickupTime && pickupTime <= now.UtcDateTime)
        {
            errors["estimatedPickupAt"] = ["Thời gian nhận bánh dự kiến phải ở tương lai."];
        }

        if (validUntil is { } until && until <= now.UtcDateTime)
        {
            errors["validUntil"] = ["Hạn báo giá phải ở tương lai."];
        }
        else if (validUntil is { } limit && pickupAt is { } pickup && limit > pickup)
        {
            errors["validUntil"] = ["Hạn báo giá không được sau thời gian nhận bánh dự kiến."];
        }

        if (input.DesignAssetId is <= 0)
        {
            errors["designAssetId"] = ["Ảnh thiết kế không hợp lệ."];
        }

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        return (new QuoteValues(
            input.Price!.Value, size, input.Servings, flavor!, decoration!, inscription, note,
            pickupAt!.Value, validUntil!.Value, input.DesignAssetId), errors);
    }

    // maxLength: số ký tự Unicode tối đa (VARCHAR); null nghĩa là TEXT, giới hạn theo byte UTF-8.
    private static string? Text(
        string? raw, string field, string label, int? maxLength, bool multiline, Dictionary<string, string[]> errors)
    {
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        int bytes;
        try
        {
            // UTF-16 lỗi (surrogate lẻ) sẽ bị driver thay bằng U+FFFD, tức âm thầm đổi dữ liệu: từ chối thay vì lưu sai.
            value = value.Normalize(NormalizationForm.FormC);
            bytes = StrictUtf8.GetByteCount(value);
        }
        catch (ArgumentException)
        {
            errors[field] = [$"{label} chứa ký tự Unicode không hợp lệ."];
            return null;
        }

        var tooLong = maxLength is { } max ? value.EnumerateRunes().Count() > max : bytes > TextMaxBytes;
        if (tooLong || value.Any(c => char.IsControl(c) && !(multiline && c is '\n' or '\r' or '\t')))
        {
            errors[field] = [maxLength is { } limit
                ? $"{label} tối đa {limit} ký tự, không chứa ký tự điều khiển."
                : $"{label} tối đa {TextMaxBytes} byte UTF-8, không chứa ký tự điều khiển ngoài xuống dòng và tab."];
            return null;
        }

        return value;
    }

    private static DateTime? ParseTime(string? raw, string field, string label, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            errors[field] = [$"{label} là bắt buộc."];
            return null;
        }

        if (!IsoWithOffset().IsMatch(raw)
            || !DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            errors[field] = [$"{label} phải theo ISO 8601 và có Z hoặc offset, ví dụ 2026-11-20T09:30:00+07:00."];
            return null;
        }

        var utc = parsed.UtcDateTime;
        if (utc.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            errors[field] = [$"{label} chỉ được chính xác tới mili giây (DATETIME(3))."];
            return null;
        }

        return utc;
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex IsoWithOffset();
}

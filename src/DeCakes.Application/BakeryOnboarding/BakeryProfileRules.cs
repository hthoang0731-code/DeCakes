using System.Globalization;
using System.Text;
using DeCakes.Application.Accounts;

namespace DeCakes.Application.BakeryOnboarding;

/// <summary>Kiểm tra hồ sơ tiệm theo schema bakeries và điều kiện tối thiểu để gửi duyệt đã chốt.</summary>
public static class BakeryProfileRules
{
    public const int NameMaxLength = 150;
    public const int SlugMaxLength = 160;
    public const int DescriptionMaxBytes = 65_535;
    public const int AddressMaxLength = 255;
    public const int CityMaxLength = 100;

    // Hậu tố slug "-" + 6 ký tự hex do store thêm.
    public const int SlugSuffixLength = 7;

    /// <summary>Chuẩn hóa dữ liệu hồ sơ (trim, NFC, rỗng thành null). Tên tiệm luôn bắt buộc vì cột NOT NULL.</summary>
    public static BakeryProfileValues? Normalize(BakeryProfileInput input, IDictionary<string, string[]> errors)
    {
        var errorCount = errors.Count;
        var name = SingleLine(input.Name, "name", NameMaxLength, errors);
        if (name is null && !errors.ContainsKey("name"))
        {
            errors["name"] = [$"Tên tiệm là bắt buộc và tối đa {NameMaxLength} ký tự."];
        }

        var description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim().Normalize(NormalizationForm.FormC);
        if (description is not null && Encoding.UTF8.GetByteCount(description) > DescriptionMaxBytes)
        {
            errors["description"] = [$"Mô tả tối đa {DescriptionMaxBytes} byte UTF-8."];
        }

        var address = SingleLine(input.AddressLine, "addressLine", AddressMaxLength, errors);
        var city = SingleLine(input.City, "city", CityMaxLength, errors);
        var phone = AccountService.NormalizePhone(input.Phone, "phone", errors);
        return errors.Count == errorCount ? new BakeryProfileValues(name!, description, address, city, phone) : null;
    }

    /// <summary>Tên trường còn thiếu để gửi duyệt: name, addressLine, city, phone (không chỉ khoảng trắng) và plan.</summary>
    public static IReadOnlyList<string> MissingForSubmit(
        string? name, string? addressLine, string? city, string? phone, bool hasOpenPlan)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(name)) missing.Add("name");
        if (string.IsNullOrWhiteSpace(addressLine)) missing.Add("addressLine");
        if (string.IsNullOrWhiteSpace(city)) missing.Add("city");
        if (string.IsNullOrWhiteSpace(phone)) missing.Add("phone");
        if (!hasOpenPlan) missing.Add("plan");
        return missing;
    }

    /// <summary>Phần đầu của slug từ tên tiệm: bỏ dấu, chữ thường ASCII, ký tự khác thành '-'. Store thêm hậu tố ngẫu nhiên.</summary>
    public static string SlugBase(string name)
    {
        var decomposed = name.Replace('đ', 'd').Replace('Đ', 'D').Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            var lower = char.ToLowerInvariant(c);
            if (char.IsAsciiLetterOrDigit(lower)) builder.Append(lower);
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length > SlugMaxLength - SlugSuffixLength) slug = slug[..(SlugMaxLength - SlugSuffixLength)].TrimEnd('-');
        return slug.Length == 0 ? "tiem" : slug;
    }

    private static string? SingleLine(string? value, string field, int maxLength, IDictionary<string, string[]> errors)
    {
        var text = string.IsNullOrWhiteSpace(value) ? null : value.Trim().Normalize(NormalizationForm.FormC);
        if (text is not null && (text.Length > maxLength || text.Any(char.IsControl)))
        {
            errors[field] = [$"Tối đa {maxLength} ký tự, không có ký tự điều khiển."];
        }

        return text;
    }
}

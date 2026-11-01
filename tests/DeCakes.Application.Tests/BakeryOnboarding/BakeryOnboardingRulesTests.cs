using DeCakes.Application.BakeryOnboarding;
using DeCakes.Domain.Subscriptions;

namespace DeCakes.Application.Tests.BakeryOnboarding;

public class BakeryProfileRulesTests
{
    [Theory]
    [InlineData("Tiệm Bánh Đẹp", "tiem-banh-dep")]
    [InlineData("  Bánh   Ngọt -- Hà Nội!! ", "banh-ngot-ha-noi")]
    [InlineData("ĐÀ LẠT 24/7", "da-lat-24-7")]
    [InlineData("🎂🎂", "tiem")]
    public void Slug_base_is_ascii_lowercase_without_diacritics(string name, string expected)
    {
        Assert.Equal(expected, BakeryProfileRules.SlugBase(name));
    }

    [Fact]
    public void Slug_base_leaves_room_for_random_suffix_within_column_length()
    {
        // Tên dài nhất hợp lệ (150 ký tự, kể cả có dấu) vẫn để đủ chỗ cho hậu tố "-xxxxxx" trong slug VARCHAR(160).
        foreach (var name in new[] { new string('a', BakeryProfileRules.NameMaxLength), new string('ặ', BakeryProfileRules.NameMaxLength) })
        {
            Assert.True(BakeryProfileRules.SlugBase(name).Length + BakeryProfileRules.SlugSuffixLength <= BakeryProfileRules.SlugMaxLength);
        }
    }

    [Fact]
    public void Profile_values_are_trimmed_and_blank_optional_fields_become_null()
    {
        var errors = new Dictionary<string, string[]>();

        var values = BakeryProfileRules.Normalize(new BakeryProfileInput("  Tiệm  ", "  ", " 1 Lê Lợi ", "", " 0901 234 "), errors);

        Assert.Empty(errors);
        Assert.Equal(new BakeryProfileValues("Tiệm", null, "1 Lê Lợi", null, "0901 234"), values);
    }

    [Fact]
    public void Profile_rejects_lengths_beyond_schema_and_control_characters()
    {
        var errors = new Dictionary<string, string[]>();

        var values = BakeryProfileRules.Normalize(new BakeryProfileInput(
            new string('a', 151), new string('ă', 32_768), "dòng 1\ndòng 2", new string('x', 101), "12-ab"), errors);

        Assert.Null(values);
        Assert.Equal(["addressLine", "city", "description", "name", "phone"], errors.Keys.Order());
    }

    [Fact]
    public void Missing_fields_for_submit_follow_agreed_minimum()
    {
        Assert.Equal(["addressLine", "city", "phone", "plan"], BakeryProfileRules.MissingForSubmit("Tiệm", " ", null, "", hasOpenPlan: false));
        Assert.Empty(BakeryProfileRules.MissingForSubmit("Tiệm", "1 Lê Lợi", "Huế", "0901", hasOpenPlan: true));
    }
}

public class SubscriptionRulesTests
{
    private static DateTime Utc(int y, int m, int d, int h = 0) => new(y, m, d, h, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("month", 2026, 1, 31, 2026, 2, 28)]
    [InlineData("month", 2028, 1, 31, 2028, 2, 29)]
    [InlineData("month", 2026, 3, 31, 2026, 4, 30)]
    [InlineData("year", 2028, 2, 29, 2029, 2, 28)]
    [InlineData("year", 2026, 10, 22, 2027, 10, 22)]
    public void Paid_period_uses_calendar_months_and_years_with_end_of_month_clamping(
        string cycle, int y, int m, int d, int ey, int em, int ed)
    {
        var (start, end) = SubscriptionRules.PaidPeriod(Utc(y, m, d, 9), null, cycle);

        Assert.Equal(Utc(y, m, d, 9), start);
        Assert.Equal(Utc(ey, em, ed, 9), end);
    }

    [Fact]
    public void Early_renewal_keeps_remaining_days_and_late_renewal_starts_at_payment()
    {
        var paidAt = Utc(2026, 10, 1);

        Assert.Equal((Utc(2026, 10, 31), Utc(2026, 11, 30)), SubscriptionRules.PaidPeriod(paidAt, Utc(2026, 10, 31), BillingCycles.Month));
        Assert.Equal((paidAt, Utc(2026, 11, 1)), SubscriptionRules.PaidPeriod(paidAt, Utc(2026, 9, 1), BillingCycles.Month));
        Assert.Equal((paidAt, Utc(2026, 11, 1)), SubscriptionRules.PaidPeriod(paidAt, paidAt, BillingCycles.Month));
    }

    [Fact]
    public void Trial_is_exactly_thirty_days_and_unknown_cycle_is_rejected()
    {
        Assert.Equal(TimeSpan.FromHours(720), SubscriptionRules.TrialLength);
        Assert.Throws<ArgumentOutOfRangeException>(() => SubscriptionRules.PaidPeriod(Utc(2026, 1, 1), null, "week"));
    }
}

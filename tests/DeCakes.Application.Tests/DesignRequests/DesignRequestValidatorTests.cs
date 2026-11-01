using DeCakes.Application.DesignRequests;

namespace DeCakes.Application.Tests.DesignRequests;

public class DesignRequestValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);

    internal static DesignRequestInput Valid() => new(
        Title: "Bánh sinh nhật",
        IdeaDescription: "Bánh hai tầng, tông hồng.",
        BudgetMin: 500_000m,
        BudgetMax: 1_200_000.50m,
        SizeDescription: "20 cm",
        Servings: 12,
        Flavor: "Vani",
        InscriptionText: "Mừng sinh nhật",
        PickupAt: "2026-10-20T09:30:00+07:00",
        InspiredByCakeId: null);

    private static Dictionary<string, string[]> Errors(DesignRequestInput input) => DesignRequestValidator.Validate(input, Now).Errors;

    [Fact]
    public void Valid_input_is_normalized_and_pickup_converted_to_utc()
    {
        var input = Valid() with { Title = "  Bánh  ", SizeDescription = "   ", Flavor = null, InscriptionText = "" };

        var (values, errors) = DesignRequestValidator.Validate(input, Now);

        Assert.Empty(errors);
        Assert.Equal("Bánh", values!.Title);
        Assert.Null(values.SizeDescription);
        Assert.Null(values.Flavor);
        Assert.Null(values.InscriptionText);
        Assert.Equal(new DateTime(2026, 10, 20, 2, 30, 0, DateTimeKind.Utc), values.PickupAtUtc);
        Assert.Equal(DateTimeKind.Utc, values.PickupAtUtc.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-10-20T09:30:00")]          // thiếu Z/offset
    [InlineData("2026-10-20")]
    [InlineData("2026-10-20 09:30:00Z")]
    [InlineData("20/10/2026 09:30")]
    [InlineData("2026-10-20T09:30:00.0001Z")]    // dưới mili giây
    [InlineData("2026-10-20T09:30:00.1234567Z")]
    [InlineData("2026-10-15T23:59:59.999Z")]     // quá khứ
    [InlineData("2026-10-16T00:00:00Z")]         // đúng thời điểm hiện tại
    [InlineData("2026-10-16T06:59:59+07:00")]    // quá khứ theo UTC
    public void Invalid_pickup_is_rejected(string? pickupAt)
    {
        Assert.Contains("pickupAt", Errors(Valid() with { PickupAt = pickupAt }).Keys);
    }

    [Theory]
    [InlineData("2026-10-16T00:00:00.001Z")]
    [InlineData("2026-10-20T09:30:00.123+07:00")]
    [InlineData("2026-10-20T09:30:00.1230000-05:00")]
    public void Future_pickup_with_millisecond_precision_is_accepted(string pickupAt)
    {
        Assert.Empty(Errors(Valid() with { PickupAt = pickupAt }));
    }

    public static TheoryData<decimal?, decimal?, string> InvalidBudgets() => new()
    {
        { null, null, "budgetMax" },
        { null, 0m, "budgetMax" },
        { null, -1m, "budgetMax" },
        { null, 1.005m, "budgetMax" },
        { null, 10_000_000_000m, "budgetMax" },
        { -0.01m, 100m, "budgetMin" },
        { 1.001m, 100m, "budgetMin" },
        { 100.01m, 100m, "budgetMin" },
    };

    [Theory]
    [MemberData(nameof(InvalidBudgets))]
    public void Invalid_budget_is_rejected_without_rounding(decimal? min, decimal? max, string field)
    {
        Assert.Contains(field, Errors(Valid() with { BudgetMin = min, BudgetMax = max }).Keys);
    }

    public static TheoryData<decimal?, decimal> ValidBudgets() => new()
    {
        { null, 0.01m },
        { 0m, 100m },
        { 100m, 100m },
        { null, 9_999_999_999.99m },
        { 1.50m, 2.5m },
    };

    [Theory]
    [MemberData(nameof(ValidBudgets))]
    public void Valid_budgets_are_accepted(decimal? min, decimal max)
    {
        Assert.Empty(Errors(Valid() with { BudgetMin = min, BudgetMax = max }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65_536)]
    public void Servings_out_of_smallint_unsigned_range_is_rejected(int servings)
    {
        Assert.Contains("servings", Errors(Valid() with { Servings = servings }).Keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(65_535)]
    public void Servings_is_optional_and_accepts_boundaries(int? servings)
    {
        Assert.Empty(Errors(Valid() with { Servings = servings, SizeDescription = null }));
    }

    [Fact]
    public void String_limits_follow_schema()
    {
        Assert.Empty(Errors(Valid() with { Title = new string('t', 200), SizeDescription = new string('s', 100), Flavor = new string('f', 200), InscriptionText = new string('i', 200) }));

        Assert.Contains("title", Errors(Valid() with { Title = new string('t', 201) }).Keys);
        Assert.Contains("title", Errors(Valid() with { Title = "   " }).Keys);
        Assert.Contains("title", Errors(Valid() with { Title = "Dòng 1\nDòng 2" }).Keys);
        Assert.Contains("sizeDescription", Errors(Valid() with { SizeDescription = new string('s', 101) }).Keys);
        Assert.Contains("flavor", Errors(Valid() with { Flavor = new string('f', 201) }).Keys);
        Assert.Contains("inscriptionText", Errors(Valid() with { InscriptionText = new string('i', 201) }).Keys);
        Assert.Contains("inscriptionText", Errors(Valid() with { InscriptionText = "chữ\u0007" }).Keys);
    }

    [Fact]
    public void Idea_description_is_limited_by_utf8_bytes_of_text_column()
    {
        Assert.Empty(Errors(Valid() with { IdeaDescription = new string('ữ', 21_845) }));            // 65 535 byte
        Assert.Contains("ideaDescription", Errors(Valid() with { IdeaDescription = new string('ữ', 21_846) }).Keys);
        Assert.Contains("ideaDescription", Errors(Valid() with { IdeaDescription = null }).Keys);
        Assert.Empty(Errors(Valid() with { IdeaDescription = "Dòng 1\nDòng 2\tcó tab" }));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    public void Non_positive_cake_id_is_rejected(long cakeId)
    {
        Assert.Contains("inspiredByCakeId", Errors(Valid() with { InspiredByCakeId = cakeId }).Keys);
    }
}

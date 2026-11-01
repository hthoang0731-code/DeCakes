using DeCakes.Application.Quotes;
using DeCakes.Domain.Quotes;

namespace DeCakes.Application.Tests.Quotes;

public class QuoteWriteServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    // Giá vượt mọi ngân sách thông thường và giờ nhận tùy tiệm đề xuất: validator không so với yêu cầu ban đầu.
    private static QuoteInput Valid() => new(
        2_500_000.50m, " 20 cm ", 12, " Vani ", "Hoa kem\nMàu hồng", " Chúc mừng ", " Giao tận nơi ",
        "2026-11-10T09:30:00.123+07:00", "2026-11-05T00:00:00Z", 15);

    private static (QuoteValues? Values, Dictionary<string, string[]> Errors) Validate(QuoteInput input) =>
        QuoteValidator.Validate(input, Now);

    [Fact]
    public void Valid_input_is_trimmed_and_converted_to_utc_without_rounding()
    {
        var (values, errors) = Validate(Valid());

        Assert.Empty(errors);
        Assert.Equal(new QuoteValues(2_500_000.50m, "20 cm", 12, "Vani", "Hoa kem\nMàu hồng", "Chúc mừng", "Giao tận nơi",
            new DateTime(2026, 11, 10, 2, 30, 0, 123, DateTimeKind.Utc), new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc), 15), values);
        Assert.Equal(DateTimeKind.Utc, values!.EstimatedPickupAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, values.ValidUntilUtc.Kind);
    }

    [Theory]
    [InlineData("price-null", "price")]
    [InlineData("price-zero", "price")]
    [InlineData("price-negative", "price")]
    [InlineData("price-three-decimals", "price")]
    [InlineData("price-over-decimal-12-2", "price")]
    [InlineData("no-size-or-servings", "sizeDescription")]
    [InlineData("servings-zero", "servings")]
    [InlineData("servings-over-smallint-unsigned", "servings")]
    [InlineData("size-101-chars", "sizeDescription")]
    [InlineData("flavor-missing", "flavor")]
    [InlineData("flavor-201-emoji", "flavor")]
    [InlineData("flavor-newline", "flavor")]
    [InlineData("flavor-lone-surrogate", "flavor")]
    [InlineData("decoration-blank", "decorationDescription")]
    [InlineData("decoration-65536-bytes", "decorationDescription")]
    [InlineData("decoration-nul", "decorationDescription")]
    [InlineData("inscription-201-chars", "inscriptionText")]
    [InlineData("note-65536-bytes", "note")]
    [InlineData("pickup-missing", "estimatedPickupAt")]
    [InlineData("pickup-without-offset", "estimatedPickupAt")]
    [InlineData("pickup-sub-millisecond", "estimatedPickupAt")]
    [InlineData("pickup-in-past", "estimatedPickupAt")]
    [InlineData("valid-until-equals-now", "validUntil")]
    [InlineData("valid-until-after-pickup", "validUntil")]
    [InlineData("valid-until-date-only", "validUntil")]
    [InlineData("design-asset-zero", "designAssetId")]
    public void Invalid_content_is_rejected_per_field(string scenario, string field)
    {
        var input = Valid();
        input = scenario switch
        {
            "price-null" => input with { Price = null },
            "price-zero" => input with { Price = 0m },
            "price-negative" => input with { Price = -1m },
            "price-three-decimals" => input with { Price = 1.234m },
            "price-over-decimal-12-2" => input with { Price = 10_000_000_000m },
            "no-size-or-servings" => input with { SizeDescription = "  ", Servings = null },
            "servings-zero" => input with { Servings = 0 },
            "servings-over-smallint-unsigned" => input with { Servings = 65_536 },
            "size-101-chars" => input with { SizeDescription = new string('x', 101) },
            "flavor-missing" => input with { Flavor = null },
            "flavor-201-emoji" => input with { Flavor = string.Concat(Enumerable.Repeat("🎂", 201)) },
            "flavor-newline" => input with { Flavor = "Vani\nDâu" },
            "flavor-lone-surrogate" => input with { Flavor = "Vani\ud800" },
            "decoration-blank" => input with { DecorationDescription = " \n " },
            "decoration-65536-bytes" => input with { DecorationDescription = new string('ệ', 21_845) + "a" },
            "decoration-nul" => input with { DecorationDescription = "Hoa\0" },
            "inscription-201-chars" => input with { InscriptionText = new string('x', 201) },
            "note-65536-bytes" => input with { Note = new string('a', 65_536) },
            "pickup-missing" => input with { EstimatedPickupAt = null },
            "pickup-without-offset" => input with { EstimatedPickupAt = "2026-11-10T09:30:00" },
            "pickup-sub-millisecond" => input with { EstimatedPickupAt = "2026-11-10T09:30:00.0001Z" },
            "pickup-in-past" => input with { EstimatedPickupAt = "2026-11-01T11:59:59Z", ValidUntil = null },
            "valid-until-equals-now" => input with { ValidUntil = "2026-11-01T19:00:00+07:00" },
            "valid-until-after-pickup" => input with { ValidUntil = "2026-11-10T02:30:00.124Z" },
            "valid-until-date-only" => input with { ValidUntil = "2026-11-05" },
            "design-asset-zero" => input with { DesignAssetId = 0 },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        var (values, errors) = Validate(input);

        Assert.Null(values);
        Assert.Contains(field, errors.Keys);
    }

    [Fact]
    public void Schema_limits_and_time_boundaries_are_inclusive()
    {
        var input = Valid() with
        {
            Price = 9_999_999_999.99m,
            SizeDescription = null,
            Servings = 65_535,
            Flavor = string.Concat(Enumerable.Repeat("🎂", 200)), // 200 ký tự, 400 UTF-16 code unit
            DecorationDescription = new string('ệ', 21_845),      // đúng 65 535 byte UTF-8
            InscriptionText = new string('x', 200),
            Note = "Dòng 1\r\n\tDòng 2",
            EstimatedPickupAt = "2026-11-01T12:00:00.001Z",
            ValidUntil = "2026-11-01T12:00:00.001Z",              // now < validUntil == estimatedPickupAt
            DesignAssetId = null,
        };

        var (values, errors) = Validate(input);

        Assert.Empty(errors);
        Assert.Equal(9_999_999_999.99m, values!.Price);
        Assert.Null(values.SizeDescription);
        Assert.Equal(values.EstimatedPickupAtUtc, values.ValidUntilUtc);
        Assert.Equal(1.23m, Validate(Valid() with { Price = 1.230m }).Values!.Price);
    }

    [Fact]
    public async Task Invalid_input_never_reaches_the_store()
    {
        var store = new Stub();
        var service = new QuoteService(store, new Clock(Now));

        Assert.IsType<SaveQuoteResult.ValidationFailed>(await service.CreateAsync(4, 3, 7, Valid() with { Price = 0m }, CancellationToken.None));
        Assert.IsType<SaveQuoteResult.ValidationFailed>(await service.ReviseAsync(4, 3, 7, 9, Valid() with { ValidUntil = null }, CancellationToken.None));
        Assert.Equal(0, store.Writes);
        Assert.Equal(0, store.Reads);
    }

    [Fact]
    public async Task Create_forwards_session_scope_and_reads_back_the_new_version()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new Stub { Write = new QuoteWriteResult(QuoteWriteOutcome.Saved, 21) };
        var service = new QuoteService(store, new Clock(Now));

        var saved = Assert.IsType<SaveQuoteResult.Saved>(await service.CreateAsync(4, 3, 7, Valid(), cancellation.Token));

        Assert.Equal(("create", 4L, 3L, 7L, 0L), store.WriteScope);
        Assert.Equal("Vani", store.Values!.Flavor);
        Assert.Equal((4L, 3L, 7L, 21L), store.ReadScope);
        Assert.Equal(21, saved.Quote.Id);
        Assert.False(saved.Quote.IsExpired);
        Assert.Equal(cancellation.Token, store.Token);
    }

    [Fact]
    public async Task Revise_and_withdraw_use_the_route_quote_and_read_back_the_returned_id()
    {
        var store = new Stub { Write = new QuoteWriteResult(QuoteWriteOutcome.Saved, 22) };
        var service = new QuoteService(store, new Clock(Now));

        Assert.Equal(22, Assert.IsType<SaveQuoteResult.Saved>(await service.ReviseAsync(4, 3, 7, 9, Valid(), CancellationToken.None)).Quote.Id);
        Assert.Equal(("revise", 4L, 3L, 7L, 9L), store.WriteScope);

        store.Write = new QuoteWriteResult(QuoteWriteOutcome.Saved, 9);
        store.Status = QuoteStatuses.Withdrawn;
        var withdrawn = Assert.IsType<SaveQuoteResult.Saved>(await service.WithdrawAsync(4, 3, 7, 9, CancellationToken.None));
        Assert.Equal(("withdraw", 4L, 3L, 7L, 9L), store.WriteScope);
        Assert.Equal((QuoteStatuses.Withdrawn, false), (withdrawn.Quote.Status, withdrawn.Quote.IsCurrent));
    }

    [Theory]
    [InlineData(QuoteWriteOutcome.QuoteNotFound)]
    [InlineData(QuoteWriteOutcome.QuoteNotCurrent)]
    [InlineData(QuoteWriteOutcome.BakerySubscriptionInactive)]
    public async Task Rejections_are_returned_without_reading_back(QuoteWriteOutcome outcome)
    {
        var store = new Stub { Write = new QuoteWriteResult(outcome) };
        var service = new QuoteService(store, new Clock(Now));

        Assert.Equal(outcome, Assert.IsType<SaveQuoteResult.Rejected>(await service.ReviseAsync(4, 3, 7, 9, Valid(), CancellationToken.None)).Reason);
        Assert.Equal(outcome, Assert.IsType<SaveQuoteResult.Rejected>(await service.WithdrawAsync(4, 3, 7, 9, CancellationToken.None)).Reason);
        Assert.Equal(0, store.Reads);
    }

    [Fact]
    public async Task Missing_id_unreadable_result_and_store_failures_are_not_reported_as_success()
    {
        var service = new QuoteService(new Stub { Write = new QuoteWriteResult(QuoteWriteOutcome.Saved) }, new Clock(Now));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(4, 3, 7, Valid(), CancellationToken.None));

        var hidden = new Stub { Write = new QuoteWriteResult(QuoteWriteOutcome.Saved, 21), Read = QuoteReadOutcome.QuoteNotFound };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new QuoteService(hidden, new Clock(Now)).CreateAsync(4, 3, 7, Valid(), CancellationToken.None));

        var failing = new Stub { Throw = true };
        await Assert.ThrowsAsync<TimeoutException>(() => new QuoteService(failing, new Clock(Now)).WithdrawAsync(4, 3, 7, 9, CancellationToken.None));
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Stub : IQuoteStore
    {
        public QuoteWriteResult Write { get; set; } = new(QuoteWriteOutcome.Saved, 21);
        public QuoteReadOutcome Read { get; init; } = QuoteReadOutcome.Found;
        public string Status { get; set; } = QuoteStatuses.Submitted;
        public bool Throw { get; init; }
        public int Writes { get; private set; }
        public int Reads { get; private set; }
        public (string Action, long Actor, long Bakery, long Request, long Quote) WriteScope { get; private set; }
        public (long Actor, long Bakery, long Request, long Quote) ReadScope { get; private set; }
        public QuoteValues? Values { get; private set; }
        public CancellationToken Token { get; private set; }

        private Task<QuoteWriteResult> Record(string action, long actor, long bakery, long request, long quote, QuoteValues? values, CancellationToken token)
        {
            Writes++;
            WriteScope = (action, actor, bakery, request, quote);
            Values = values;
            Token = token;
            if (Throw) throw new TimeoutException("Lock wait timeout");
            return Task.FromResult(Write);
        }

        public Task<QuoteWriteResult> CreateAsync(long actorUserId, long bakeryId, long requestId, QuoteValues values, CancellationToken cancellationToken) =>
            Record("create", actorUserId, bakeryId, requestId, 0, values, cancellationToken);
        public Task<QuoteWriteResult> ReviseAsync(long actorUserId, long bakeryId, long requestId, long quoteId, QuoteValues values, CancellationToken cancellationToken) =>
            Record("revise", actorUserId, bakeryId, requestId, quoteId, values, cancellationToken);
        public Task<QuoteWriteResult> WithdrawAsync(long actorUserId, long bakeryId, long requestId, long quoteId, CancellationToken cancellationToken) =>
            Record("withdraw", actorUserId, bakeryId, requestId, quoteId, null, cancellationToken);

        public Task<QuoteReadResult> FindForBakeryAsync(long actorUserId, long bakeryId, long requestId, long quoteId, CancellationToken cancellationToken)
        {
            Reads++;
            ReadScope = (actorUserId, bakeryId, requestId, quoteId);
            return Task.FromResult(Read == QuoteReadOutcome.Found
                ? new QuoteReadResult(Read, QuoteReadServiceTests.Quote(Status) with { Id = quoteId, ValidUntil = Now.UtcDateTime.AddDays(1) })
                : new QuoteReadResult(Read));
        }

        public Task<QuoteListResult> ListForBakeryAsync(long actorUserId, long bakeryId, long requestId, string? status, int page, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<QuoteListResult> ListForCustomerAsync(long customerId, long requestId, string? status, int page, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<QuoteReadResult> FindForCustomerAsync(long customerId, long requestId, long quoteId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

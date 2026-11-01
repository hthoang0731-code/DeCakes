using DeCakes.Application.DesignRequests;
using DeCakes.Application.Orders;
using DeCakes.Domain.Orders;

namespace DeCakes.Application.Tests.Orders;

public class OrderServiceTests
{
    private static readonly DateTime Created = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
    private static CancellationToken Ct => CancellationToken.None;

    public static OrderDto Order(long id = 31, string status = OrderStatuses.PendingConfirmation) => new(
        id, 7, 9, 3, "Tiệm", status, 2, 2_500_000.50m, "VND", Created.AddDays(5), "20 cm", 12, "Vani", "Hoa kem", null, null,
        15, "design_proposal", 2, null, null, null, null, Created, Created);

    [Theory]
    [InlineData("pending_confirmation")]
    [InlineData("confirmed")]
    [InlineData("rejected_by_bakery")]
    [InlineData("ready")]
    [InlineData("picked_up")]
    [InlineData("cancelled")]
    public async Task Every_schema_status_is_a_valid_filter(string status)
    {
        var store = new Stub();
        var service = new OrderService(store);

        Assert.IsType<ListOrdersResult.Ok>(await service.ListForCustomerAsync(4, " " + status + " ", null, null, Ct));
        Assert.Equal((status, 1, 20), store.Filter);
        Assert.IsType<ListOrdersResult.Ok>(await service.ListForBakeryAsync(4, 3, status, 3, 100, Ct));
        Assert.Equal((status, 3, 100), store.Filter);
        Assert.Equal("bakery:4:3", store.ListScope);
    }

    [Theory]
    [InlineData("pending", null, null, "status")]
    [InlineData("CONFIRMED", null, null, "status")]
    [InlineData(null, 0, null, "page")]
    [InlineData(null, null, 0, "pageSize")]
    [InlineData(null, null, 101, "pageSize")]
    public async Task Invalid_list_query_does_not_reach_the_store(string? status, int? page, int? pageSize, string field)
    {
        var store = new Stub();
        var service = new OrderService(store);

        var customer = Assert.IsType<ListOrdersResult.ValidationFailed>(await service.ListForCustomerAsync(4, status, page, pageSize, Ct));
        var bakery = Assert.IsType<ListOrdersResult.ValidationFailed>(await service.ListForBakeryAsync(4, 3, status, page, pageSize, Ct));

        Assert.Contains(field, customer.Errors.Keys);
        Assert.Contains(field, bakery.Errors.Keys);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task Blank_status_lists_every_status_with_default_paging()
    {
        var store = new Stub();
        var result = await new OrderService(store).ListForCustomerAsync(4, "  ", null, null, Ct);

        Assert.Single(Assert.IsType<ListOrdersResult.Ok>(result).Result.Page!.Items);
        Assert.Equal((null, 1, 20), store.Filter);
        Assert.Equal("customer:4", store.ListScope);
    }

    [Fact]
    public async Task Accept_forwards_scope_and_reads_back_in_the_customer_scope()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new Stub { Write = new OrderWriteResult(OrderWriteOutcome.Saved, 31) };

        var saved = Assert.IsType<SaveOrderResult.Saved>(await new OrderService(store).AcceptAsync(4, 7, 9, cancellation.Token));

        Assert.Equal(31, saved.Order.Id);
        Assert.Equal("accept:4:7:9", store.WriteScope);
        Assert.Equal("customer:4:31", store.ReadScope);
        Assert.Equal(cancellation.Token, store.Token);
    }

    [Fact]
    public async Task Confirm_sends_no_reason_and_reads_back_in_the_bakery_scope()
    {
        var store = new Stub { Write = new OrderWriteResult(OrderWriteOutcome.Saved, 31) };
        var service = new OrderService(store);

        Assert.IsType<SaveOrderResult.Saved>(await service.DecideAsync(4, 3, 31, new OrderDecisionInput("confirm", null), Ct));
        Assert.Equal(("decide:4:3:31", "confirm", (string?)null), (store.WriteScope, store.Decision, store.Reason));
        Assert.Equal("bakery:4:3:31", store.ReadScope);

        // Chuỗi rỗng/khoảng trắng coi như không gửi lý do.
        Assert.IsType<SaveOrderResult.Saved>(await service.DecideAsync(4, 3, 31, new OrderDecisionInput("confirm", " \t "), Ct));
        Assert.Null(store.Reason);
    }

    [Fact]
    public async Task Reject_reason_is_trimmed_normalized_and_may_have_exactly_500_characters()
    {
        var store = new Stub { Write = new OrderWriteResult(OrderWriteOutcome.Saved, 31) };
        var service = new OrderService(store);

        await service.DecideAsync(4, 3, 31, new OrderDecisionInput("reject", "  Hết nguyên liệu\nxin lỗi  "), Ct);
        Assert.Equal(("reject", "Hết nguyên liệu\nxin lỗi"), (store.Decision, store.Reason));

        var emoji = string.Concat(Enumerable.Repeat("🎂", 500));
        Assert.IsType<SaveOrderResult.Saved>(await service.DecideAsync(4, 3, 31, new OrderDecisionInput("reject", emoji), Ct));
        Assert.Equal(emoji, store.Reason);
    }

    [Theory]
    [InlineData(null, null, "decision")]
    [InlineData("", null, "decision")]
    [InlineData("Confirm", null, "decision")]
    [InlineData(" reject", "Hết hàng", "decision")]
    [InlineData("approve", null, "decision")]
    [InlineData("reject", null, "reason")]
    [InlineData("reject", "   ", "reason")]
    [InlineData("reject", "501", "reason")]
    [InlineData("reject", "nul", "reason")]
    [InlineData("reject", "surrogate", "reason")]
    [InlineData("confirm", "Đồng ý", "reason")]
    public async Task Invalid_decision_does_not_reach_the_store(string? decision, string? reason, string field)
    {
        var store = new Stub();
        reason = reason switch
        {
            "501" => new string('a', 501),
            "nul" => "Hết\0hàng",
            "surrogate" => "Hết hàng \uD83C",
            _ => reason,
        };

        var failed = Assert.IsType<SaveOrderResult.ValidationFailed>(
            await new OrderService(store).DecideAsync(4, 3, 31, new OrderDecisionInput(decision, reason), Ct));

        Assert.Contains(field, failed.Errors.Keys);
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData(OrderWriteOutcome.WorkflowNotReady)]
    [InlineData(OrderWriteOutcome.OrderNotPending)]
    [InlineData(OrderWriteOutcome.OrderPickupPassed)]
    [InlineData(OrderWriteOutcome.RejectionReasonRequired)]
    [InlineData(OrderWriteOutcome.OrderNotFound)]
    public async Task Rejected_writes_are_not_read_back(OrderWriteOutcome outcome)
    {
        var store = new Stub { Write = new OrderWriteResult(outcome) };
        var service = new OrderService(store);

        Assert.Equal(outcome, Assert.IsType<SaveOrderResult.Rejected>(
            await service.DecideAsync(4, 3, 31, new OrderDecisionInput("reject", "Hết hàng"), Ct)).Reason);
        Assert.Equal(outcome, Assert.IsType<SaveOrderResult.Rejected>(await service.AcceptAsync(4, 7, 9, Ct)).Reason);
        Assert.Equal(0, store.Reads);
    }

    [Fact]
    public async Task Missing_id_or_unreadable_order_is_not_reported_as_success()
    {
        var missing = new Stub { Write = new OrderWriteResult(OrderWriteOutcome.Saved) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new OrderService(missing).AcceptAsync(4, 7, 9, Ct));

        var hidden = new Stub { Write = new OrderWriteResult(OrderWriteOutcome.Saved, 31), Read = OrderReadOutcome.OrderNotFound };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OrderService(hidden).DecideAsync(4, 3, 31, new OrderDecisionInput("confirm", null), Ct));
    }

    [Fact]
    public async Task Store_failures_propagate()
    {
        var store = new Stub { Throw = true };
        await Assert.ThrowsAsync<TimeoutException>(() => new OrderService(store).AcceptAsync(4, 7, 9, Ct));
    }

    private sealed class Stub : IOrderStore
    {
        public OrderWriteResult Write { get; init; } = new(OrderWriteOutcome.Saved, 31);
        public OrderReadOutcome Read { get; init; } = OrderReadOutcome.Found;
        public bool Throw { get; init; }
        public int Calls { get; private set; }
        public int Reads { get; private set; }
        public string? WriteScope { get; private set; }
        public string? ReadScope { get; private set; }
        public string? ListScope { get; private set; }
        public string? Decision { get; private set; }
        public string? Reason { get; private set; }
        public (string? Status, int Page, int PageSize) Filter { get; private set; }
        public CancellationToken Token { get; private set; }

        private Task<OrderWriteResult> Record(string scope, CancellationToken token)
        {
            Calls++;
            WriteScope = scope;
            Token = token;
            if (Throw) throw new TimeoutException("Lock wait timeout");
            return Task.FromResult(Write);
        }

        private Task<OrderReadResult> Find(string scope, long orderId)
        {
            Calls++;
            Reads++;
            ReadScope = scope;
            return Task.FromResult(new OrderReadResult(Read, Read == OrderReadOutcome.Found ? Order(orderId) : null));
        }

        private Task<OrderListResult> List(string scope, string? status, int page, int pageSize)
        {
            Calls++;
            ListScope = scope;
            Filter = (status, page, pageSize);
            return Task.FromResult(new OrderListResult(OrderReadOutcome.Found, new PagedResult<OrderDto>([Order()], page, pageSize, 1)));
        }

        public Task<OrderWriteResult> AcceptAsync(long customerId, long requestId, long quoteId, CancellationToken cancellationToken) =>
            Record($"accept:{customerId}:{requestId}:{quoteId}", cancellationToken);

        public Task<OrderWriteResult> DecideAsync(long actorUserId, long bakeryId, long orderId, string decision, string? reason, CancellationToken cancellationToken)
        {
            Decision = decision;
            Reason = reason;
            return Record($"decide:{actorUserId}:{bakeryId}:{orderId}", cancellationToken);
        }

        public Task<OrderReadResult> FindForCustomerAsync(long customerId, long orderId, CancellationToken cancellationToken) =>
            Find($"customer:{customerId}:{orderId}", orderId);
        public Task<OrderReadResult> FindForCustomerRequestAsync(long customerId, long requestId, CancellationToken cancellationToken) =>
            Find($"customer-request:{customerId}:{requestId}", 31);
        public Task<OrderReadResult> FindForBakeryAsync(long actorUserId, long bakeryId, long orderId, CancellationToken cancellationToken) =>
            Find($"bakery:{actorUserId}:{bakeryId}:{orderId}", orderId);
        public Task<OrderListResult> ListForCustomerAsync(long customerId, string? status, int page, int pageSize, CancellationToken cancellationToken) =>
            List($"customer:{customerId}", status, page, pageSize);
        public Task<OrderListResult> ListForBakeryAsync(long actorUserId, long bakeryId, string? status, int page, int pageSize, CancellationToken cancellationToken) =>
            List($"bakery:{actorUserId}:{bakeryId}", status, page, pageSize);
    }
}

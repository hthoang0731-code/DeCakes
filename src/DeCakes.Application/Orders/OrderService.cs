using System.Text;
using DeCakes.Application.Common;
using DeCakes.Application.DesignRequests;
using DeCakes.Domain.Orders;

namespace DeCakes.Application.Orders;

public interface IOrderService
{
    Task<ListOrdersResult> ListForCustomerAsync(
        long customerId, string? status, int? page, int? pageSize, CancellationToken cancellationToken);

    Task<OrderReadResult> GetForCustomerAsync(long customerId, long orderId, CancellationToken cancellationToken);

    Task<OrderReadResult> GetForCustomerRequestAsync(long customerId, long requestId, CancellationToken cancellationToken);

    Task<ListOrdersResult> ListForBakeryAsync(
        long actorUserId, long bakeryId, string? status, int? page, int? pageSize, CancellationToken cancellationToken);

    Task<OrderReadResult> GetForBakeryAsync(long actorUserId, long bakeryId, long orderId, CancellationToken cancellationToken);

    Task<SaveOrderResult> AcceptAsync(long customerId, long requestId, long quoteId, CancellationToken cancellationToken);

    Task<SaveOrderResult> DecideAsync(
        long actorUserId, long bakeryId, long orderId, OrderDecisionInput input, CancellationToken cancellationToken);
}

public abstract record ListOrdersResult
{
    public sealed record Ok(OrderListResult Result) : ListOrdersResult;

    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> Errors) : ListOrdersResult;
}

public abstract record SaveOrderResult
{
    /// <summary>Đơn vừa tạo hoặc vừa được quyết định, đọc lại sau CALL trong phạm vi của actor.</summary>
    public sealed record Saved(OrderDto Order) : SaveOrderResult;

    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> Errors) : SaveOrderResult;

    public sealed record Rejected(OrderWriteOutcome Reason) : SaveOrderResult;
}

public sealed class OrderService(IOrderStore store) : IOrderService
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public Task<ListOrdersResult> ListForCustomerAsync(
        long customerId, string? status, int? page, int? pageSize, CancellationToken cancellationToken) =>
        ListAsync(status, page, pageSize, (filter, number, size) =>
            store.ListForCustomerAsync(customerId, filter, number, size, cancellationToken));

    public Task<OrderReadResult> GetForCustomerAsync(long customerId, long orderId, CancellationToken cancellationToken) =>
        store.FindForCustomerAsync(customerId, orderId, cancellationToken);

    public Task<OrderReadResult> GetForCustomerRequestAsync(long customerId, long requestId, CancellationToken cancellationToken) =>
        store.FindForCustomerRequestAsync(customerId, requestId, cancellationToken);

    public Task<ListOrdersResult> ListForBakeryAsync(
        long actorUserId, long bakeryId, string? status, int? page, int? pageSize, CancellationToken cancellationToken) =>
        ListAsync(status, page, pageSize, (filter, number, size) =>
            store.ListForBakeryAsync(actorUserId, bakeryId, filter, number, size, cancellationToken));

    public Task<OrderReadResult> GetForBakeryAsync(long actorUserId, long bakeryId, long orderId, CancellationToken cancellationToken) =>
        store.FindForBakeryAsync(actorUserId, bakeryId, orderId, cancellationToken);

    public async Task<SaveOrderResult> AcceptAsync(long customerId, long requestId, long quoteId, CancellationToken cancellationToken) =>
        await ReadBackAsync(await store.AcceptAsync(customerId, requestId, quoteId, cancellationToken),
            id => store.FindForCustomerAsync(customerId, id, cancellationToken));

    public async Task<SaveOrderResult> DecideAsync(
        long actorUserId, long bakeryId, long orderId, OrderDecisionInput input, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var reason = Reason(input.Reason, errors);
        switch (input.Decision)
        {
            case OrderDecisions.Confirm when reason is not null:
                errors["reason"] = ["Chỉ gửi lý do khi từ chối đơn."];
                break;
            case OrderDecisions.Reject when reason is null:
                errors.TryAdd("reason", ["Lý do từ chối là bắt buộc."]);
                break;
            case OrderDecisions.Confirm or OrderDecisions.Reject:
                break;
            default:
                errors["decision"] = ["decision phải là confirm hoặc reject."];
                break;
        }

        if (errors.Count > 0) return new SaveOrderResult.ValidationFailed(errors);

        var result = await store.DecideAsync(actorUserId, bakeryId, orderId, input.Decision!, reason, cancellationToken);
        return await ReadBackAsync(result, id => store.FindForBakeryAsync(actorUserId, bakeryId, id, cancellationToken));
    }

    // Trim, chuẩn hóa NFC, tối đa 500 ký tự Unicode như VARCHAR(500) utf8mb4; cho phép xuống dòng/tab. Rỗng thành null.
    private static string? Reason(string? raw, Dictionary<string, string[]> errors)
    {
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value)) return null;

        try
        {
            // UTF-16 lỗi (surrogate lẻ) sẽ bị driver thay bằng U+FFFD: từ chối thay vì lưu sai.
            value = value.Normalize(NormalizationForm.FormC);
            StrictUtf8.GetByteCount(value);
        }
        catch (ArgumentException)
        {
            errors["reason"] = ["Lý do chứa ký tự Unicode không hợp lệ."];
            return null;
        }

        if (value.EnumerateRunes().Count() > OrderDecisions.RejectionReasonMaxLength
            || value.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
        {
            errors["reason"] = [$"Lý do tối đa {OrderDecisions.RejectionReasonMaxLength} ký tự, không chứa ký tự điều khiển ngoài xuống dòng và tab."];
            return null;
        }

        return value;
    }

    // Procedure không trả result set: đọc lại đơn theo đúng phạm vi của actor; không đọc lại được thì không coi là thành công.
    private static async Task<SaveOrderResult> ReadBackAsync(OrderWriteResult result, Func<long, Task<OrderReadResult>> read)
    {
        if (result.Outcome != OrderWriteOutcome.Saved) return new SaveOrderResult.Rejected(result.Outcome);

        var id = result.OrderId ?? throw new InvalidOperationException("Store không trả id đơn đã ghi.");
        return await read(id) is { Outcome: OrderReadOutcome.Found, Order: { } order }
            ? new SaveOrderResult.Saved(order)
            : throw new InvalidOperationException("Không đọc lại được đơn vừa ghi.");
    }

    private static async Task<ListOrdersResult> ListAsync(
        string? status, int? page, int? pageSize, Func<string?, int, int, Task<OrderListResult>> query)
    {
        var errors = new Dictionary<string, string[]>();
        var filter = string.IsNullOrWhiteSpace(status) ? null : status.Trim();
        if (filter is not null && !OrderStatuses.IsKnown(filter))
        {
            errors["status"] = ["Trạng thái phải là pending_confirmation, confirmed, rejected_by_bakery, ready, picked_up hoặc cancelled."];
        }

        var (number, size) = Paging.Normalize(page, pageSize, errors);
        if (errors.Count > 0) return new ListOrdersResult.ValidationFailed(errors);

        return new ListOrdersResult.Ok(await query(filter, number, size));
    }
}

using System.Data;
using DeCakes.Application.DesignRequests;
using DeCakes.Application.Orders;
using DeCakes.Infrastructure.Persistence;
using MySqlConnector;

namespace DeCakes.Infrastructure.Orders;

public sealed class MySqlOrderStore(IMySqlConnectionFactory connectionFactory) : IOrderStore
{
    // Version của migrations/V006__order_decision_time_rules.sql: procedure accept/decide kiểm tra giờ nhận và lý do từ chối.
    private const int OrderWorkflowVersion = 6;

    private const string ActiveCustomerUser =
        "EXISTS (SELECT 1 FROM users u WHERE u.id = @actorId AND u.status = 'active' AND u.system_role = 'customer')";

    private const string ActiveMember =
        """
        EXISTS (SELECT 1 FROM bakery_members bm JOIN users u ON u.id = bm.user_id
                 WHERE bm.bakery_id = @bakeryId AND bm.user_id = @actorId
                   AND bm.status = 'active' AND u.status = 'active' AND u.system_role = 'bakery')
        """;

    private const string Columns =
        """
        o.id, o.design_request_id, o.quote_id, o.bakery_id, b.name AS bakery_name, o.status,
        o.agreed_quote_revision_no, o.agreed_price, o.currency, o.agreed_pickup_at, o.agreed_size_description,
        o.agreed_servings, o.agreed_flavor, o.agreed_decoration_description, o.agreed_inscription_text, o.agreed_note,
        o.agreed_design_asset_id, o.agreed_design_asset_kind, o.agreed_design_version_no,
        o.bakery_decided_at, o.rejection_reason, o.picked_up_at, o.cancelled_at, o.created_at, o.updated_at
        """;

    private const string FromOrders = "FROM orders o JOIN bakeries b ON b.id = o.bakery_id";

    // Chỉ hai mệnh đề cố định của server; giá trị từ request luôn đi qua parameter.
    private const string CustomerScope = $"o.customer_id = @actorId AND {ActiveCustomerUser}";
    private const string BakeryScope = $"o.bakery_id = @bakeryId AND {ActiveMember}";

    public async Task<OrderListResult> ListForCustomerAsync(
        long customerId, string? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var data = await ListAsync(connection, CustomerScope, customerId, null, status, page, pageSize, cancellationToken);
        return new OrderListResult(OrderReadOutcome.Found, data);
    }

    public async Task<OrderReadResult> FindForCustomerAsync(long customerId, long orderId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        return Found(await FindAsync(connection, $"o.id = @orderId AND {CustomerScope}", customerId, null,
            ("@orderId", orderId), cancellationToken));
    }

    public async Task<OrderReadResult> FindForCustomerRequestAsync(long customerId, long requestId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        if (!await OwnsRequestAsync(connection, customerId, requestId, cancellationToken))
            return new OrderReadResult(OrderReadOutcome.RequestNotFound);

        return Found(await FindAsync(connection, $"o.design_request_id = @requestId AND {CustomerScope}", customerId, null,
            ("@requestId", requestId), cancellationToken));
    }

    public async Task<OrderListResult> ListForBakeryAsync(
        long actorUserId, long bakeryId, string? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        if (!await IsActiveMemberAsync(connection, actorUserId, bakeryId, cancellationToken))
            return new OrderListResult(OrderReadOutcome.BakeryNotFound);

        var data = await ListAsync(connection, BakeryScope, actorUserId, bakeryId, status, page, pageSize, cancellationToken);
        return new OrderListResult(OrderReadOutcome.Found, data);
    }

    public async Task<OrderReadResult> FindForBakeryAsync(long actorUserId, long bakeryId, long orderId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        if (!await IsActiveMemberAsync(connection, actorUserId, bakeryId, cancellationToken))
            return new OrderReadResult(OrderReadOutcome.BakeryNotFound);

        return Found(await FindAsync(connection, $"o.id = @orderId AND {BakeryScope}", actorUserId, bakeryId,
            ("@orderId", orderId), cancellationToken));
    }

    public async Task<OrderWriteResult> AcceptAsync(long customerId, long requestId, long quoteId, CancellationToken cancellationToken)
    {
        // Procedure tự START TRANSACTION/COMMIT: connection riêng cho thao tác này, không có transaction ứng dụng.
        // Không retry: nếu kết nối đứt khi CALL, đơn có thể đã được tạo; client đọc GET /api/design-requests/{id}/order.
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        if (!await WorkflowReadyAsync(connection, cancellationToken)) return new OrderWriteResult(OrderWriteOutcome.WorkflowNotReady);

        // Phạm vi route trước CALL: procedure chỉ suy ra yêu cầu từ báo giá. design_request_id của báo giá không đổi;
        // quyền chủ yêu cầu và trạng thái được procedure kiểm tra lại dưới khóa.
        if (!await OwnsRequestAsync(connection, customerId, requestId, cancellationToken))
            return new OrderWriteResult(OrderWriteOutcome.RequestNotFound);

        await using (var quote = new MySqlCommand("SELECT 1 FROM quotes WHERE id = @quoteId AND design_request_id = @requestId", connection))
        {
            quote.Parameters.Add("@quoteId", MySqlDbType.Int64).Value = quoteId;
            quote.Parameters.Add("@requestId", MySqlDbType.Int64).Value = requestId;
            if (await quote.ExecuteScalarAsync(cancellationToken) is null) return new OrderWriteResult(OrderWriteOutcome.QuoteNotFound);
        }

        await using var command = Procedure(connection, "sp_accept_quote");
        command.Parameters.Add("p_quote_id", MySqlDbType.Int64).Value = quoteId;
        command.Parameters.Add("p_customer_id", MySqlDbType.Int64).Value = customerId;
        var output = command.Parameters.Add("p_order_id", MySqlDbType.Int64);
        output.Direction = ParameterDirection.Output;
        var result = await CallAsync(command, OrderProcedureErrors.Accept, cancellationToken);
        if (result != OrderWriteOutcome.Saved) return new OrderWriteResult(result);

        // Thành công thì OUT là BIGINT; NULL hoặc kiểu khác là trạng thái không hợp lệ, không đoán id.
        return output.Value is long id && id > 0
            ? new OrderWriteResult(OrderWriteOutcome.Saved, id)
            : throw new InvalidOperationException("OUT p_order_id không phải BIGINT hợp lệ.");
    }

    public async Task<OrderWriteResult> DecideAsync(
        long actorUserId, long bakeryId, long orderId, string decision, string? reason, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        if (!await WorkflowReadyAsync(connection, cancellationToken)) return new OrderWriteResult(OrderWriteOutcome.WorkflowNotReady);

        // Procedure suy ra tiệm từ đơn: một user thuộc cả tiệm A và B có thể quyết định đơn của B qua route A nếu không chặn ở đây.
        // bakery_id của đơn không đổi; membership được procedure kiểm tra lại dưới khóa.
        if (!await IsActiveMemberAsync(connection, actorUserId, bakeryId, cancellationToken))
            return new OrderWriteResult(OrderWriteOutcome.BakeryNotFound);

        await using (var order = new MySqlCommand("SELECT 1 FROM orders WHERE id = @orderId AND bakery_id = @bakeryId", connection))
        {
            order.Parameters.Add("@orderId", MySqlDbType.Int64).Value = orderId;
            order.Parameters.Add("@bakeryId", MySqlDbType.Int64).Value = bakeryId;
            if (await order.ExecuteScalarAsync(cancellationToken) is null) return new OrderWriteResult(OrderWriteOutcome.OrderNotFound);
        }

        await using var command = Procedure(connection, "sp_bakery_decide_order");
        command.Parameters.Add("p_order_id", MySqlDbType.Int64).Value = orderId;
        command.Parameters.Add("p_actor_user_id", MySqlDbType.Int64).Value = actorUserId;
        command.Parameters.Add("p_decision", MySqlDbType.VarChar).Value = decision;
        command.Parameters.Add("p_reason", MySqlDbType.VarChar).Value = (object?)reason ?? DBNull.Value;
        var result = await CallAsync(command, OrderProcedureErrors.Decide, cancellationToken);
        return new OrderWriteResult(result, result == OrderWriteOutcome.Saved ? orderId : null);
    }

    // Kiểm tra lúc thao tác (không lúc khởi động/health): chưa có V006 thì procedure cũ thiếu kiểm tra giờ nhận/lý do, không gọi.
    private static async Task<bool> WorkflowReadyAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand("SELECT 1 FROM schema_migrations WHERE version = @version", connection);
        command.Parameters.Add("@version", MySqlDbType.Int32).Value = OrderWorkflowVersion;
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static MySqlCommand Procedure(MySqlConnection connection, string name) =>
        new(name, connection) { CommandType = CommandType.StoredProcedure };

    private static async Task<OrderWriteOutcome> CallAsync(
        MySqlCommand command, IReadOnlyDictionary<string, OrderWriteOutcome> allowlist, CancellationToken cancellationToken)
    {
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
            return OrderWriteOutcome.Saved;
        }
        catch (MySqlException ex) when (OrderProcedureErrors.TryMap(allowlist, ex.SqlState, ex.Message, out var outcome))
        {
            return outcome;
        }
    }

    private static async Task<bool> OwnsRequestAsync(
        MySqlConnection connection, long customerId, long requestId, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            $"SELECT 1 FROM design_requests WHERE id = @requestId AND customer_id = @actorId AND {ActiveCustomerUser}", connection);
        command.Parameters.Add("@requestId", MySqlDbType.Int64).Value = requestId;
        command.Parameters.Add("@actorId", MySqlDbType.Int64).Value = customerId;
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> IsActiveMemberAsync(
        MySqlConnection connection, long actorUserId, long bakeryId, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand($"SELECT 1 WHERE {ActiveMember}", connection);
        AddScope(command, actorUserId, bakeryId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<OrderDto?> FindAsync(
        MySqlConnection connection, string where, long actorUserId, long? bakeryId, (string Name, long Value) key,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand($"SELECT {Columns} {FromOrders} WHERE {where}", connection);
        AddScope(command, actorUserId, bakeryId);
        command.Parameters.Add(key.Name, MySqlDbType.Int64).Value = key.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    private static async Task<PagedResult<OrderDto>> ListAsync(
        MySqlConnection connection, string scope, long actorUserId, long? bakeryId, string? status, int page, int pageSize,
        CancellationToken cancellationToken)
    {
        var where = scope + (status is null ? "" : " AND o.status = @status");
        long total;
        await using (var count = new MySqlCommand($"SELECT COUNT(*) {FromOrders} WHERE {where}", connection))
        {
            AddScope(count, actorUserId, bakeryId);
            if (status is not null) count.Parameters.Add("@status", MySqlDbType.VarChar).Value = status;
            total = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken));
        }

        var items = new List<OrderDto>();
        await using (var command = new MySqlCommand(
                         $"SELECT {Columns} {FromOrders} WHERE {where} ORDER BY o.created_at DESC, o.id DESC LIMIT @limit OFFSET @offset", connection))
        {
            AddScope(command, actorUserId, bakeryId);
            if (status is not null) command.Parameters.Add("@status", MySqlDbType.VarChar).Value = status;
            command.Parameters.Add("@limit", MySqlDbType.Int32).Value = pageSize;
            command.Parameters.Add("@offset", MySqlDbType.Int64).Value = (long)(page - 1) * pageSize;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) items.Add(Map(reader));
        }

        return new PagedResult<OrderDto>(items, page, pageSize, total);
    }

    private static OrderReadResult Found(OrderDto? order) => order is null
        ? new OrderReadResult(OrderReadOutcome.OrderNotFound)
        : new OrderReadResult(OrderReadOutcome.Found, order);

    private static void AddScope(MySqlCommand command, long actorUserId, long? bakeryId)
    {
        command.Parameters.Add("@actorId", MySqlDbType.Int64).Value = actorUserId;
        if (bakeryId.HasValue) command.Parameters.Add("@bakeryId", MySqlDbType.Int64).Value = bakeryId.Value;
    }

    private static OrderDto Map(MySqlDataReader reader) => new(
        reader.GetInt64("id"), reader.GetInt64("design_request_id"), reader.GetInt64("quote_id"), reader.GetInt64("bakery_id"),
        reader.GetString("bakery_name"), reader.GetString("status"), reader.GetInt64("agreed_quote_revision_no"),
        reader.GetDecimal("agreed_price"), reader.GetString("currency"), reader.GetDateTime("agreed_pickup_at"),
        NullableString(reader, "agreed_size_description"),
        reader.IsDBNull(reader.GetOrdinal("agreed_servings")) ? null : reader.GetInt32("agreed_servings"),
        reader.GetString("agreed_flavor"), reader.GetString("agreed_decoration_description"),
        NullableString(reader, "agreed_inscription_text"), NullableString(reader, "agreed_note"),
        NullableInt64(reader, "agreed_design_asset_id"), NullableString(reader, "agreed_design_asset_kind"),
        NullableInt64(reader, "agreed_design_version_no"), NullableDateTime(reader, "bakery_decided_at"),
        NullableString(reader, "rejection_reason"), NullableDateTime(reader, "picked_up_at"), NullableDateTime(reader, "cancelled_at"),
        reader.GetDateTime("created_at"), reader.GetDateTime("updated_at"));

    private static string? NullableString(MySqlDataReader reader, string name) =>
        reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetString(name);

    private static long? NullableInt64(MySqlDataReader reader, string name) =>
        reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetInt64(name);

    private static DateTime? NullableDateTime(MySqlDataReader reader, string name) =>
        reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetDateTime(name);
}

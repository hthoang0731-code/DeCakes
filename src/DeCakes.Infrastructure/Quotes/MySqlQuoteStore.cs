using DeCakes.Application.DesignRequests;
using DeCakes.Application.Quotes;
using DeCakes.Infrastructure.Persistence;
using MySqlConnector;

namespace DeCakes.Infrastructure.Quotes;

public sealed class MySqlQuoteStore(IMySqlConnectionFactory connectionFactory) : IQuoteStore
{
    private const string ActiveMember =
        """
        EXISTS (SELECT 1 FROM bakery_members bm JOIN users u ON u.id = bm.user_id
                 WHERE bm.bakery_id = @bakeryId AND bm.user_id = @actorId
                   AND bm.status = 'active' AND u.status = 'active' AND u.system_role = 'bakery')
        """;

    private const string ActiveCustomer =
        """
        EXISTS (SELECT 1 FROM design_requests dr JOIN users u ON u.id = dr.customer_id
                 WHERE dr.id = @requestId AND dr.customer_id = @actorId
                   AND u.status = 'active' AND u.system_role = 'customer')
        """;

    private const string Columns =
        """
        q.id, q.design_request_id, q.bakery_id, b.name AS bakery_name, q.revision_no, q.status,
        q.price, q.currency, q.size_description, q.servings, q.flavor, q.decoration_description,
        q.inscription_text, q.note, q.estimated_pickup_at, q.valid_until, q.confirmed_design_asset_id,
        q.withdrawn_at, q.created_at, q.updated_at
        """;

    private const string FromQuotes = "FROM quotes q JOIN bakeries b ON b.id = q.bakery_id";

    public async Task<QuoteListResult> ListForBakeryAsync(
        long actorUserId, long bakeryId, long requestId, string? status, int page, int pageSize,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var access = await BakeryAccessAsync(connection, actorUserId, bakeryId, requestId, cancellationToken);
        if (access != QuoteReadOutcome.Found) return new QuoteListResult(access);

        var data = await ListAsync(connection, actorUserId, bakeryId, requestId, status, page, pageSize, cancellationToken);
        return new QuoteListResult(QuoteReadOutcome.Found, data);
    }

    public async Task<QuoteReadResult> FindForBakeryAsync(
        long actorUserId, long bakeryId, long requestId, long quoteId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var access = await BakeryAccessAsync(connection, actorUserId, bakeryId, requestId, cancellationToken);
        if (access != QuoteReadOutcome.Found) return new QuoteReadResult(access);

        var quote = await FindAsync(connection, actorUserId, bakeryId, requestId, quoteId, cancellationToken);
        return quote is null
            ? new QuoteReadResult(QuoteReadOutcome.QuoteNotFound)
            : new QuoteReadResult(QuoteReadOutcome.Found, quote);
    }

    public async Task<QuoteListResult> ListForCustomerAsync(
        long customerId, long requestId, string? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        if (!await CustomerAccessAsync(connection, customerId, requestId, cancellationToken))
            return new QuoteListResult(QuoteReadOutcome.RequestNotFound);

        var data = await ListAsync(connection, customerId, null, requestId, status, page, pageSize, cancellationToken);
        return new QuoteListResult(QuoteReadOutcome.Found, data);
    }

    public async Task<QuoteReadResult> FindForCustomerAsync(
        long customerId, long requestId, long quoteId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        if (!await CustomerAccessAsync(connection, customerId, requestId, cancellationToken))
            return new QuoteReadResult(QuoteReadOutcome.RequestNotFound);

        var quote = await FindAsync(connection, customerId, null, requestId, quoteId, cancellationToken);
        return quote is null
            ? new QuoteReadResult(QuoteReadOutcome.QuoteNotFound)
            : new QuoteReadResult(QuoteReadOutcome.Found, quote);
    }

    private static async Task<QuoteReadOutcome> BakeryAccessAsync(
        MySqlConnection connection, long actorUserId, long bakeryId, long requestId, CancellationToken cancellationToken)
    {
        await using (var member = new MySqlCommand($"SELECT 1 WHERE {ActiveMember}", connection))
        {
            member.Parameters.AddWithValue("@actorId", actorUserId);
            member.Parameters.AddWithValue("@bakeryId", bakeryId);
            if (await member.ExecuteScalarAsync(cancellationToken) is null) return QuoteReadOutcome.BakeryNotFound;
        }

        await using var request = new MySqlCommand(
            $"SELECT 1 FROM request_bakeries WHERE bakery_id = @bakeryId AND design_request_id = @requestId AND {ActiveMember}", connection);
        AddScope(request, actorUserId, bakeryId, requestId);
        return await request.ExecuteScalarAsync(cancellationToken) is null
            ? QuoteReadOutcome.RequestNotFound
            : QuoteReadOutcome.Found;
    }

    private static async Task<bool> CustomerAccessAsync(
        MySqlConnection connection, long customerId, long requestId, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand($"SELECT 1 WHERE {ActiveCustomer}", connection);
        AddScope(command, customerId, null, requestId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    // Chỉ lựa chọn hai mệnh đề cố định của server; giá trị từ request luôn đi qua parameter.
    private static string Where(long? bakeryId) => bakeryId.HasValue
        ? $"q.design_request_id = @requestId AND q.bakery_id = @bakeryId AND {ActiveMember}"
        : $"q.design_request_id = @requestId AND {ActiveCustomer}";

    private static async Task<QuoteDto?> FindAsync(
        MySqlConnection connection, long actorUserId, long? bakeryId, long requestId, long quoteId,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand($"SELECT {Columns} {FromQuotes} WHERE q.id = @quoteId AND {Where(bakeryId)}", connection);
        AddScope(command, actorUserId, bakeryId, requestId);
        command.Parameters.Add("@quoteId", MySqlDbType.Int64).Value = quoteId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    private static async Task<PagedResult<QuoteDto>> ListAsync(
        MySqlConnection connection, long actorUserId, long? bakeryId, long requestId,
        string? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        var where = Where(bakeryId) + (status is null ? "" : " AND q.status = @status");
        long total;
        await using (var count = new MySqlCommand($"SELECT COUNT(*) {FromQuotes} WHERE {where}", connection))
        {
            AddScope(count, actorUserId, bakeryId, requestId);
            if (status is not null) count.Parameters.Add("@status", MySqlDbType.VarChar).Value = status;
            total = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken));
        }

        var items = new List<QuoteDto>();
        await using (var command = new MySqlCommand(
                         $"SELECT {Columns} {FromQuotes} WHERE {where} ORDER BY q.created_at DESC, q.id DESC LIMIT @limit OFFSET @offset", connection))
        {
            AddScope(command, actorUserId, bakeryId, requestId);
            if (status is not null) command.Parameters.Add("@status", MySqlDbType.VarChar).Value = status;
            command.Parameters.Add("@limit", MySqlDbType.Int32).Value = pageSize;
            command.Parameters.Add("@offset", MySqlDbType.Int64).Value = (long)(page - 1) * pageSize;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) items.Add(Map(reader));
        }

        return new PagedResult<QuoteDto>(items, page, pageSize, total);
    }

    private static void AddScope(MySqlCommand command, long actorUserId, long? bakeryId, long requestId)
    {
        command.Parameters.Add("@actorId", MySqlDbType.Int64).Value = actorUserId;
        command.Parameters.Add("@requestId", MySqlDbType.Int64).Value = requestId;
        if (bakeryId.HasValue) command.Parameters.Add("@bakeryId", MySqlDbType.Int64).Value = bakeryId.Value;
    }

    private static QuoteDto Map(MySqlDataReader reader) => new(
        reader.GetInt64("id"), reader.GetInt64("design_request_id"), reader.GetInt64("bakery_id"),
        reader.GetString("bakery_name"), reader.GetInt64("revision_no"), reader.GetString("status"),
        reader.GetDecimal("price"), reader.GetString("currency"), NullableString(reader, "size_description"),
        reader.IsDBNull(reader.GetOrdinal("servings")) ? null : reader.GetInt32("servings"),
        reader.GetString("flavor"), reader.GetString("decoration_description"), NullableString(reader, "inscription_text"),
        NullableString(reader, "note"), reader.GetDateTime("estimated_pickup_at"), reader.GetDateTime("valid_until"),
        reader.IsDBNull(reader.GetOrdinal("confirmed_design_asset_id")) ? null : reader.GetInt64("confirmed_design_asset_id"),
        reader.IsDBNull(reader.GetOrdinal("withdrawn_at")) ? null : reader.GetDateTime("withdrawn_at"),
        reader.GetDateTime("created_at"), reader.GetDateTime("updated_at"));

    private static string? NullableString(MySqlDataReader reader, string name) =>
        reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetString(name);
}

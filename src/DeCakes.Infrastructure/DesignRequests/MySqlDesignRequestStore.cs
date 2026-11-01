using DeCakes.Application.DesignRequests;
using DeCakes.Domain.DesignRequests;
using DeCakes.Infrastructure.Persistence;
using MySqlConnector;

namespace DeCakes.Infrastructure.DesignRequests;

public sealed class MySqlDesignRequestStore(IMySqlConnectionFactory connectionFactory) : IDesignRequestStore
{
    private const string DetailColumns =
        "id, status, title, idea_description, budget_min, budget_max, currency, size_description, servings, flavor, " +
        "inscription_text, pickup_at, inspired_by_cake_id, created_at, updated_at";

    private const string SummaryColumns = "id, status, title, budget_min, budget_max, currency, pickup_at, created_at, updated_at";

    public async Task<long> CreateDraftAsync(long customerId, DesignRequestValues values, CancellationToken cancellationToken)
    {
        // Trạng thái, tiền tệ và is_demo cố định ở server. created_at/updated_at theo DEFAULT UTC_TIMESTAMP(3).
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new MySqlCommand(
            """
            INSERT INTO design_requests
              (customer_id, inspired_by_cake_id, title, idea_description, budget_min, budget_max, currency,
               size_description, servings, flavor, inscription_text, pickup_at, status, is_demo)
            VALUES
              (@customerId, @cakeId, @title, @idea, @budgetMin, @budgetMax, 'VND',
               @size, @servings, @flavor, @inscription, @pickupAt, 'draft', 0)
            """,
            connection);
        command.Parameters.Add("@customerId", MySqlDbType.Int64).Value = customerId;
        AddContent(command, values);

        await command.ExecuteNonQueryAsync(cancellationToken);
        return command.LastInsertedId;
    }

    public async Task<DesignRequestDto?> FindForCustomerAsync(long requestId, long customerId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new MySqlCommand(
            $"SELECT {DetailColumns} FROM design_requests WHERE id = @id AND customer_id = @customerId", connection);
        command.Parameters.Add("@id", MySqlDbType.Int64).Value = requestId;
        command.Parameters.Add("@customerId", MySqlDbType.Int64).Value = customerId;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new DesignRequestDto(
            reader.GetInt64("id"),
            reader.GetString("status"),
            reader.GetString("title"),
            reader.GetString("idea_description"),
            NullableDecimal(reader, "budget_min"),
            reader.GetDecimal("budget_max"),
            reader.GetString("currency"),
            NullableString(reader, "size_description"),
            NullableInt(reader, "servings"),
            NullableString(reader, "flavor"),
            NullableString(reader, "inscription_text"),
            reader.GetDateTime("pickup_at"),
            reader.IsDBNull(reader.GetOrdinal("inspired_by_cake_id")) ? null : reader.GetInt64("inspired_by_cake_id"),
            reader.GetDateTime("created_at"),
            reader.GetDateTime("updated_at"));
    }

    public async Task<PagedResult<DesignRequestSummaryDto>> ListForCustomerAsync(
        long customerId, string? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        // Điều kiện trạng thái là đoạn SQL cố định; giá trị luôn đi qua tham số.
        var where = status is null ? "customer_id = @customerId" : "customer_id = @customerId AND status = @status";

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        long total;
        await using (var count = new MySqlCommand($"SELECT COUNT(*) FROM design_requests WHERE {where}", connection))
        {
            AddFilter(count, customerId, status);
            total = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken));
        }

        var items = new List<DesignRequestSummaryDto>();
        await using (var list = new MySqlCommand(
                         $"SELECT {SummaryColumns} FROM design_requests WHERE {where} " +
                         "ORDER BY created_at DESC, id DESC LIMIT @limit OFFSET @offset",
                         connection))
        {
            AddFilter(list, customerId, status);
            list.Parameters.Add("@limit", MySqlDbType.Int32).Value = pageSize;
            list.Parameters.Add("@offset", MySqlDbType.Int64).Value = (long)(page - 1) * pageSize;

            await using var reader = await list.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new DesignRequestSummaryDto(
                    reader.GetInt64("id"),
                    reader.GetString("status"),
                    reader.GetString("title"),
                    NullableDecimal(reader, "budget_min"),
                    reader.GetDecimal("budget_max"),
                    reader.GetString("currency"),
                    reader.GetDateTime("pickup_at"),
                    reader.GetDateTime("created_at"),
                    reader.GetDateTime("updated_at")));
            }
        }

        return new PagedResult<DesignRequestSummaryDto>(items, page, pageSize, total);
    }

    public async Task<UpdateDraftOutcome> UpdateDraftAsync(
        long requestId, long customerId, DesignRequestValues values, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        // Một câu UPDATE kiểm tra chủ sở hữu và trạng thái draft rồi ghi, dưới khóa dòng của InnoDB.
        // Nếu sp_send_request_to_bakery đã chuyển yêu cầu sang open (kể cả khi đang chờ khóa), điều kiện draft
        // được đánh giá lại trên bản đã commit và câu lệnh không ghi gì.
        await using (var update = new MySqlCommand(
                         """
                         UPDATE design_requests
                            SET inspired_by_cake_id = @cakeId, title = @title, idea_description = @idea,
                                budget_min = @budgetMin, budget_max = @budgetMax, size_description = @size,
                                servings = @servings, flavor = @flavor, inscription_text = @inscription,
                                pickup_at = @pickupAt, updated_at = UTC_TIMESTAMP(3)
                          WHERE id = @id AND customer_id = @customerId AND status = 'draft'
                         """,
                         connection))
        {
            update.Parameters.Add("@id", MySqlDbType.Int64).Value = requestId;
            update.Parameters.Add("@customerId", MySqlDbType.Int64).Value = customerId;
            AddContent(update, values);
            if (await update.ExecuteNonQueryAsync(cancellationToken) > 0)
            {
                return UpdateDraftOutcome.Updated;
            }
        }

        // 0 dòng: phân biệt không tồn tại/của khách khác, không còn draft, hay dữ liệu không đổi
        // (khi server đếm "dòng thay đổi" thay vì "dòng khớp").
        await using var check = new MySqlCommand(
            "SELECT status FROM design_requests WHERE id = @id AND customer_id = @customerId", connection);
        check.Parameters.Add("@id", MySqlDbType.Int64).Value = requestId;
        check.Parameters.Add("@customerId", MySqlDbType.Int64).Value = customerId;
        return await check.ExecuteScalarAsync(cancellationToken) switch
        {
            null => UpdateDraftOutcome.NotFound,
            string s when s == DesignRequestStatuses.Draft => UpdateDraftOutcome.Updated,
            _ => UpdateDraftOutcome.NotDraft,
        };
    }

    public async Task<bool> IsCakeAvailableForInspirationAsync(long cakeId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new MySqlCommand(
            """
            SELECT 1
              FROM cakes c
              JOIN bakeries b ON b.id = c.bakery_id
             WHERE c.id = @cakeId AND c.visibility = 'published' AND b.profile_status = 'active'
             LIMIT 1
            """,
            connection);
        command.Parameters.Add("@cakeId", MySqlDbType.Int64).Value = cakeId;
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<SendToBakeryOutcome> SendToBakeryAsync(
        long requestId, long customerId, long bakeryId, CancellationToken cancellationToken)
    {
        // Procedure tự START TRANSACTION/COMMIT: gọi trên connection riêng, không có transaction ứng dụng.
        // Không retry: nếu kết nối đứt giữa chừng, không biết procedure đã commit hay chưa.
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new MySqlCommand("CALL sp_send_request_to_bakery(@requestId, @customerId, @bakeryId)", connection);
        command.Parameters.Add("@requestId", MySqlDbType.Int64).Value = requestId;
        command.Parameters.Add("@customerId", MySqlDbType.Int64).Value = customerId;
        command.Parameters.Add("@bakeryId", MySqlDbType.Int64).Value = bakeryId;

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
            return SendToBakeryOutcome.Sent;
        }
        catch (MySqlException ex) when (SendRequestProcedureErrors.TryMap(ex.SqlState, ex.Message, out var outcome))
        {
            return outcome;
        }
    }

    public async Task<IReadOnlyList<RequestBakeryDto>?> ListBakeriesAsync(
        long requestId, long customerId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using (var owner = new MySqlCommand(
                         "SELECT 1 FROM design_requests WHERE id = @id AND customer_id = @customerId", connection))
        {
            owner.Parameters.Add("@id", MySqlDbType.Int64).Value = requestId;
            owner.Parameters.Add("@customerId", MySqlDbType.Int64).Value = customerId;
            if (await owner.ExecuteScalarAsync(cancellationToken) is null)
            {
                return null;
            }
        }

        await using var command = new MySqlCommand(
            """
            SELECT rb.bakery_id, b.name, rb.status, rb.decline_reason, rb.sent_at, rb.responded_at
              FROM request_bakeries rb
              JOIN bakeries b ON b.id = rb.bakery_id
             WHERE rb.design_request_id = @id
             ORDER BY rb.sent_at, rb.id
            """,
            connection);
        command.Parameters.Add("@id", MySqlDbType.Int64).Value = requestId;

        var result = new List<RequestBakeryDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new RequestBakeryDto(
                reader.GetInt64("bakery_id"),
                reader.GetString("name"),
                reader.GetString("status"),
                NullableString(reader, "decline_reason"),
                reader.GetDateTime("sent_at"),
                reader.IsDBNull(reader.GetOrdinal("responded_at")) ? null : reader.GetDateTime("responded_at")));
        }

        return result;
    }

    private static void AddContent(MySqlCommand command, DesignRequestValues values)
    {
        command.Parameters.Add("@cakeId", MySqlDbType.Int64).Value = (object?)values.InspiredByCakeId ?? DBNull.Value;
        command.Parameters.Add("@title", MySqlDbType.VarChar).Value = values.Title;
        command.Parameters.Add("@idea", MySqlDbType.Text).Value = values.IdeaDescription;
        command.Parameters.Add("@budgetMin", MySqlDbType.NewDecimal).Value = (object?)values.BudgetMin ?? DBNull.Value;
        command.Parameters.Add("@budgetMax", MySqlDbType.NewDecimal).Value = values.BudgetMax;
        command.Parameters.Add("@size", MySqlDbType.VarChar).Value = (object?)values.SizeDescription ?? DBNull.Value;
        command.Parameters.Add("@servings", MySqlDbType.UInt16).Value = values.Servings is { } s ? (ushort)s : DBNull.Value;
        command.Parameters.Add("@flavor", MySqlDbType.VarChar).Value = (object?)values.Flavor ?? DBNull.Value;
        command.Parameters.Add("@inscription", MySqlDbType.VarChar).Value = (object?)values.InscriptionText ?? DBNull.Value;
        command.Parameters.Add("@pickupAt", MySqlDbType.DateTime).Value = values.PickupAtUtc;
    }

    private static void AddFilter(MySqlCommand command, long customerId, string? status)
    {
        command.Parameters.Add("@customerId", MySqlDbType.Int64).Value = customerId;
        if (status is not null)
        {
            command.Parameters.Add("@status", MySqlDbType.VarChar).Value = status;
        }
    }

    private static string? NullableString(MySqlDataReader reader, string column) =>
        reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetString(column);

    private static decimal? NullableDecimal(MySqlDataReader reader, string column) =>
        reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetDecimal(column);

    private static int? NullableInt(MySqlDataReader reader, string column) =>
        reader.IsDBNull(reader.GetOrdinal(column)) ? null : Convert.ToInt32(reader.GetValue(reader.GetOrdinal(column)));
}

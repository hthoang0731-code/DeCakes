using DeCakes.Application.BakeryWorkspace;
using DeCakes.Application.DesignRequests;
using DeCakes.Infrastructure.Persistence;
using MySqlConnector;

namespace DeCakes.Infrastructure.BakeryWorkspace;

/// <summary>
/// Inbox của tiệm, chỉ SELECT. Mọi truy vấn chứa điều kiện thành viên giống sp_guard_active_bakery_member
/// (user active, system_role 'bakery', bakery_members.status 'active'), nên bakeryId từ route không mở được dữ liệu
/// của tiệm khác dù truy vấn nào được gọi. Không SELECT cột nào của users ngoài điều kiện kiểm tra.
/// </summary>
public sealed class MySqlBakeryWorkspaceStore(IMySqlConnectionFactory connectionFactory) : IBakeryWorkspaceStore
{
    // Đoạn SQL cố định; @bakeryId và @userId luôn là tham số.
    private const string ActiveMember =
        """
        EXISTS (SELECT 1
                  FROM bakery_members bm
                  JOIN users u ON u.id = bm.user_id
                 WHERE bm.bakery_id = @bakeryId AND bm.user_id = @userId AND bm.status = 'active'
                   AND u.status = 'active' AND u.system_role = 'bakery')
        """;

    public async Task<IReadOnlyList<WorkspaceBakeryDto>> ListMemberBakeriesAsync(long userId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new MySqlCommand(
            """
            SELECT b.id, b.name, b.profile_status, bm.member_role
              FROM bakery_members bm
              JOIN bakeries b ON b.id = bm.bakery_id
              JOIN users u ON u.id = bm.user_id
             WHERE bm.user_id = @userId AND bm.status = 'active'
               AND u.status = 'active' AND u.system_role = 'bakery'
             ORDER BY b.name, b.id
            """,
            connection);
        command.Parameters.Add("@userId", MySqlDbType.Int64).Value = userId;

        var result = new List<WorkspaceBakeryDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new WorkspaceBakeryDto(
                reader.GetInt64("id"),
                reader.GetString("name"),
                reader.GetString("profile_status"),
                reader.GetString("member_role")));
        }

        return result;
    }

    public async Task<bool> IsActiveMemberAsync(long userId, long bakeryId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        return await IsActiveMemberAsync(connection, userId, bakeryId, cancellationToken);
    }

    public async Task<PagedResult<BakeryInboxItemDto>?> ListInboxAsync(
        long userId, long bakeryId, string? status, string? requestStatus, int page, int pageSize, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        // Phân biệt "không phải thành viên" (404) với inbox rỗng. COUNT và danh sách bên dưới vẫn tự kiểm tra lại
        // thành viên: nếu bị removed giữa chừng, kết quả rỗng thay vì lộ dữ liệu.
        if (!await IsActiveMemberAsync(connection, userId, bakeryId, cancellationToken))
        {
            return null;
        }

        var where = $"rb.bakery_id = @bakeryId AND {ActiveMember}";
        if (status is not null) where += " AND rb.status = @status";
        if (requestStatus is not null) where += " AND dr.status = @requestStatus";
        const string from = "FROM request_bakeries rb JOIN design_requests dr ON dr.id = rb.design_request_id";

        long total;
        await using (var count = new MySqlCommand($"SELECT COUNT(*) {from} WHERE {where}", connection))
        {
            AddFilter(count, userId, bakeryId, status, requestStatus);
            total = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken));
        }

        var items = new List<BakeryInboxItemDto>();
        await using (var list = new MySqlCommand(
                         "SELECT dr.id, dr.title, dr.budget_min, dr.budget_max, dr.currency, dr.pickup_at, " +
                         "dr.status AS request_status, rb.status AS send_status, rb.sent_at " +
                         $"{from} WHERE {where} ORDER BY rb.sent_at DESC, rb.id DESC LIMIT @limit OFFSET @offset",
                         connection))
        {
            AddFilter(list, userId, bakeryId, status, requestStatus);
            list.Parameters.Add("@limit", MySqlDbType.Int32).Value = pageSize;
            list.Parameters.Add("@offset", MySqlDbType.Int64).Value = (long)(page - 1) * pageSize;

            await using var reader = await list.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new BakeryInboxItemDto(
                    reader.GetInt64("id"),
                    reader.GetString("title"),
                    NullableDecimal(reader, "budget_min"),
                    reader.GetDecimal("budget_max"),
                    reader.GetString("currency"),
                    reader.GetDateTime("pickup_at"),
                    reader.GetString("request_status"),
                    reader.GetString("send_status"),
                    reader.GetDateTime("sent_at")));
            }
        }

        return new PagedResult<BakeryInboxItemDto>(items, page, pageSize, total);
    }

    public async Task<BakeryInboxRequestDto?> FindInboxRequestAsync(
        long userId, long bakeryId, long requestId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new MySqlCommand(
            $"""
             SELECT dr.id, dr.status AS request_status, dr.title, dr.idea_description, dr.budget_min, dr.budget_max,
                    dr.currency, dr.size_description, dr.servings, dr.flavor, dr.inscription_text, dr.pickup_at,
                    dr.inspired_by_cake_id, rb.status AS send_status, rb.decline_reason, rb.sent_at, rb.responded_at
               FROM request_bakeries rb
               JOIN design_requests dr ON dr.id = rb.design_request_id
              WHERE rb.bakery_id = @bakeryId AND rb.design_request_id = @requestId AND {ActiveMember}
             """,
            connection);
        command.Parameters.Add("@userId", MySqlDbType.Int64).Value = userId;
        command.Parameters.Add("@bakeryId", MySqlDbType.Int64).Value = bakeryId;
        command.Parameters.Add("@requestId", MySqlDbType.Int64).Value = requestId;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new BakeryInboxRequestDto(
            reader.GetInt64("id"),
            reader.GetString("request_status"),
            reader.GetString("title"),
            reader.GetString("idea_description"),
            NullableDecimal(reader, "budget_min"),
            reader.GetDecimal("budget_max"),
            reader.GetString("currency"),
            NullableString(reader, "size_description"),
            reader.IsDBNull(reader.GetOrdinal("servings")) ? null : Convert.ToInt32(reader.GetValue(reader.GetOrdinal("servings"))),
            NullableString(reader, "flavor"),
            NullableString(reader, "inscription_text"),
            reader.GetDateTime("pickup_at"),
            reader.IsDBNull(reader.GetOrdinal("inspired_by_cake_id")) ? null : reader.GetInt64("inspired_by_cake_id"),
            reader.GetString("send_status"),
            NullableString(reader, "decline_reason"),
            reader.GetDateTime("sent_at"),
            reader.IsDBNull(reader.GetOrdinal("responded_at")) ? null : reader.GetDateTime("responded_at"));
    }

    private static async Task<bool> IsActiveMemberAsync(
        MySqlConnection connection, long userId, long bakeryId, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand($"SELECT {ActiveMember}", connection);
        command.Parameters.Add("@userId", MySqlDbType.Int64).Value = userId;
        command.Parameters.Add("@bakeryId", MySqlDbType.Int64).Value = bakeryId;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static void AddFilter(MySqlCommand command, long userId, long bakeryId, string? status, string? requestStatus)
    {
        command.Parameters.Add("@userId", MySqlDbType.Int64).Value = userId;
        command.Parameters.Add("@bakeryId", MySqlDbType.Int64).Value = bakeryId;
        if (status is not null)
        {
            command.Parameters.Add("@status", MySqlDbType.VarChar).Value = status;
        }

        if (requestStatus is not null)
        {
            command.Parameters.Add("@requestStatus", MySqlDbType.VarChar).Value = requestStatus;
        }
    }

    private static string? NullableString(MySqlDataReader reader, string column) =>
        reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetString(column);

    private static decimal? NullableDecimal(MySqlDataReader reader, string column) =>
        reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetDecimal(column);
}

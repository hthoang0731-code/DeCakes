using DeCakes.Application.Cakes;
using DeCakes.Infrastructure.Persistence;
using MySqlConnector;

namespace DeCakes.Infrastructure.Cakes;

public sealed class MySqlCakeCatalogStore(IMySqlConnectionFactory connectionFactory) : ICakeCatalogStore
{
    public async Task<IReadOnlyList<CakeListItemDto>> ListPublicAsync(long offset, int take, long? bakeryId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        string sql = """
            SELECT c.id, c.bakery_id, c.name, c.reference_price, c.currency
              FROM cakes AS c
              JOIN bakeries AS b ON b.id = c.bakery_id
             WHERE c.visibility = 'published' AND c.is_demo = 0
               AND b.profile_status = 'active' AND b.is_demo = 0
            """;
        if (bakeryId.HasValue) sql += " AND c.bakery_id = @bakeryId";
        sql += " ORDER BY c.id ASC LIMIT @take OFFSET @offset";
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@take", MySqlDbType.Int64).Value = (long)take;
        command.Parameters.Add("@offset", MySqlDbType.Int64).Value = offset;
        if (bakeryId.HasValue) command.Parameters.Add("@bakeryId", MySqlDbType.Int64).Value = bakeryId.Value;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<CakeListItemDto>();
        int price = reader.GetOrdinal("reference_price");
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CakeListItemDto(
                reader.GetInt64(reader.GetOrdinal("id")),
                reader.GetInt64(reader.GetOrdinal("bakery_id")),
                reader.GetString(reader.GetOrdinal("name")),
                reader.IsDBNull(price) ? null : reader.GetDecimal(price),
                reader.GetString(reader.GetOrdinal("currency"))));
        }

        return rows;
    }

    public async Task<CakeDetailDto?> FindPublicByIdAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new MySqlCommand(
            """
            SELECT c.id, c.bakery_id, c.name, c.reference_price, c.currency,
                   c.description, c.flavor, c.size_label
              FROM cakes AS c
              JOIN bakeries AS b ON b.id = c.bakery_id
             WHERE c.id = @id AND c.visibility = 'published' AND c.is_demo = 0
               AND b.profile_status = 'active' AND b.is_demo = 0
             LIMIT 1
            """, connection);
        command.Parameters.Add("@id", MySqlDbType.Int64).Value = id;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        int price = reader.GetOrdinal("reference_price");
        int description = reader.GetOrdinal("description");
        int flavor = reader.GetOrdinal("flavor");
        int size = reader.GetOrdinal("size_label");
        return new CakeDetailDto(
            reader.GetInt64(reader.GetOrdinal("id")),
            reader.GetInt64(reader.GetOrdinal("bakery_id")),
            reader.GetString(reader.GetOrdinal("name")),
            reader.IsDBNull(price) ? null : reader.GetDecimal(price),
            reader.GetString(reader.GetOrdinal("currency")),
            reader.IsDBNull(description) ? null : reader.GetString(description),
            reader.IsDBNull(flavor) ? null : reader.GetString(flavor),
            reader.IsDBNull(size) ? null : reader.GetString(size));
    }
}

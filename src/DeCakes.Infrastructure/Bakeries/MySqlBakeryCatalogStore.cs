using DeCakes.Application.Bakeries;
using DeCakes.Infrastructure.Persistence;
using MySqlConnector;

namespace DeCakes.Infrastructure.Bakeries;

public sealed class MySqlBakeryCatalogStore(IMySqlConnectionFactory connectionFactory) : IBakeryCatalogStore
{
    public async Task<IReadOnlyList<BakeryListItemDto>> ListPublicAsync(long offset, int take, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new MySqlCommand(
            """
            SELECT id, name, slug, city
              FROM bakeries
             WHERE profile_status = 'active' AND is_demo = 0
             ORDER BY id ASC
             LIMIT @take OFFSET @offset
            """, connection);
        command.Parameters.Add("@take", MySqlDbType.Int64).Value = (long)take;
        command.Parameters.Add("@offset", MySqlDbType.Int64).Value = offset;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<BakeryListItemDto>();
        int cityOrdinal = reader.GetOrdinal("city");
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new BakeryListItemDto(
                reader.GetInt64(reader.GetOrdinal("id")),
                reader.GetString(reader.GetOrdinal("name")),
                reader.GetString(reader.GetOrdinal("slug")),
                reader.IsDBNull(cityOrdinal) ? null : reader.GetString(cityOrdinal)));
        }

        return rows;
    }

    public async Task<BakeryDetailDto?> FindPublicByIdAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new MySqlCommand(
            """
            SELECT id, name, slug, city, description, address_line, phone
              FROM bakeries
             WHERE id = @id AND profile_status = 'active' AND is_demo = 0
             LIMIT 1
            """, connection);
        command.Parameters.Add("@id", MySqlDbType.Int64).Value = id;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        int city = reader.GetOrdinal("city");
        int description = reader.GetOrdinal("description");
        int address = reader.GetOrdinal("address_line");
        int phone = reader.GetOrdinal("phone");
        return new BakeryDetailDto(
            reader.GetInt64(reader.GetOrdinal("id")),
            reader.GetString(reader.GetOrdinal("name")),
            reader.GetString(reader.GetOrdinal("slug")),
            reader.IsDBNull(city) ? null : reader.GetString(city),
            reader.IsDBNull(description) ? null : reader.GetString(description),
            reader.IsDBNull(address) ? null : reader.GetString(address),
            reader.IsDBNull(phone) ? null : reader.GetString(phone));
    }
}

using System.Data;
using DeCakes.Application.DesignRequests;
using DeCakes.Application.Quotes;
using DeCakes.Infrastructure.DesignRequests;
using DeCakes.Infrastructure.Quotes;
using MySqlConnector;

namespace DeCakes.Infrastructure.Tests.MySql;

[Collection(MySqlCollection.Name)]
public class MySqlQuoteReadStoreTests(MySqlTestDatabase database)
{
    private static CancellationToken Ct => CancellationToken.None;
    private MySqlQuoteStore Store => new(database.Factory);

    private Task<long> UserAsync(string role = "customer", string status = "active") => database.AdminExecuteAsync(
        "INSERT INTO users(email,full_name,system_role,status) VALUES (@email,'Người dùng kiểm thử',@role,@status)",
        ("@email", Guid.NewGuid().ToString("N") + "@example.com"), ("@role", role), ("@status", status));

    private async Task<long> BakeryAsync(long owner, string role = "owner")
    {
        var bakery = await database.SeedBakeryAsync("Tiệm " + Guid.NewGuid().ToString("N"));
        await database.AdminExecuteAsync("INSERT INTO bakery_members(bakery_id,user_id,member_role) VALUES (@b,@u,@r)",
            ("@b", bakery), ("@u", owner), ("@r", role));
        await database.GrantAccessAsync(bakery);
        return bakery;
    }

    private async Task<long> RequestAsync(long customer, params long[] bakeries)
    {
        var store = new MySqlDesignRequestStore(database.Factory);
        var request = await store.CreateDraftAsync(customer, new DesignRequestValues("Bánh", "Hoa trang trí", null,
            1_000_000m, "20 cm", 10, "Vani", null, DateTime.UtcNow.AddDays(7), null), Ct);
        foreach (var bakery in bakeries)
            Assert.Equal(SendToBakeryOutcome.Sent, await store.SendToBakeryAsync(request, customer, bakery, Ct));
        return request;
    }

    private static void Content(MySqlCommand command)
    {
        command.Parameters.Add("p_price", MySqlDbType.Decimal).Value = 680_000.50m;
        command.Parameters.Add("p_size_description", MySqlDbType.VarChar).Value = "20 cm";
        command.Parameters.Add("p_servings", MySqlDbType.UInt16).Value = (ushort)10;
        command.Parameters.Add("p_flavor", MySqlDbType.VarChar).Value = "Vani";
        command.Parameters.Add("p_decoration", MySqlDbType.Text).Value = "Hoa 🌷\nMàu hồng";
        command.Parameters.Add("p_inscription", MySqlDbType.VarChar).Value = DBNull.Value;
        command.Parameters.Add("p_note", MySqlDbType.Text).Value = DBNull.Value;
        command.Parameters.Add("p_estimated_pickup_at", MySqlDbType.DateTime).Value = DateTime.UtcNow.AddDays(7);
        command.Parameters.Add("p_valid_until", MySqlDbType.DateTime).Value = DateTime.UtcNow.AddDays(2);
        command.Parameters.Add("p_design_asset_id", MySqlDbType.Int64).Value = DBNull.Value;
    }

    private async Task<long> QuoteAsync(long request, long bakery, long actor)
    {
        await using var connection = await database.Factory.OpenConnectionAsync(Ct);
        await using var command = new MySqlCommand("sp_create_quote", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.Add("p_request_id", MySqlDbType.Int64).Value = request;
        command.Parameters.Add("p_bakery_id", MySqlDbType.Int64).Value = bakery;
        command.Parameters.Add("p_actor_user_id", MySqlDbType.Int64).Value = actor;
        Content(command);
        var output = command.Parameters.Add("p_quote_id", MySqlDbType.Int64);
        output.Direction = ParameterDirection.Output;
        await command.ExecuteNonQueryAsync(Ct);
        return Convert.ToInt64(output.Value);
    }

    private async Task<long> ReviseAsync(long quote, long actor)
    {
        await using var connection = await database.Factory.OpenConnectionAsync(Ct);
        await using var command = new MySqlCommand("sp_revise_quote", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.Add("p_quote_id", MySqlDbType.Int64).Value = quote;
        command.Parameters.Add("p_actor_user_id", MySqlDbType.Int64).Value = actor;
        Content(command);
        var output = command.Parameters.Add("p_new_quote_id", MySqlDbType.Int64);
        output.Direction = ParameterDirection.Output;
        await command.ExecuteNonQueryAsync(Ct);
        return Convert.ToInt64(output.Value);
    }

    [MySqlFact]
    public async Task Real_create_and_revise_out_parameters_map_versions_nulls_unicode_and_utc()
    {
        var customer = await UserAsync();
        var owner = await UserAsync("bakery");
        var bakery = await BakeryAsync(owner);
        var request = await RequestAsync(customer, bakery);
        var first = await QuoteAsync(request, bakery, owner);
        var second = await ReviseAsync(first, owner);
        Assert.NotEqual(first, second);

        var old = (await Store.FindForCustomerAsync(customer, request, first, Ct)).Quote!;
        var current = (await Store.FindForBakeryAsync(owner, bakery, request, second, Ct)).Quote!;
        Assert.Equal(("superseded", 1L, false), (old.Status, old.RevisionNo, old.IsCurrent));
        Assert.Equal(("submitted", 2L, true), (current.Status, current.RevisionNo, current.IsCurrent));
        Assert.Equal(680_000.50m, current.Price);
        Assert.Equal("Hoa 🌷\nMàu hồng", current.DecorationDescription);
        Assert.Null(current.InscriptionText);
        Assert.Null(current.Note);
        Assert.Null(current.DesignAssetId);
        Assert.Null(current.WithdrawnAt);
        Assert.Equal(DateTimeKind.Utc, current.ValidUntil.Kind);
        Assert.Equal(DateTimeKind.Utc, current.CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, current.EstimatedPickupAt.Kind);
    }

    [MySqlFact]
    public async Task Customers_only_read_quotes_of_their_own_request()
    {
        var customer = await UserAsync();
        var other = await UserAsync();
        var owner = await UserAsync("bakery");
        var bakery = await BakeryAsync(owner);
        var request = await RequestAsync(customer, bakery);
        var quote = await QuoteAsync(request, bakery, owner);

        Assert.Equal(QuoteReadOutcome.RequestNotFound, (await Store.FindForCustomerAsync(other, request, quote, Ct)).Outcome);
        Assert.Equal(QuoteReadOutcome.RequestNotFound, (await Store.ListForCustomerAsync(other, request, null, 1, 20, Ct)).Outcome);
        var anotherRequest = await RequestAsync(customer, bakery);
        Assert.Equal(QuoteReadOutcome.QuoteNotFound, (await Store.FindForCustomerAsync(customer, anotherRequest, quote, Ct)).Outcome);
        var empty = await Store.ListForCustomerAsync(customer, anotherRequest, null, 1, 20, Ct);
        Assert.Equal(QuoteReadOutcome.Found, empty.Outcome);
        Assert.Empty(empty.Page!.Items);
    }

    [MySqlFact]
    public async Task Bakery_cannot_read_competitor_quotes_even_when_request_was_sent_to_both()
    {
        var customer = await UserAsync();
        var ownerA = await UserAsync("bakery");
        var ownerB = await UserAsync("bakery");
        var bakeryA = await BakeryAsync(ownerA);
        var bakeryB = await BakeryAsync(ownerB);
        var request = await RequestAsync(customer, bakeryA, bakeryB);
        var quoteA = await QuoteAsync(request, bakeryA, ownerA);
        var quoteB = await QuoteAsync(request, bakeryB, ownerB);

        Assert.Equal(QuoteReadOutcome.BakeryNotFound, (await Store.FindForBakeryAsync(ownerA, bakeryB, request, quoteB, Ct)).Outcome);
        Assert.Equal(QuoteReadOutcome.QuoteNotFound, (await Store.FindForBakeryAsync(ownerA, bakeryA, request, quoteB, Ct)).Outcome);
        var list = await Store.ListForBakeryAsync(ownerA, bakeryA, request, null, 1, 20, Ct);
        Assert.Equal(quoteA, Assert.Single(list.Page!.Items).Id);
        Assert.Equal(2, (await Store.ListForCustomerAsync(customer, request, null, 1, 20, Ct)).Page!.TotalCount);

        await database.AdminExecuteAsync("INSERT INTO bakery_members(bakery_id,user_id,member_role) VALUES (@b,@u,'staff')", ("@b", bakeryB), ("@u", ownerA));
        Assert.Equal(QuoteReadOutcome.QuoteNotFound, (await Store.FindForBakeryAsync(ownerA, bakeryA, request, quoteB, Ct)).Outcome);
        Assert.Equal(QuoteReadOutcome.Found, (await Store.FindForBakeryAsync(ownerA, bakeryB, request, quoteB, Ct)).Outcome);
    }

    [MySqlFact]
    public async Task Removed_disabled_and_wrong_role_members_are_denied_at_the_next_read()
    {
        var customer = await UserAsync();
        var owner = await UserAsync("bakery");
        var bakery = await BakeryAsync(owner);
        var request = await RequestAsync(customer, bakery);
        var quote = await QuoteAsync(request, bakery, owner);
        var staff = await UserAsync("bakery");
        await database.AdminExecuteAsync("INSERT INTO bakery_members(bakery_id,user_id,member_role) VALUES (@b,@u,'staff')", ("@b", bakery), ("@u", staff));
        Assert.Equal(QuoteReadOutcome.Found, (await Store.FindForBakeryAsync(staff, bakery, request, quote, Ct)).Outcome);
        await database.AdminExecuteAsync("UPDATE bakery_members SET status='removed' WHERE bakery_id=@b AND user_id=@u", ("@b", bakery), ("@u", staff));
        Assert.Equal(QuoteReadOutcome.BakeryNotFound, (await Store.ListForBakeryAsync(staff, bakery, request, null, 1, 20, Ct)).Outcome);
        await database.AdminExecuteAsync("UPDATE users SET status='disabled' WHERE id=@u", ("@u", owner));
        Assert.Equal(QuoteReadOutcome.BakeryNotFound, (await Store.FindForBakeryAsync(owner, bakery, request, quote, Ct)).Outcome);
        await database.AdminExecuteAsync("UPDATE users SET status='active', system_role='customer' WHERE id=@u", ("@u", owner));
        Assert.Equal(QuoteReadOutcome.BakeryNotFound, (await Store.FindForBakeryAsync(owner, bakery, request, quote, Ct)).Outcome);
    }

    [MySqlFact]
    public async Task Unsent_requests_are_hidden_but_suspended_or_expired_bakeries_can_read_history()
    {
        var customer = await UserAsync();
        var owner = await UserAsync("bakery");
        var bakery = await BakeryAsync(owner);
        var unsent = await RequestAsync(customer);
        Assert.Equal(QuoteReadOutcome.RequestNotFound, (await Store.ListForBakeryAsync(owner, bakery, unsent, null, 1, 20, Ct)).Outcome);
        var request = await RequestAsync(customer, bakery);
        var quote = await QuoteAsync(request, bakery, owner);
        await database.AdminExecuteAsync("UPDATE bakeries SET profile_status='suspended' WHERE id=@b", ("@b", bakery));
        await database.AdminExecuteAsync("UPDATE bakery_subscription_periods SET starts_at=UTC_TIMESTAMP(3)-INTERVAL 2 DAY, ends_at=UTC_TIMESTAMP(3)-INTERVAL 1 DAY WHERE bakery_id=@b", ("@b", bakery));
        Assert.Equal(QuoteReadOutcome.Found, (await Store.FindForBakeryAsync(owner, bakery, request, quote, Ct)).Outcome);
        Assert.Equal(QuoteReadOutcome.Found, (await Store.FindForCustomerAsync(customer, request, quote, Ct)).Outcome);
    }

    [MySqlFact]
    public async Task Filters_and_paging_preserve_version_order_and_use_64_bit_offsets()
    {
        var customer = await UserAsync();
        var owner = await UserAsync("bakery");
        var bakery = await BakeryAsync(owner);
        var request = await RequestAsync(customer, bakery);
        var first = await QuoteAsync(request, bakery, owner);
        var second = await ReviseAsync(first, owner);
        var third = await ReviseAsync(second, owner);
        await database.AdminExecuteAsync("UPDATE quotes SET created_at=@t WHERE design_request_id=@r", ("@t", DateTime.UtcNow.AddDays(-1)), ("@r", request));
        var ids = new List<long>();
        for (var page = 1; page <= 3; page++)
            ids.Add(Assert.Single((await Store.ListForCustomerAsync(customer, request, null, page, 1, Ct)).Page!.Items).Id);
        Assert.Equal(new[] { third, second, first }, ids);
        Assert.Equal(third, Assert.Single((await Store.ListForBakeryAsync(owner, bakery, request, "submitted", 1, 20, Ct)).Page!.Items).Id);
        Assert.Empty((await Store.ListForCustomerAsync(customer, request, "submitted' OR 1=1", 1, 20, Ct)).Page!.Items);
        var beyond = (await Store.ListForCustomerAsync(customer, request, null, int.MaxValue, 100, Ct)).Page!;
        Assert.Equal(3, beyond.TotalCount);
        Assert.Empty(beyond.Items);
    }

    [MySqlFact]
    public async Task Reads_do_not_mutate_withdrawn_quotes_or_request_bakery_status()
    {
        var customer = await UserAsync();
        var owner = await UserAsync("bakery");
        var bakery = await BakeryAsync(owner);
        var request = await RequestAsync(customer, bakery);
        var quote = await QuoteAsync(request, bakery, owner);
        await using (var connection = await database.Factory.OpenConnectionAsync(Ct))
        await using (var command = new MySqlCommand("CALL sp_withdraw_quote(@q,@u)", connection))
        {
            command.Parameters.AddWithValue("@q", quote);
            command.Parameters.AddWithValue("@u", owner);
            await command.ExecuteNonQueryAsync(Ct);
        }
        const string snapshotSql = "SELECT CONCAT(q.status,'|',q.revision_no,'|',q.updated_at,'|',q.withdrawn_at,'|',rb.status,'|',rb.updated_at) FROM quotes q JOIN request_bakeries rb ON rb.design_request_id=q.design_request_id AND rb.bakery_id=q.bakery_id WHERE q.id=@q";
        var before = await database.AdminScalarAsync(snapshotSql, ("@q", quote));
        var data = (await Store.FindForCustomerAsync(customer, request, quote, Ct)).Quote!;
        Assert.Equal("withdrawn", data.Status);
        Assert.NotNull(data.WithdrawnAt);
        Assert.False(data.IsCurrent);
        await Store.FindForBakeryAsync(owner, bakery, request, quote, Ct);
        await Store.ListForCustomerAsync(customer, request, null, 1, 20, Ct);
        await Store.ListForBakeryAsync(owner, bakery, request, null, 1, 20, Ct);
        Assert.Equal(before, await database.AdminScalarAsync(snapshotSql, ("@q", quote)));
        Assert.Equal("quoted", await database.AdminScalarAsync("SELECT status FROM request_bakeries WHERE design_request_id=@r AND bakery_id=@b", ("@r", request), ("@b", bakery)));
    }
}

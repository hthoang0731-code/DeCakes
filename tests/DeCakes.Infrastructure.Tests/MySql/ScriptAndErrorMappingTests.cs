using System.Text.RegularExpressions;
using DeCakes.Application.DesignRequests;
using DeCakes.Application.Orders;
using DeCakes.Application.Quotes;
using DeCakes.Infrastructure.DesignRequests;
using DeCakes.Infrastructure.Orders;
using DeCakes.Infrastructure.Quotes;

namespace DeCakes.Infrastructure.Tests.MySql;

/// <summary>Không cần MySQL: bộ tách script, đọc quyền backend, và allowlist mã lỗi của procedure.</summary>
public class ScriptAndErrorMappingTests
{
    [Fact]
    public void Splitter_handles_delimiter_strings_and_comments()
    {
        const string script = """
            SET NAMES utf8mb4;
            -- comment có dấu ; không tách
            DROP PROCEDURE IF EXISTS p;
            DELIMITER $$
            /* khối comment ; $$ không tách */
            CREATE PROCEDURE p()
            BEGIN
              DECLARE v VARCHAR(20) DEFAULT 'a;b$$c';
              SELECT "x;y", `col;name`;  -- ; trong comment
            END$$
            DELIMITER ;
            SELECT 'it''s; ok';
            """;

        var statements = SqlScriptSplitter.Split(script);

        Assert.Equal(4, statements.Count);
        Assert.Equal("SET NAMES utf8mb4", statements[0]);
        Assert.EndsWith("DROP PROCEDURE IF EXISTS p", statements[1]);
        Assert.Contains("CREATE PROCEDURE p()", statements[2]);
        Assert.Contains("'a;b$$c'", statements[2]);
        Assert.EndsWith("END", statements[2]);
        Assert.Equal("SELECT 'it''s; ok'", statements[3]);
    }

    [Fact]
    public void Real_procedure_script_splits_into_eleven_drops_and_eleven_creates()
    {
        var statements = SqlScriptSplitter.Split(SchemaScript.LoadSanitized(SchemaScript.ProceduresFile)).Select(CodeOf).ToList();

        Assert.Equal(11, statements.Count(s => s.StartsWith("DROP PROCEDURE IF EXISTS ", StringComparison.Ordinal)));
        var creates = statements.Where(s => s.StartsWith("CREATE PROCEDURE ", StringComparison.Ordinal)).ToList();
        Assert.Equal(11, creates.Count);
        Assert.All(creates, s => Assert.EndsWith("END", s));
        Assert.Equal(["SET NAMES utf8mb4"], statements.Where(s => !s.StartsWith("DROP ", StringComparison.Ordinal) && !s.StartsWith("CREATE ", StringComparison.Ordinal)));
        Assert.DoesNotContain(statements, s => s.Contains("DELIMITER", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Backend_grants_are_rewritten_only_to_test_database_and_user()
    {
        var grants = SchemaScript.LoadGrantsFor("decakes_test_abc123", "dc_test_abc123");

        // 10 câu của app_user_grants.sql + đề xuất V003 (1, user_tokens) + V004 (4) + V005 (1).
        Assert.Equal(16, grants.Count);
        Assert.Contains("GRANT INSERT, UPDATE ON `decakes_test_abc123`.`user_tokens` TO 'dc_test_abc123'@'%'", grants);
        Assert.All(grants, g => Assert.Matches(@"^GRANT .+ ON `decakes_test_abc123`\.(\*|`[a-z_]+`) TO 'dc_test_abc123'@'%'$", g));
        Assert.DoesNotContain(grants, g => g.Contains("decakes_db", StringComparison.Ordinal) || g.Contains("decakes_app", StringComparison.Ordinal));
        Assert.Contains(grants, g => g.StartsWith("GRANT UPDATE (status, decline_reason, responded_at, updated_at) ON", StringComparison.Ordinal));
    }

    [Fact]
    public void Account_security_migration_is_loadable_and_records_version_three()
    {
        var statements = SqlScriptSplitter.Split(SchemaScript.LoadSanitized(SchemaScript.MigrationFiles[0])).Select(CodeOf).ToList();

        Assert.Contains(statements, s => s.StartsWith("CREATE PROCEDURE tmp_v003_precheck()", StringComparison.Ordinal) && s.EndsWith("END", StringComparison.Ordinal));
        Assert.Contains("CALL tmp_v003_precheck()", statements);
        Assert.Contains(statements, s => s.StartsWith("ALTER TABLE users", StringComparison.Ordinal));
        Assert.Contains(statements, s => s.StartsWith("CREATE TABLE user_tokens", StringComparison.Ordinal));
        Assert.Contains(statements, s => s.StartsWith("INSERT INTO schema_migrations", StringComparison.Ordinal) && s.Contains("VALUES (3,"));
        Assert.DoesNotContain(statements, s => s.Contains("DELIMITER", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("decakes_db", "dc_test_x")]
    [InlineData("decakes_test_x", "decakes_app")]
    [InlineData("decakes_test_x", "dc_test_x'@'%'; DROP USER x")]
    public void Grants_refuse_non_test_targets(string database, string user)
    {
        Assert.Throws<InvalidOperationException>(() => SchemaScript.LoadGrantsFor(database, user));
    }

    [Fact]
    public void Allowlist_covers_every_signal_of_send_procedure_and_its_guards()
    {
        // Phiên bản có hiệu lực sau migration đề xuất V005: sp_send_request_to_bakery được tạo lại và gọi thêm
        // sp_guard_bakery_entitled; hai guard còn lại giữ nguyên trong install/02.
        var install = File.ReadAllText(Path.Combine(RepoRoot(), "database", "install", "02_procedures.sql"));
        var v005 = File.ReadAllText(Path.Combine(RepoRoot(), "database", "migrations", "V005__subscription_payments.sql"));
        Assert.Contains("CALL sp_guard_bakery_entitled(p_bakery_id, v_now);", Block(v005, "sp_send_request_to_bakery"));
        var codes = new[] { (install, "sp_guard_active_customer"), (install, "sp_guard_bakery_active"), (v005, "sp_send_request_to_bakery"), (v005, "sp_guard_bakery_entitled") }
            .SelectMany(x => Regex.Matches(Block(x.Item1, x.Item2), @"MESSAGE_TEXT = '([A-Z_]+)'").Select(m => m.Groups[1].Value))
            .ToHashSet();

        Assert.Equal(codes.Order(), SendRequestProcedureErrors.KnownCodes.Order());
    }

    [Theory]
    [InlineData("45000", "REQUEST_ALREADY_SENT", SendToBakeryOutcome.AlreadySent)]
    [InlineData("45000", "NOT_REQUEST_OWNER", SendToBakeryOutcome.RequestNotFound)]
    [InlineData("45000", "BAKERY_NOT_ACTIVE", SendToBakeryOutcome.BakeryNotActive)]
    public void Known_codes_map(string sqlState, string message, SendToBakeryOutcome expected)
    {
        Assert.True(SendRequestProcedureErrors.TryMap(sqlState, message, out var outcome));
        Assert.Equal(expected, outcome);
    }

    [Theory]
    [InlineData("45000", "QUOTE_EXPIRED")]           // mã của procedure khác
    [InlineData("45000", "request_already_sent")]    // phân biệt hoa thường
    [InlineData("23000", "REQUEST_ALREADY_SENT")]    // không phải SIGNAL nghiệp vụ
    [InlineData("45000", null)]
    public void Unknown_codes_are_not_mapped(string sqlState, string? message)
    {
        Assert.False(SendRequestProcedureErrors.TryMap(sqlState, message, out _));
    }

    [Theory]
    [InlineData("sp_create_quote")]
    [InlineData("sp_revise_quote")]
    [InlineData("sp_withdraw_quote")]
    public void Quote_allowlists_equal_the_signals_of_each_procedure_and_the_guards_it_calls(string procedure)
    {
        // Bản có hiệu lực: V005 tạo lại create/revise (thêm sp_guard_bakery_entitled); withdraw và các guard khác giữ install/02.
        var install = File.ReadAllText(Path.Combine(RepoRoot(), "database", "install", "02_procedures.sql"));
        var v005 = File.ReadAllText(Path.Combine(RepoRoot(), "database", "migrations", "V005__subscription_payments.sql"));
        string Effective(string name) => v005.Contains($"-- ----- BLOCK: {name} -----", StringComparison.Ordinal) ? Block(v005, name) : Block(install, name);

        var body = Effective(procedure);
        var blocks = new[] { body }.Concat(Regex.Matches(body, @"CALL (sp_guard_[a-z_]+)\(").Select(m => Effective(m.Groups[1].Value)));
        var codes = blocks.SelectMany(b => Regex.Matches(b, @"MESSAGE_TEXT = '([A-Z_]+)'").Select(m => m.Groups[1].Value)).ToHashSet();
        var allowlist = procedure switch
        {
            "sp_create_quote" => QuoteProcedureErrors.Create,
            "sp_revise_quote" => QuoteProcedureErrors.Revise,
            _ => QuoteProcedureErrors.Withdraw,
        };

        Assert.Equal(codes.Order(), allowlist.Keys.Order());
        Assert.Equal(procedure != "sp_withdraw_quote", codes.Contains("BAKERY_SUBSCRIPTION_INACTIVE"));
    }

    [Theory]
    [InlineData("45000", "QUOTE_NOT_CURRENT", QuoteWriteOutcome.QuoteNotCurrent)]
    [InlineData("45000", "NOT_BAKERY_MEMBER", QuoteWriteOutcome.BakeryNotFound)]
    [InlineData("45000", "QUOTE_ALREADY_ACCEPTED", QuoteWriteOutcome.QuoteAlreadyAccepted)]
    public void Known_withdraw_codes_map(string sqlState, string message, QuoteWriteOutcome expected)
    {
        Assert.True(QuoteProcedureErrors.TryMap(QuoteProcedureErrors.Withdraw, sqlState, message, out var outcome));
        Assert.Equal(expected, outcome);
    }

    [Theory]
    [InlineData("45000", "QUOTE_ALREADY_EXISTS")]          // mã của sp_create_quote, withdraw không thể trả
    [InlineData("45000", "BAKERY_SUBSCRIPTION_INACTIVE")]  // rút không kiểm tra gói
    [InlineData("45000", "QUOTE_EXPIRED")]                 // mã của sp_accept_quote
    [InlineData("45000", "quote_not_current")]             // phân biệt hoa thường
    [InlineData("23000", "QUOTE_NOT_CURRENT")]             // không phải SIGNAL nghiệp vụ
    [InlineData("45000", null)]
    public void Codes_outside_the_withdraw_allowlist_are_not_mapped(string sqlState, string? message)
    {
        Assert.False(QuoteProcedureErrors.TryMap(QuoteProcedureErrors.Withdraw, sqlState, message, out _));
        if (message == "QUOTE_EXPIRED")
        {
            Assert.False(QuoteProcedureErrors.TryMap(QuoteProcedureErrors.Create, sqlState, message, out _));
            Assert.False(QuoteProcedureErrors.TryMap(QuoteProcedureErrors.Revise, sqlState, message, out _));
        }
    }

    [Fact]
    public void Order_rules_migration_is_loadable_needs_no_grants_and_records_version_six_last()
    {
        var statements = SqlScriptSplitter.Split(SchemaScript.LoadSanitized(SchemaScript.OrderRulesMigrationFile)).Select(CodeOf).ToList();

        Assert.Equal("CALL tmp_v006_precheck()", statements.Single(s => s.StartsWith("CALL ", StringComparison.Ordinal)));
        Assert.Equal(["sp_accept_quote", "sp_bakery_decide_order"], statements
            .Where(s => s.StartsWith("CREATE PROCEDURE sp_", StringComparison.Ordinal))
            .Select(s => s["CREATE PROCEDURE ".Length..s.IndexOf('(')]));
        Assert.StartsWith("INSERT INTO schema_migrations (version, description)\nVALUES (6,", statements[^1].ReplaceLineEndings("\n"));
        Assert.DoesNotContain(statements, s => s.Contains("DELIMITER", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(statements, s => s.StartsWith("ALTER ", StringComparison.Ordinal) || s.StartsWith("CREATE TABLE", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(RepoRoot(), SchemaScript.OrderRulesMigrationFile.Replace(".sql", ".grants.sql", StringComparison.Ordinal))));
        Assert.DoesNotContain(SchemaScript.GrantFiles, f => f.Contains("V006", StringComparison.Ordinal));
    }

    [Fact]
    public void Order_rules_migration_only_adds_the_approved_checks_to_the_effective_procedures()
    {
        var install = File.ReadAllText(Path.Combine(RepoRoot(), "database", "install", "02_procedures.sql"));
        var v005 = File.ReadAllText(Path.Combine(RepoRoot(), "database", "migrations", "V005__subscription_payments.sql"));
        var v006 = File.ReadAllText(Path.Combine(RepoRoot(), SchemaScript.OrderRulesMigrationFile));

        // Accept: mọi dòng mã của V005 (khóa, guard, gói, bản chốt INSERT) giữ nguyên thứ tự; chỉ thêm lần lấy giờ thứ hai và kiểm tra giờ nhận.
        var accept = AddedCodeLines(CodeLines(Block(v005, "sp_accept_quote")), CodeLines(Block(v006, "sp_accept_quote")));
        Assert.Equal(
        [
            "SET v_now = SYSDATE(3) - INTERVAL TIMESTAMPDIFF(MICROSECOND, UTC_TIMESTAMP(3), NOW(3)) MICROSECOND;",
            "IF v_pickup_at <= v_now THEN",
            "SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'QUOTE_PICKUP_PASSED';",
            "END IF;",
        ], accept);
        var acceptBody = Block(v006, "sp_accept_quote");
        Assert.True(acceptBody.IndexOf("CALL sp_guard_bakery_entitled", StringComparison.Ordinal)
                    < acceptBody.LastIndexOf("SET v_now", StringComparison.Ordinal));
        Assert.True(acceptBody.LastIndexOf("SET v_now", StringComparison.Ordinal)
                    < acceptBody.IndexOf("'QUOTE_EXPIRED'", StringComparison.Ordinal));

        // Decide: giữ mọi dòng của install/02; dòng khóa orders chỉ đọc thêm agreed_pickup_at.
        var original = CodeLines(Block(install, "sp_bakery_decide_order")).Select(line => line == "SELECT bakery_id, status INTO v_bakery_id, v_status"
            ? "SELECT bakery_id, status, agreed_pickup_at INTO v_bakery_id, v_status, v_pickup_at" : line).ToList();
        Assert.Equal(
        [
            "DECLARE v_pickup_at  DATETIME(3) DEFAULT NULL;",
            "IF p_decision = 'reject' AND (p_reason IS NULL OR TRIM(p_reason) = '') THEN",
            "SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ORDER_REJECTION_REASON_REQUIRED';",
            "END IF;",
            "IF v_now >= v_pickup_at THEN",
            "SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ORDER_PICKUP_PASSED';",
            "END IF;",
        ], AddedCodeLines(original, CodeLines(Block(v006, "sp_bakery_decide_order"))));
    }

    [Theory]
    [InlineData("sp_accept_quote")]
    [InlineData("sp_bakery_decide_order")]
    public void Order_allowlists_equal_the_signals_of_each_procedure_and_the_guards_it_calls(string procedure)
    {
        // Bản có hiệu lực: V006 cho accept/decide, V005 cho sp_guard_bakery_entitled, install/02 cho các guard khác.
        var sources = new[]
        {
            File.ReadAllText(Path.Combine(RepoRoot(), SchemaScript.OrderRulesMigrationFile)),
            File.ReadAllText(Path.Combine(RepoRoot(), "database", "migrations", "V005__subscription_payments.sql")),
            File.ReadAllText(Path.Combine(RepoRoot(), "database", "install", "02_procedures.sql")),
        };
        string Effective(string name) => Block(sources.First(s => s.Contains($"-- ----- BLOCK: {name} -----", StringComparison.Ordinal)), name);

        var body = Effective(procedure);
        var blocks = new[] { body }.Concat(Regex.Matches(body, @"CALL (sp_guard_[a-z_]+)\(").Select(m => Effective(m.Groups[1].Value)));
        var codes = blocks.SelectMany(b => Regex.Matches(b, @"MESSAGE_TEXT = '([A-Z_]+)'").Select(m => m.Groups[1].Value)).ToHashSet();
        var allowlist = procedure == "sp_accept_quote" ? OrderProcedureErrors.Accept : OrderProcedureErrors.Decide;

        Assert.Equal(codes.Order(), allowlist.Keys.Order());
        Assert.Equal(procedure == "sp_accept_quote", codes.Contains("BAKERY_SUBSCRIPTION_INACTIVE"));
    }

    [Theory]
    [InlineData("45000", "QUOTE_EXPIRED", OrderWriteOutcome.QuoteExpired)]
    [InlineData("45000", "QUOTE_PICKUP_PASSED", OrderWriteOutcome.QuotePickupPassed)]
    [InlineData("45000", "NOT_REQUEST_OWNER", OrderWriteOutcome.RequestNotFound)]
    public void Known_accept_codes_map(string sqlState, string message, OrderWriteOutcome expected)
    {
        Assert.True(OrderProcedureErrors.TryMap(OrderProcedureErrors.Accept, sqlState, message, out var outcome));
        Assert.Equal(expected, outcome);
    }

    [Theory]
    [InlineData("45000", "ORDER_PICKUP_PASSED")]            // mã của sp_bakery_decide_order
    [InlineData("45000", "ORDER_NOT_PENDING")]
    [InlineData("45000", "MIGRATION_V006_ALREADY_APPLIED")] // mã của precheck migration
    [InlineData("45000", "quote_expired")]                  // phân biệt hoa thường
    [InlineData("23000", "QUOTE_EXPIRED")]                  // không phải SIGNAL nghiệp vụ
    [InlineData("45000", null)]
    public void Codes_outside_the_accept_allowlist_are_not_mapped(string sqlState, string? message)
    {
        Assert.False(OrderProcedureErrors.TryMap(OrderProcedureErrors.Accept, sqlState, message, out _));
    }

    [Theory]
    [InlineData("45000", "QUOTE_EXPIRED")]                  // mã của sp_accept_quote
    [InlineData("45000", "BAKERY_SUBSCRIPTION_INACTIVE")]   // quyết định đơn không kiểm tra gói
    [InlineData("45000", "QUOTE_PICKUP_PASSED")]
    [InlineData("45000", "order_not_pending")]
    [InlineData("22001", "ORDER_NOT_PENDING")]
    [InlineData("45000", null)]
    public void Codes_outside_the_decide_allowlist_are_not_mapped(string sqlState, string? message)
    {
        Assert.False(OrderProcedureErrors.TryMap(OrderProcedureErrors.Decide, sqlState, message, out _));
    }

    // Dòng mã (bỏ comment, trim, bỏ dòng trống) của một block procedure.
    private static List<string> CodeLines(string block) => block.ReplaceLineEndings("\n").Split('\n')
        .Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith("--", StringComparison.Ordinal)).ToList();

    // Bản mới phải giữ mọi dòng của bản cũ theo đúng thứ tự; trả về các dòng được thêm.
    private static List<string> AddedCodeLines(IReadOnlyList<string> original, IReadOnlyList<string> updated)
    {
        var added = new List<string>();
        var index = 0;
        foreach (var line in updated)
        {
            if (index < original.Count && line == original[index]) index++;
            else added.Add(line);
        }

        Assert.True(index == original.Count, $"Dòng gốc bị đổi hoặc mất: '{(index < original.Count ? original[index] : "")}'");
        return added;
    }

    // Phần mã của câu lệnh, bỏ các dòng comment đứng trước.
    private static string CodeOf(string statement) =>
        string.Join('\n', statement.Split('\n').SkipWhile(l => l.TrimStart().StartsWith("--", StringComparison.Ordinal) || l.Trim().Length == 0)).Trim();

    private static string Block(string source, string procedure)
    {
        var start = source.IndexOf($"-- ----- BLOCK: {procedure} -----", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Không thấy procedure {procedure}");
        var end = source.IndexOf("END$$", start, StringComparison.Ordinal);
        return source[start..end];
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DeCakes.slnx"))) directory = directory.Parent;
        return directory!.FullName;
    }
}

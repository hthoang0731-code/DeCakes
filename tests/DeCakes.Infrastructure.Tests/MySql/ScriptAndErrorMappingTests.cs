using System.Text.RegularExpressions;
using DeCakes.Application.DesignRequests;
using DeCakes.Infrastructure.DesignRequests;

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

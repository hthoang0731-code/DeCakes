using System.Text.RegularExpressions;

namespace DeCakes.Infrastructure.Tests.MySql;

/// <summary>
/// Nạp script SQL nguồn (không sửa file) cho database kiểm thử: bỏ đúng dòng "USE decakes_db;" để script chạy
/// trong database tạm, và từ chối nếu script còn câu lệnh có thể chuyển/tạo/xóa database hoặc đổi tài khoản.
/// Quyền của tài khoản kiểm thử được đọc từ security/app_user_grants.sql (quyền của backend thật).
/// </summary>
public static partial class SchemaScript
{
    public const string SchemaFile = "database/install/01_schema.sql";
    public const string ProceduresFile = "database/install/02_procedures.sql";
    public const string GrantsFile = "database/security/app_user_grants.sql";

    public const string OrderRulesMigrationFile = "database/migrations/V006__order_decision_time_rules.sql";

    /// <summary>
    /// Migration nạp theo thứ tự sau install để mã hiện tại có đủ cột/bảng/procedure (V003–V005 đã áp dụng lên Aiven,
    /// V006 mới chỉ kiểm thử local). Mỗi file có grants tương ứng <c>*.grants.sql</c> cùng định dạng app_user_grants.sql,
    /// trừ các migration trong <see cref="MigrationsWithoutGrants"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> MigrationFiles =
    [
        "database/migrations/V003__account_security.sql",
        "database/migrations/V004__bakery_onboarding.sql",
        "database/migrations/V005__subscription_payments.sql",
        OrderRulesMigrationFile,
    ];

    /// <summary>V006 chỉ tạo lại procedure (DEFINER); backend đã có EXECUTE nên không có file grants, không cấp thêm quyền.</summary>
    public static readonly IReadOnlySet<string> MigrationsWithoutGrants = new HashSet<string>(StringComparer.Ordinal) { OrderRulesMigrationFile };

    public static IEnumerable<string> GrantFiles =>
    [
        GrantsFile,
        .. MigrationFiles.Where(file => !MigrationsWithoutGrants.Contains(file))
            .Select(file => file.Replace(".sql", ".grants.sql", StringComparison.Ordinal)),
    ];

    public static string LoadSanitizedSchema() => LoadSanitized(SchemaFile);

    public static string LoadSanitized(string relativePath) => Sanitize(File.ReadAllText(SourcePath(relativePath)));

    public static string Sanitize(string script)
    {
        var lines = script.Split('\n');
        var useLines = lines.Select((line, index) => (line, index)).Where(x => UseDecakesDb().IsMatch(x.line)).ToList();
        if (useLines.Count != 1)
        {
            throw new InvalidOperationException($"Script phải có đúng một dòng 'USE decakes_db;' (tìm thấy {useLines.Count}).");
        }

        var kept = lines.Where((_, index) => index != useLines[0].index).ToArray();
        foreach (var line in kept)
        {
            var code = StripComment(line);
            if (ForbiddenStatement().IsMatch(code))
            {
                throw new InvalidOperationException($"Script chứa câu lệnh không được phép trong kiểm thử: '{code.Trim()}'.");
            }
        }

        return string.Join('\n', kept);
    }

    /// <summary>
    /// Các câu GRANT của backend, chuyển sang database và tài khoản kiểm thử. Mọi câu lệnh khác, hoặc GRANT
    /// không đúng dạng "GRANT ... ON decakes_db.&lt;đối tượng&gt; TO 'decakes_app'@'%'", đều bị từ chối.
    /// </summary>
    public static IReadOnlyList<string> LoadGrantsFor(string databaseName, string userName)
    {
        if (!MySqlTestSettings.IsTestDatabaseName(databaseName)
            || !userName.StartsWith(MySqlTestSettings.UserPrefix, StringComparison.Ordinal)
            || !userName.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))
        {
            throw new InvalidOperationException("Tên database hoặc tài khoản kiểm thử không hợp lệ.");
        }

        var code = string.Join('\n', GrantFiles.SelectMany(file => File.ReadAllLines(SourcePath(file))).Select(StripComment));
        var statements = code.Split(';').Select(s => Whitespace().Replace(s, " ").Trim()).Where(s => s.Length > 0).ToList();
        var result = new List<string>();
        foreach (var statement in statements)
        {
            var match = BackendGrant().Match(statement);
            if (!match.Success)
            {
                throw new InvalidOperationException($"Câu lệnh quyền không đúng dạng mong đợi: '{statement}'.");
            }

            var target = match.Groups["object"].Value == "*" ? "*" : $"`{match.Groups["object"].Value}`";
            result.Add($"GRANT {match.Groups["privileges"].Value} ON `{databaseName}`.{target} TO '{userName}'@'%'");
        }

        return result.Count > 0 ? result : throw new InvalidOperationException("Không có câu GRANT nào.");
    }

    private static string StripComment(string line)
    {
        var index = line.IndexOf("--", StringComparison.Ordinal);
        return index >= 0 ? line[..index] : line;
    }

    private static string SourcePath(string relativePath) => Path.Combine(FindRepoRoot(), relativePath);

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DeCakes.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Không tìm thấy thư mục repository (DeCakes.slnx).");
    }

    [GeneratedRegex(@"^\s*USE\s+`?decakes_db`?\s*;\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex UseDecakesDb();

    [GeneratedRegex(@"\bUSE\s+\S|\b(CREATE|DROP|ALTER)\s+(DATABASE|SCHEMA)\b|\bGRANT\b|\bREVOKE\b|\b(CREATE|DROP|ALTER)\s+USER\b", RegexOptions.IgnoreCase)]
    private static partial Regex ForbiddenStatement();

    [GeneratedRegex(@"^GRANT (?<privileges>[A-Z]+(?: [A-Z]+)*(?: ?\([a-z_, ]+\))?(?:, ?[A-Z]+(?: [A-Z]+)*(?: ?\([a-z_, ]+\))?)*) ON decakes_db\.(?<object>\*|[a-z_]+) TO 'decakes_app'@'%'$")]
    private static partial Regex BackendGrant();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

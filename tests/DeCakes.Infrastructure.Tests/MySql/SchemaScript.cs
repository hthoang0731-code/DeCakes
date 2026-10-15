using System.Text.RegularExpressions;

namespace DeCakes.Infrastructure.Tests.MySql;

/// <summary>
/// Nạp database/install/01_schema.sql (SQL nguồn, không sửa file) cho database kiểm thử.
/// Bỏ đúng dòng "USE decakes_db;" để script chạy trong database tạm, và từ chối nếu script còn câu lệnh
/// có thể chuyển hoặc tạo/xóa database.
/// </summary>
public static partial class SchemaScript
{
    public static string LoadSanitizedSchema() => Sanitize(File.ReadAllText(Path.Combine(FindRepoRoot(), "database", "install", "01_schema.sql")));

    public static string Sanitize(string script)
    {
        var lines = script.Split('\n');
        var useLines = lines.Select((line, index) => (line, index)).Where(x => UseDecakesDb().IsMatch(x.line)).ToList();
        if (useLines.Count != 1)
        {
            throw new InvalidOperationException($"Script schema phải có đúng một dòng 'USE decakes_db;' (tìm thấy {useLines.Count}).");
        }

        var kept = lines.Where((_, index) => index != useLines[0].index).ToArray();
        foreach (var line in kept)
        {
            var code = StripComment(line);
            if (ForbiddenStatement().IsMatch(code))
            {
                throw new InvalidOperationException($"Script schema chứa câu lệnh không được phép trong kiểm thử: '{code.Trim()}'.");
            }
        }

        return string.Join('\n', kept);
    }

    private static string StripComment(string line)
    {
        var index = line.IndexOf("--", StringComparison.Ordinal);
        return index >= 0 ? line[..index] : line;
    }

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

    [GeneratedRegex(@"\bUSE\s+\S|\b(CREATE|DROP|ALTER)\s+(DATABASE|SCHEMA)\b|\bGRANT\b|\bCREATE\s+USER\b", RegexOptions.IgnoreCase)]
    private static partial Regex ForbiddenStatement();
}

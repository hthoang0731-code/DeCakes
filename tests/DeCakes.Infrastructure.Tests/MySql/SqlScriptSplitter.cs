using System.Text;
using System.Text.RegularExpressions;

namespace DeCakes.Infrastructure.Tests.MySql;

/// <summary>
/// Tách script MySQL thành câu lệnh theo cách client mysql/DBeaver làm: hiểu lệnh DELIMITER ở đầu dòng,
/// bỏ qua dấu phân cách nằm trong chuỗi ('...', "...", `...`) và comment (-- , #, /* */).
/// Không split mù theo dấu chấm phẩy.
/// </summary>
public static partial class SqlScriptSplitter
{
    public static IReadOnlyList<string> Split(string script)
    {
        var statements = new List<string>();
        var current = new StringBuilder();
        var hasCode = false;
        var delimiter = ";";
        var i = 0;
        var atLineStart = true;

        void Flush()
        {
            if (hasCode) statements.Add(current.ToString().Trim());
            current.Clear();
            hasCode = false;
        }

        while (i < script.Length)
        {
            if (atLineStart && !hasCode)
            {
                var lineEnd = script.IndexOf('\n', i);
                var line = lineEnd < 0 ? script[i..] : script[i..lineEnd];
                var match = DelimiterCommand().Match(line);
                if (match.Success)
                {
                    delimiter = match.Groups[1].Value;
                    current.Clear();
                    i = lineEnd < 0 ? script.Length : lineEnd + 1;
                    continue;
                }
            }

            var c = script[i];
            atLineStart = false;

            if (StartsLineComment(script, i))
            {
                var end = script.IndexOf('\n', i);
                end = end < 0 ? script.Length : end;
                current.Append(script, i, end - i);
                i = end;
                continue;
            }

            if (c == '/' && i + 1 < script.Length && script[i + 1] == '*')
            {
                var end = script.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? script.Length : end + 2;
                current.Append(script, i, end - i);
                i = end;
                continue;
            }

            if (c is '\'' or '"' or '`')
            {
                var end = EndOfQuoted(script, i);
                current.Append(script, i, end - i);
                hasCode = true;
                i = end;
                continue;
            }

            if (string.CompareOrdinal(script, i, delimiter, 0, delimiter.Length) == 0)
            {
                Flush();
                i += delimiter.Length;
                continue;
            }

            current.Append(c);
            if (!char.IsWhiteSpace(c)) hasCode = true;
            atLineStart = c == '\n';
            i++;
        }

        Flush();
        return statements;
    }

    private static bool StartsLineComment(string s, int i) =>
        s[i] == '#'
        || (s[i] == '-' && i + 1 < s.Length && s[i + 1] == '-' && (i + 2 >= s.Length || char.IsWhiteSpace(s[i + 2])));

    private static int EndOfQuoted(string s, int start)
    {
        var quote = s[start];
        var i = start + 1;
        while (i < s.Length)
        {
            if (quote != '`' && s[i] == '\\')
            {
                i += 2;
                continue;
            }

            if (s[i] == quote)
            {
                if (i + 1 < s.Length && s[i + 1] == quote)
                {
                    i += 2; // ký tự quote nhân đôi
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        throw new InvalidOperationException("Chuỗi trong script không được đóng.");
    }

    [GeneratedRegex(@"^\s*DELIMITER\s+(\S+)\s*\r?$", RegexOptions.IgnoreCase)]
    private static partial Regex DelimiterCommand();
}

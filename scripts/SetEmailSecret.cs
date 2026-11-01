// Nhập cấu hình gửi email (Resend) vào user-secrets của project Api.
// API key nhập ẩn, không đi qua tham số dòng lệnh nên không nằm trong lịch sử lệnh hay danh sách tiến trình.
// Giữ nguyên mọi secret khác (vd. ConnectionStrings:DeCakesDb). Không in giá trị API key, không gọi Resend.
// Chạy: dotnet run scripts/SetEmailSecret.cs
using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

const string BaseUrlKey = "App:PublicBaseUrl";
const string FromKey = "Email:From";
const string ApiKeyKey = "Email:Resend:ApiKey";

if (Console.IsInputRedirected)
{ Console.Error.WriteLine("Chạy trong terminal tương tác; không truyền API key qua pipe hoặc file."); return 1; }

var repoRoot = FindRepoRoot();
var csproj = Path.Combine(repoRoot, "src", "DeCakes.Api", "DeCakes.Api.csproj");
var secretsId = Regex.Match(File.ReadAllText(csproj), "<UserSecretsId>([^<]+)</UserSecretsId>").Groups[1].Value;
if (secretsId.Length == 0) { Console.Error.WriteLine("Không tìm thấy UserSecretsId trong Api."); return 1; }

var secretsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "UserSecrets", secretsId);
var secretsFile = Path.Combine(secretsDir, "secrets.json");
var root = File.Exists(secretsFile) ? (JsonNode.Parse(File.ReadAllText(secretsFile)) as JsonObject ?? new JsonObject()) : new JsonObject();

Console.WriteLine("Để trống một mục để giữ giá trị đang có (nếu có).");

var currentBaseUrl = root[BaseUrlKey]?.GetValue<string>();
var baseUrl = Ask($"{BaseUrlKey} — origin HTTPS nhận link{(currentBaseUrl is null ? "" : $" [{currentBaseUrl}]")}: ", currentBaseUrl);
if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
    || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
{ Console.Error.WriteLine("URL phải là HTTPS tuyệt đối, không có user-info, query hay fragment (vd. https://localhost:7215)."); return 1; }

var currentFrom = root[FromKey]?.GetValue<string>();
var from = Ask($"{FromKey} — vd. DeCakes <no-reply@send.ten-mien.vn>{(currentFrom is null ? "" : $" [{currentFrom}]")}: ", currentFrom);
if (!MailAddress.TryCreate(from, out var fromAddress))
{ Console.Error.WriteLine("Địa chỉ gửi không hợp lệ."); return 1; }
if (fromAddress.Host.Equals("resend.dev", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("Lưu ý: domain thử nghiệm resend.dev chỉ gửi được tới email của chính tài khoản Resend.");
}

var hasKey = root[ApiKeyKey] is not null;
var apiKey = AskHidden($"{ApiKeyKey} (nhập ẩn{(hasKey ? ", để trống để giữ khóa đang có" : "")}): ").Trim();
if (apiKey.Length == 0 && !hasKey) { Console.Error.WriteLine("API key trống."); return 1; }
if (apiKey.Length > 0 && (!apiKey.StartsWith("re_", StringComparison.Ordinal) || apiKey.Any(char.IsWhiteSpace)))
{ Console.Error.WriteLine("API key Resend có dạng re_..., không chứa khoảng trắng."); return 1; }

root[BaseUrlKey] = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
root[FromKey] = from;
if (apiKey.Length > 0) root[ApiKeyKey] = apiKey;

Directory.CreateDirectory(secretsDir);
File.WriteAllText(secretsFile, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine($"Đã lưu {BaseUrlKey}, {FromKey}{(apiKey.Length > 0 ? $", {ApiKeyKey}" : "")} vào user-secrets. Khởi động lại API để chắc chắn dùng cấu hình mới.");
return 0;

static string Ask(string prompt, string? fallback = null)
{
    Console.Write(prompt);
    var value = (Console.ReadLine() ?? "").Trim();
    return value.Length == 0 && fallback is not null ? fallback : value;
}

static string AskHidden(string prompt)
{
    Console.Write(prompt);
    var sb = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
        if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
    }
    Console.WriteLine();
    return sb.ToString();
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DeCakes.slnx"))) dir = dir.Parent;
    return dir?.FullName ?? throw new InvalidOperationException("Chạy lệnh trong thư mục repository DeCakes.");
}

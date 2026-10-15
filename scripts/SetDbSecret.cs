#:package MySqlConnector@2.6.2

// Nhập cấu hình kết nối DeCakes vào user-secrets của project Api.
// Mật khẩu nhập ẩn, không đi qua tham số dòng lệnh nên không nằm trong lịch sử lệnh hay danh sách tiến trình.
// Chạy: dotnet run scripts/SetDbSecret.cs
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MySqlConnector;

const string SecretKey = "ConnectionStrings:DeCakesDb";

var repoRoot = FindRepoRoot();
var csproj = Path.Combine(repoRoot, "src", "DeCakes.Api", "DeCakes.Api.csproj");
var secretsId = Regex.Match(File.ReadAllText(csproj), "<UserSecretsId>([^<]+)</UserSecretsId>").Groups[1].Value;
if (secretsId.Length == 0) { Console.Error.WriteLine("Không tìm thấy UserSecretsId trong Api."); return 1; }

var host = Ask("Host Aiven (dạng ...aivencloud.com): ");
var port = Ask("Port: ");
var user = Ask("Tài khoản backend (không phải avnadmin): ");
var caPath = Ask($"Đường dẫn ca.pem [{Path.Combine(repoRoot, "certs", "ca.pem")}]: ", Path.Combine(repoRoot, "certs", "ca.pem"));

if (!host.Contains('.') || host.Equals("defaultdb", StringComparison.OrdinalIgnoreCase))
{ Console.Error.WriteLine("Host có vẻ sai: cần tên máy chủ đầy đủ, không phải tên database."); return 1; }
if (!uint.TryParse(port, out var portNumber) || portNumber is < 1 or > 65535)
{ Console.Error.WriteLine("Port không hợp lệ (cần số từ 1 đến 65535)."); return 1; }
if (user.Length == 0) { Console.Error.WriteLine("Tài khoản trống."); return 1; }
if (user.Equals("avnadmin", StringComparison.OrdinalIgnoreCase))
{ Console.Error.WriteLine("Không dùng avnadmin cho ứng dụng. Tạo tài khoản backend riêng (xem database/security/app_user_grants.sql)."); return 1; }
if (caPath.Length == 0) { Console.Error.WriteLine("Đường dẫn CA trống."); return 1; }
caPath = Path.GetFullPath(caPath);
if (!File.Exists(caPath)) { Console.Error.WriteLine("Không thấy file CA."); return 1; }
var password = AskHidden("Mật khẩu (nhập ẩn): ");
if (password.Length == 0) { Console.Error.WriteLine("Mật khẩu trống."); return 1; }

var builder = new MySqlConnectionStringBuilder
{
    Server = host,
    Port = portNumber,
    Database = "decakes_db",
    UserID = user,
    Password = password,
    SslMode = MySqlSslMode.VerifyFull,
    SslCa = caPath,
};

var secretsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "UserSecrets", secretsId);
Directory.CreateDirectory(secretsDir);
var secretsFile = Path.Combine(secretsDir, "secrets.json");
var root = File.Exists(secretsFile) ? (JsonNode.Parse(File.ReadAllText(secretsFile)) as JsonObject ?? new JsonObject()) : new JsonObject();
root[SecretKey] = builder.ConnectionString;
File.WriteAllText(secretsFile, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine($"Đã lưu {SecretKey} vào user-secrets (tài khoản: {user}, SslMode=VerifyFull).");
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

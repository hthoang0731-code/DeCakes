using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using DeCakes.Api.Controllers;
using DeCakes.Application.Accounts;
using DeCakes.Infrastructure.Persistence;
using DeCakes.Infrastructure.Tests.MySql;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MySqlConnector;

// Công cụ phát triển riêng, không nằm trong solution hoặc entry point của API triển khai.
var root = new DirectoryInfo(AppContext.BaseDirectory);
while (root is not null && !File.Exists(Path.Combine(root.FullName, "DeCakes.slnx"))) root = root.Parent;
var repository = root?.FullName ?? throw new InvalidOperationException("Không tìm thấy repository backend.");
if (args.Length != 2 || !int.TryParse(args[1], out var parentPid))
    throw new InvalidOperationException("Công cụ này được khởi chạy bằng npm run dev:local trong FE.");
var outputRoot = Path.GetFullPath(Path.Combine(repository, "TestResults", "local-web"));
var outputDirectory = Path.GetFullPath(args[0]);
if (!outputDirectory.StartsWith(outputRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
    || !Guid.TryParseExact(Path.GetFileName(outputDirectory), "N", out _))
    throw new InvalidOperationException("Thư mục chạy local không hợp lệ.");
Directory.CreateDirectory(outputDirectory);
using var parent = Process.GetProcessById(parentPid);
var mysqlRoot = Path.GetFullPath(Path.Combine(repository, "..", ".local", "mysql84"));
var mysqlDirectory = Directory.GetDirectories(mysqlRoot, "mysql-8.4.*-winx64")
    .OrderByDescending(path => Version.Parse(Path.GetFileName(path)[6..^7])).FirstOrDefault()
    ?? throw new InvalidOperationException("Cần MySQL 8.4 ZIP tại .local/mysql84/mysql-8.4.x-winx64; xem FE/README.md.");
var tlsDirectory = Path.Combine(mysqlRoot, "tls");
var temporaryDirectory = Path.GetFullPath(Path.Combine(outputDirectory, "mysql-disposable-" + Guid.NewGuid().ToString("N")));
var dataDirectory = Path.Combine(temporaryDirectory, "data");
var stopFile = Path.Combine(outputDirectory, "stop.request");
var exitCode = 1;
Process? server = null;
MySqlConnectionStringBuilder? administrator = null;
MySqlTestDatabase? fixture = null;
LocalApiFactory? api = null;
WebApplication? mailboxServer = null;
using var cancelled = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancelled.Cancel(); };

ProcessStartInfo Command(string file, params string[] arguments)
{
    var info = new ProcessStartInfo(file)
    {
        WorkingDirectory = repository, UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardError = true, RedirectStandardOutput = true,
    };
    foreach (var argument in arguments) info.ArgumentList.Add(argument);
    return info;
}
int FreePort()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
}

try
{
    if (File.Exists(stopFile) || Directory.Exists(temporaryDirectory)
        || !temporaryDirectory.StartsWith(Path.GetFullPath(outputDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Temporary path guard failed.");
    Directory.CreateDirectory(dataDirectory);
    var mysqld = Path.Combine(mysqlDirectory, "bin", "mysqld.exe");
    using (var init = Process.Start(Command(mysqld, "--no-defaults", "--initialize-insecure",
        "--basedir=" + mysqlDirectory, "--datadir=" + dataDirectory,
        "--log-error=" + Path.Combine(temporaryDirectory, "initialize.log")))!)
    {
        var output = init.StandardOutput.ReadToEndAsync();
        var error = init.StandardError.ReadToEndAsync();
        await init.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
        await Task.WhenAll(output, error);
        if (init.ExitCode != 0) throw new InvalidOperationException("MySQL initialization failed.");
    }
    var mysqlPort = FreePort();
    server = Process.Start(Command(mysqld, "--no-defaults", "--no-monitor", "--basedir=" + mysqlDirectory,
        "--datadir=" + dataDirectory, "--port=" + mysqlPort, "--bind-address=127.0.0.1", "--mysqlx=OFF",
        "--require-secure-transport=ON", "--ssl-ca=" + Path.Combine(tlsDirectory, "ca.pem"),
        "--ssl-cert=" + Path.Combine(tlsDirectory, "server-cert.pem"),
        "--ssl-key=" + Path.Combine(tlsDirectory, "server-key.pem"),
        "--log-error=" + Path.Combine(temporaryDirectory, "server.log")))!;
    _ = server.StandardOutput.ReadToEndAsync();
    _ = server.StandardError.ReadToEndAsync();
    administrator = new MySqlConnectionStringBuilder
    {
        Server = "127.0.0.1", Port = (uint)mysqlPort, UserID = "root", Pooling = false,
        SslMode = MySqlSslMode.VerifyFull, SslCa = Path.Combine(tlsDirectory, "ca.pem"), ConnectionTimeout = 2,
    };
    var ready = false;
    for (var attempt = 0; attempt < 120 && !server.HasExited; attempt++)
    {
        try
        {
            await using var connection = new MySqlConnection(administrator.ConnectionString);
            await connection.OpenAsync();
            await using (var check = new MySqlCommand("SELECT VERSION(), @@datadir, @@bind_address, @@port", connection))
            await using (var reader = await check.ExecuteReaderAsync())
            {
                if (!await reader.ReadAsync() || !reader.GetString(0).StartsWith("8.4.", StringComparison.Ordinal)
                    || !Path.GetFullPath(reader.GetString(1)).TrimEnd('\\', '/').Equals(dataDirectory, StringComparison.OrdinalIgnoreCase)
                    || reader.GetString(2) != "127.0.0.1" || reader.GetInt32(3) != mysqlPort)
                    throw new InvalidOperationException("Local server identity guard failed.");
                Console.WriteLine("Local MySQL " + reader.GetString(0) + ": loopback/datadir/port verified, VerifyFull.");
            }
            var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            await using var change = new MySqlCommand("ALTER USER 'root'@'localhost' IDENTIFIED BY @password", connection);
            change.Parameters.AddWithValue("@password", password);
            await change.ExecuteNonQueryAsync();
            administrator.Password = password;
            ready = true;
            break;
        }
        catch (MySqlException) { await Task.Delay(250); }
    }
    if (!ready) throw new InvalidOperationException("MySQL readiness failed.");
    fixture = new MySqlTestDatabase();
    var previous = Environment.GetEnvironmentVariable(MySqlTestSettings.AdminConnectionVariable);
    try
    {
        Environment.SetEnvironmentVariable(MySqlTestSettings.AdminConnectionVariable, administrator.ConnectionString);
        await fixture.InitializeAsync();
    }
    finally { Environment.SetEnvironmentVariable(MySqlTestSettings.AdminConnectionVariable, previous); }
    if (!fixture.Available) throw new InvalidOperationException("Fixture is unavailable.");

    var backendPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    await using (var connection = new MySqlConnection(administrator.ConnectionString))
    {
        await connection.OpenAsync();
        await using var change = new MySqlCommand($"ALTER USER '{fixture.UserName}'@'%' IDENTIFIED BY @password", connection);
        change.Parameters.AddWithValue("@password", backendPassword);
        await change.ExecuteNonQueryAsync();
    }
    var backend = new MySqlConnectionStringBuilder(administrator.ConnectionString)
    {
        Database = fixture.DatabaseName, UserID = fixture.UserName, Password = backendPassword,
    };
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:DeCakesDb"] = backend.ConnectionString,
        ["Payments:PayOS:Enabled"] = "false",
        ["App:PublicBaseUrl"] = "https://localhost:5174",
    }).Build();
    var factory = new MySqlConnectionFactory(configuration);
    await using (var preflight = await factory.OpenConnectionAsync())
    {
        await using var command = new MySqlCommand("SELECT @@port, DATABASE(), CURRENT_USER()", preflight);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync() || reader.GetInt32(0) != mysqlPort
            || reader.GetString(1) != fixture.DatabaseName || reader.GetString(2) != fixture.UserName + "@%")
            throw new InvalidOperationException("Actual API connection factory guard failed.");
    }
    using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
    store.Open(OpenFlags.ReadOnly);
    var certificate = store.Certificates.Where(c => c.HasPrivateKey && c.NotBefore <= DateTime.Now && c.NotAfter > DateTime.Now
        && c.Extensions.Any(e => e.Oid?.Value == "1.3.6.1.4.1.311.84.1.1")).OrderByDescending(c => c.NotAfter).First();
    var apiPort = FreePort();
    var mailboxPort = FreePort();
    var mailbox = new LocalMailbox("https://localhost:5174");
    api = new LocalApiFactory(configuration, factory, mailbox, repository);
    api.UseKestrel(options => options.Listen(IPAddress.Loopback, apiPort, listen => listen.UseHttps(certificate)));
    api.StartServer();
    var mailboxBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing", ContentRootPath = outputDirectory });
    mailboxBuilder.Configuration.Sources.Clear();
    mailboxBuilder.Logging.ClearProviders();
    mailboxBuilder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, mailboxPort, listen => listen.UseHttps(certificate)));
    mailboxServer = mailboxBuilder.Build();
    mailboxServer.MapGet("/", (HttpContext context) => {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
        return Results.Content(mailbox.Render(), "text/html; charset=utf-8");
    });
    await mailboxServer.StartAsync();
    await File.WriteAllTextAsync(Path.Combine(outputDirectory, "manifest.json"), JsonSerializer.Serialize(new {
        ApiOrigin = $"https://localhost:{apiPort}", FrontendOrigin = "https://localhost:5174", MysqlPort = mysqlPort,
        MailboxOrigin = $"https://localhost:{mailboxPort}", Kind = "decakes-disposable-local",
        Database = fixture.DatabaseName, BackendUser = fixture.UserName, Pid = Environment.ProcessId,
        ServerVersion = fixture.ServerVersion, Target = "disposable-loopback-only", RealEmail = false,
    }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("READY isolated API and MySQL; no persistent configuration changed.");
    while (!File.Exists(stopFile) && !cancelled.IsCancellationRequested && !parent.HasExited) await Task.Delay(500);
    var summary = new {
        UserCount = await fixture.AdminScalarAsync("SELECT COUNT(*) FROM users"),
        CustomerCount = await fixture.AdminScalarAsync("SELECT COUNT(*) FROM users WHERE system_role='customer'"),
        VerifiedCount = await fixture.AdminScalarAsync("SELECT COUNT(*) FROM users WHERE email_verified_at IS NOT NULL"),
        TokenCount = await fixture.AdminScalarAsync("SELECT COUNT(*) FROM user_tokens"),
        Version = await fixture.AdminScalarAsync("SELECT MAX(version) FROM schema_migrations"),
        SessionVersion = await fixture.AdminScalarAsync("SELECT MAX(session_version) FROM users"),
        LocalEmailCount = mailbox.Count,
    };
    await File.WriteAllTextAsync(Path.Combine(outputDirectory, "FINAL_METADATA.json"), JsonSerializer.Serialize(summary));
    exitCode = 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("Local frontend host failed: " + exception.GetType().Name);
}
finally
{
    if (api is not null) await api.DisposeAsync();
    if (mailboxServer is not null) { await mailboxServer.StopAsync(); await mailboxServer.DisposeAsync(); }
    if (fixture is not null) await fixture.DisposeAsync();
    if (server is not null && !server.HasExited && administrator is not null)
    {
        try
        {
            await using var connection = new MySqlConnection(administrator.ConnectionString);
            await connection.OpenAsync();
            await using (var check = new MySqlCommand("SELECT (SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name REGEXP '^decakes_test_'), (SELECT COUNT(*) FROM mysql.user WHERE user REGEXP '^dc_test_')", connection))
            await using (var reader = await check.ExecuteReaderAsync())
            {
                await reader.ReadAsync();
                Console.WriteLine($"Fixture cleanup: {reader.GetInt64(0)} databases, {reader.GetInt64(1)} test accounts remaining.");
            }
            await using var shutdown = new MySqlCommand("SHUTDOWN", connection);
            await shutdown.ExecuteNonQueryAsync();
        }
        catch (MySqlException) { }
        try { await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (TimeoutException)
        {
            if (!server.HasExited && server.StartInfo.ArgumentList.Contains("--datadir=" + dataDirectory))
            { server.Kill(entireProcessTree: true); await server.WaitForExitAsync(); }
            exitCode = 1;
        }
    }
    server?.Dispose();
    if (Directory.Exists(temporaryDirectory))
    {
        var resolved = Path.GetFullPath(temporaryDirectory);
        if (!resolved.StartsWith(Path.GetFullPath(outputDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith("mysql-disposable-", StringComparison.Ordinal))
            throw new InvalidOperationException("Cleanup path guard failed.");
        Directory.Delete(resolved, recursive: true);
    }
    if (File.Exists(stopFile)) File.Delete(stopFile);
    Console.WriteLine("Disposable MySQL/data/private environment cleaned.");
}
return exitCode;

sealed class LocalApiFactory(IConfiguration configuration, IMySqlConnectionFactory factory, IAccountEmailSender mailbox, string repository)
    : WebApplicationFactory<HealthController>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseContentRoot(Path.Combine(repository, "src", "DeCakes.Api"));
        builder.ConfigureAppConfiguration((_, config) => { config.Sources.Clear(); config.AddConfiguration(configuration); });
        builder.ConfigureLogging(logging => {
            logging.ClearProviders();
            logging.AddConsole();
            logging.AddFilter((category, level) => category == "DeCakes.Infrastructure.Email.ResendAccountEmailSender" && level >= LogLevel.Information);
        });
        builder.ConfigureTestServices(services => {
            services.RemoveAll<IMySqlConnectionFactory>();
            services.AddSingleton(factory);
            services.RemoveAll<IAccountEmailSender>();
            services.AddSingleton(mailbox);
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
        });
    }
}

// Chỉ tồn tại trong tiến trình local; không gửi ra Internet và không ghi token ra file/log.
sealed class LocalMailbox(string frontendOrigin) : IAccountEmailSender
{
    private readonly ConcurrentQueue<Message> _messages = new();
    private int _count;
    public int Count => _count;

    public Task SendEmailVerificationAsync(string toEmail, string fullName, string token, DateTime expiresAt, CancellationToken cancellationToken)
        => Add("Xác minh email", "verify-email", toEmail, token, expiresAt, cancellationToken);
    public Task SendPasswordResetAsync(string toEmail, string fullName, string token, DateTime expiresAt, CancellationToken cancellationToken)
        => Add("Đặt lại mật khẩu", "reset-password", toEmail, token, expiresAt, cancellationToken);
    public Task SendStaffInvitationAsync(string toEmail, string bakeryName, string token, DateTime expiresAt, CancellationToken cancellationToken)
        => Add("Lời mời nhân viên", "accept-invitation", toEmail, token, expiresAt, cancellationToken);

    private Task Add(string title, string route, string recipient, string token, DateTime expiresAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _messages.Enqueue(new Message(Interlocked.Increment(ref _count), title, recipient, frontendOrigin + "/" + route + "#token=" + token, expiresAt));
        while (_messages.Count > 100) _messages.TryDequeue(out _);
        return Task.CompletedTask;
    }

    public string Render()
    {
        static string E(string value) => WebUtility.HtmlEncode(value);
        var messages = string.Join("", _messages.ToArray().Reverse().Select(m =>
            $"<article><h2>{E(m.Title)} #{m.Id}</h2><p>Đến: {E(m.Recipient)}</p><p>Hạn dùng: {m.ExpiresAt:yyyy-MM-dd HH:mm:ss} UTC</p>"
            + $"<a href=\"{E(m.Link)}\" rel=\"noreferrer\">Mở liên kết {E(m.Title.ToLowerInvariant())} #{m.Id}</a></article>"));
        return "<!doctype html><html lang=\"vi\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
            + "<title>Hộp thư kiểm thử local | DeCakes</title><style>body{font:1rem/1.6 system-ui;max-width:50rem;margin:2rem auto;padding:1rem}article{border:1px solid #aaa;padding:1rem;margin:1rem 0}a{overflow-wrap:anywhere}</style>"
            + "<h1>Hộp thư kiểm thử local</h1><p>Chỉ có dữ liệu thử trên máy. Không gửi email thật. Tải lại trang để xem thư mới. Dừng môi trường sẽ xóa toàn bộ thư và dữ liệu thử.</p>"
            + (messages.Length == 0 ? "<p>Chưa có thư.</p>" : messages) + "</html>";
    }

    private sealed record Message(int Id, string Title, string Recipient, string Link, DateTime ExpiresAt);
}

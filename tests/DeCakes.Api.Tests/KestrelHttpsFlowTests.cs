using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using DeCakes.Api.Authentication;
using DeCakes.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;

namespace DeCakes.Api.Tests;

/// <summary>
/// Luồng cookie trên Kestrel HTTPS thật (không phải TestServer) với store user trong bộ nhớ: không chạm database.
/// Client chỉ tin chứng chỉ phát triển ASP.NET Core làm root và vẫn kiểm tra hostname; không tắt xác minh TLS.
/// </summary>
public class KestrelHttpsFlowTests
{
    private const string Password = "mat-khau-du-dai-1";

    [DevCertFact]
    public async Task Register_login_me_logout_over_real_https()
    {
        var certificate = DevCertificate.Find()!;
        var port = FreePort();
        using var app = new TestApp();
        app.UseKestrel(options => options.Listen(IPAddress.Loopback, port, listen => listen.UseHttps(certificate)));
        app.StartServer();

        using var handler = new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, serverCertificate, _, errors) =>
                TrustOnlyDevCertificate(serverCertificate, errors, certificate),
        };
        using var client = new ApiClient(new HttpClient(handler) { BaseAddress = new Uri($"https://localhost:{port}") });

        // Đăng ký: 201, chưa đăng nhập.
        var register = await client.PostAsync(
            "/api/auth/register", new { email = "kestrel@example.com", password = Password, fullName = "Kestrel" }, await client.GetCsrfTokenAsync());
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        Assert.Null(client.SetCookieFor(AuthDefaults.AuthCookieName));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);

        // Đăng nhập: cookie đúng cờ qua HTTPS thật.
        var login = await client.LoginAsync("kestrel@example.com", Password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = client.SetCookieFor(AuthDefaults.AuthCookieName)!.ToLowerInvariant();
        Assert.Contains("; secure", cookie);
        Assert.Contains("; httponly", cookie);
        Assert.Contains("samesite=lax", cookie);
        Assert.Contains("path=/", cookie);
        Assert.DoesNotContain("domain=", cookie);
        Assert.DoesNotContain("expires=", cookie);

        // Lấy CSRF token mới sau khi đăng nhập, rồi /me.
        var tokenAfterLogin = await client.GetCsrfTokenAsync();
        var me = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using (var body = JsonDocument.Parse(await me.Content.ReadAsStringAsync()))
        {
            Assert.Equal("kestrel@example.com", body.RootElement.GetProperty("email").GetString());
        }

        // Đăng xuất bằng token mới, rồi /me trả 401.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null, tokenAfterLogin)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    private static bool TrustOnlyDevCertificate(X509Certificate2? serverCertificate, SslPolicyErrors errors, X509Certificate2 trustedRoot)
    {
        // Lỗi khác ngoài chuỗi chứng chỉ (ví dụ sai hostname) luôn bị từ chối.
        if (serverCertificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(X509CertificateLoader.LoadCertificate(trustedRoot.RawData));
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(serverCertificate);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

internal static class DevCertificate
{
    // OID đánh dấu chứng chỉ phát triển HTTPS của ASP.NET Core (dotnet dev-certs).
    private const string AspNetHttpsOid = "1.3.6.1.4.1.311.84.1.1";

    public static X509Certificate2? Find()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        var now = DateTime.Now;
        return store.Certificates
            .Where(c => c.HasPrivateKey && c.NotBefore <= now && c.NotAfter > now
                        && c.Extensions.Any(e => e.Oid?.Value == AspNetHttpsOid))
            .OrderByDescending(c => c.NotAfter)
            .FirstOrDefault();
    }
}

public sealed class DevCertFactAttribute : FactAttribute
{
    public DevCertFactAttribute()
    {
        if (DevCertificate.Find() is null)
        {
            Skip = "Không có chứng chỉ phát triển HTTPS. Chạy: dotnet dev-certs https";
        }
    }
}

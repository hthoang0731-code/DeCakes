using System.Net.Http.Json;
using System.Text.Json;

namespace DeCakes.Api.Tests.Infrastructure;

/// <summary>HttpClient kèm cookie jar tự quản lý, để kiểm tra trực tiếp header Set-Cookie.</summary>
public sealed class ApiClient(HttpClient http) : IDisposable
{
    private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);

    public IReadOnlyList<string> LastSetCookies { get; private set; } = [];

    public IReadOnlyDictionary<string, string> Cookies => _cookies;

    public void SetCookie(string name, string value) => _cookies[name] = value;

    public void RemoveCookie(string name) => _cookies.Remove(name);

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null, string? csrfToken = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (csrfToken is not null) request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        if (_cookies.Count > 0) request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(c => $"{c.Key}={c.Value}")));

        var response = await http.SendAsync(request);
        LastSetCookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToList() : [];
        foreach (var header in LastSetCookies)
        {
            var pair = header.Split(';', 2)[0];
            var separator = pair.IndexOf('=');
            var name = pair[..separator];
            var value = pair[(separator + 1)..];
            var expired = header.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(value) || expired) _cookies.Remove(name);
            else _cookies[name] = value;
        }

        return response;
    }

    public Task<HttpResponseMessage> GetAsync(string url) => SendAsync(HttpMethod.Get, url);

    public Task<HttpResponseMessage> PostAsync(string url, object? body, string? csrfToken) =>
        SendAsync(HttpMethod.Post, url, body ?? new { }, csrfToken);

    public async Task<string> GetCsrfTokenAsync()
    {
        var response = await GetAsync("/api/auth/csrf");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("token").GetString()!;
    }

    public async Task<HttpResponseMessage> LoginAsync(string email, string password) =>
        await PostAsync("/api/auth/login", new { email, password }, await GetCsrfTokenAsync());

    public string? SetCookieFor(string cookieName) =>
        LastSetCookies.FirstOrDefault(h => h.StartsWith(cookieName + "=", StringComparison.Ordinal));

    public void Dispose() => http.Dispose();
}

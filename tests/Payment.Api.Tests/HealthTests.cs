using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Payment.Api.Tests;

// ─── Test Factory ───────────────────────────────────────────────
// Program.cs minimal API; `builder.Configuration["Jwt:Key"]` çağrısı
// CreateBuilder()'dan hemen sonra çalışır — bu noktada WebApplicationFactory'nin
// ConfigureAppConfiguration callback'i HENÜZ devreye girmemiştir, dolayısıyla
// in-memory config enjeksiyonu çok geç kalır. Çözüm: WebApplication.CreateBuilder
// varsayılan olarak environment variable'ları okuduğu için config'i process env
// üzerinden veriyoruz. Static ctor bir kez çalışır, tüm fixture instance'ları paylaşır.
public sealed class TestApiFactory : WebApplicationFactory<Program>
{
    public const string JwtKey = "test-secret-key-min-32-bytes-1234567890!!"; // 40 byte
    public const string JwtIssuer = "test-api";
    public const string JwtAudience = "test-api-clients";
    public const string Username = "tester";
    public const string Password = "P@ssw0rd!Test";

    static TestApiFactory()
    {
        // Deterministik salt — paralel test class'larında tutarlı hash üretmek için.
        var salt = new byte[16]
        {
            0xA1, 0xB2, 0xC3, 0xD4, 0xE5, 0xF6, 0x07, 0x18,
            0x29, 0x3A, 0x4B, 0x5C, 0x6D, 0x7E, 0x8F, 0x90
        };
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Password, salt, iterations: 100_000,
            HashAlgorithmName.SHA256, outputLength: 32);

        Environment.SetEnvironmentVariable("Jwt__Key", JwtKey);
        Environment.SetEnvironmentVariable("Jwt__Issuer", JwtIssuer);
        Environment.SetEnvironmentVariable("Jwt__Audience", JwtAudience);
        Environment.SetEnvironmentVariable("TestUser__Username", Username);
        Environment.SetEnvironmentVariable("TestUser__PasswordSalt", Convert.ToBase64String(salt));
        Environment.SetEnvironmentVariable("TestUser__PasswordHash", Convert.ToBase64String(hash));
        Environment.SetEnvironmentVariable("Cors__AllowedOrigins__0", "https://localhost");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development"); // HTTPS metadata zorunluluğunu kapatır
    }

    /// <summary>Test'lerde kullanmak için manuel JWT üretici.</summary>
    public string CreateJwt(string username = Username)
    {
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: JwtIssuer,
            audience: JwtAudience,
            claims: new[] { new Claim(ClaimTypes.Name, username) },
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateJwt());
        return client;
    }
}

// ─── Health & Meta ──────────────────────────────────────────────
public sealed class HealthTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;
    public HealthTests(TestApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Health_endpoint_returns_success()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Version_endpoint_returns_ok()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/version");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<VersionDto>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body!.Version));
    }

    private sealed record VersionDto(string Version);
}

// ─── Authentication & Authorization ─────────────────────────────
public sealed class AuthTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;
    public AuthTests(TestApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Protected_endpoint_without_token_returns_unauthorized()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/payments");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoint_with_token_returns_ok()
    {
        var client = _factory.CreateAuthenticatedClient();
        var response = await client.GetAsync("/api/payments");
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Login_with_valid_credentials_returns_token()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = TestApiFactory.Username,
            password = TestApiFactory.Password
        });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<TokenDto>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body!.Token));
    }

    [Fact]
    public async Task Login_with_invalid_password_returns_unauthorized()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = TestApiFactory.Username,
            password = "wrong-password-123"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_with_unknown_user_returns_unauthorized()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "nobody",
            password = TestApiFactory.Password
        });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_with_short_password_returns_validation_problem()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "x",
            password = "short"   // < 8 karakter
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Secure_endpoint_returns_username_from_token()
    {
        var client = _factory.CreateAuthenticatedClient();
        var response = await client.GetAsync("/api/secure");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<SecureDto>();
        Assert.Equal(TestApiFactory.Username, body!.User);
    }

    private sealed record TokenDto(string Token);
    private sealed record SecureDto(string Message, string User);
}

// ─── Payments CRUD ──────────────────────────────────────────────
public sealed class PaymentsTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;
    public PaymentsTests(TestApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Create_payment_returns_created_with_location()
    {
        var client = _factory.CreateAuthenticatedClient();
        var response = await client.PostAsJsonAsync("/api/payments", new
        {
            amount = 99.99m,
            currency = "USD"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        var payment = await response.Content.ReadFromJsonAsync<PaymentDto>();
        Assert.NotNull(payment);
        Assert.Equal(99.99m, payment!.Amount);
        Assert.Equal("USD", payment.Currency);
        Assert.Equal("AUTHORIZED", payment.Status);
    }

    [Fact]
    public async Task Get_existing_payment_returns_ok()
    {
        var client = _factory.CreateAuthenticatedClient();

        var created = await client.PostAsJsonAsync("/api/payments",
            new { amount = 10m, currency = "EUR" });
        var payment = await created.Content.ReadFromJsonAsync<PaymentDto>();

        var response = await client.GetAsync($"/api/payments/{payment!.Id}");
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Get_nonexistent_payment_returns_not_found()
    {
        var client = _factory.CreateAuthenticatedClient();
        var response = await client.GetAsync("/api/payments/doesnotexist");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("../etc")]
    [InlineData("with spaces")]
    [InlineData("inj'ect")]
    public async Task Get_payment_with_invalid_id_returns_bad_request(string id)
    {
        var client = _factory.CreateAuthenticatedClient();
        var response = await client.GetAsync($"/api/payments/{Uri.EscapeDataString(id)}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(0, "USD")]           // amount <= 0
    [InlineData(-5, "USD")]
    [InlineData(10, "US")]            // currency 3 harf değil
    [InlineData(10, "USDD")]
    [InlineData(10, "12X")]           // sayı içeriyor
    public async Task Create_payment_with_invalid_data_returns_bad_request(decimal amount, string currency)
    {
        var client = _factory.CreateAuthenticatedClient();
        var response = await client.PostAsJsonAsync("/api/payments", new { amount, currency });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Update_payment_status_returns_updated_payment()
    {
        var client = _factory.CreateAuthenticatedClient();
        var created = await client.PostAsJsonAsync("/api/payments",
            new { amount = 50m, currency = "TRY" });
        var payment = await created.Content.ReadFromJsonAsync<PaymentDto>();

        var response = await client.PutAsJsonAsync($"/api/payments/{payment!.Id}",
            new { status = "REFUNDED" });

        response.EnsureSuccessStatusCode();
        var updated = await response.Content.ReadFromJsonAsync<PaymentDto>();
        Assert.Equal("REFUNDED", updated!.Status);
    }

    [Fact]
    public async Task Update_payment_with_invalid_status_returns_bad_request()
    {
        var client = _factory.CreateAuthenticatedClient();
        var created = await client.PostAsJsonAsync("/api/payments",
            new { amount = 50m, currency = "TRY" });
        var payment = await created.Content.ReadFromJsonAsync<PaymentDto>();

        var response = await client.PutAsJsonAsync($"/api/payments/{payment!.Id}",
            new { status = "HACKED" }); // allow-list dışı

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Delete_payment_returns_no_content_then_not_found()
    {
        var client = _factory.CreateAuthenticatedClient();
        var created = await client.PostAsJsonAsync("/api/payments",
            new { amount = 1m, currency = "USD" });
        var payment = await created.Content.ReadFromJsonAsync<PaymentDto>();

        var del = await client.DeleteAsync($"/api/payments/{payment!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var get = await client.GetAsync($"/api/payments/{payment.Id}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task List_payments_with_pagination_clamps_pagesize()
    {
        var client = _factory.CreateAuthenticatedClient();

        // 3 payment ekle
        for (var i = 0; i < 3; i++)
            await client.PostAsJsonAsync("/api/payments",
                new { amount = i + 1m, currency = "USD" });

        // pageSize=999 verilse bile 100'e clamp edilmeli
        var response = await client.GetAsync("/api/payments?pageSize=999");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<PageDto>();
        Assert.Equal(100, body!.PageSize);
        Assert.True(body.Total >= 3);
    }

    [Fact]
    public async Task List_payments_with_invalid_status_returns_bad_request()
    {
        var client = _factory.CreateAuthenticatedClient();
        var response = await client.GetAsync("/api/payments?status=INVALID");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_payments_with_valid_status_filters_results()
    {
        var client = _factory.CreateAuthenticatedClient();
        await client.PostAsJsonAsync("/api/payments",
            new { amount = 5m, currency = "USD" });

        var response = await client.GetAsync("/api/payments?status=AUTHORIZED");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<PageDto>();
        Assert.All(body!.Items, p => Assert.Equal("AUTHORIZED", p.Status));
    }

    private sealed record PaymentDto(string Id, decimal Amount, string Currency,
        string Status, DateTimeOffset CheckedAt);

    private sealed record PageDto(int Page, int PageSize, int Total, PaymentDto[] Items);
}

// ─── Orders ─────────────────────────────────────────────────────
public sealed class OrdersTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;
    public OrdersTests(TestApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Create_order_returns_created()
    {
        var client = _factory.CreateAuthenticatedClient();
        var response = await client.PostAsJsonAsync("/api/orders", new
        {
            userId = "user-1",
            items = new[] { "sku-1", "sku-2" }
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = await response.Content.ReadFromJsonAsync<OrderDto>();
        Assert.Equal("PENDING", order!.Status);
        Assert.Equal(2, order.Items.Length);
    }

    [Fact]
    public async Task Create_order_with_empty_items_returns_bad_request()
    {
        var client = _factory.CreateAuthenticatedClient();
        var response = await client.PostAsJsonAsync("/api/orders", new
        {
            userId = "user-1",
            items = Array.Empty<string>()
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_order_without_user_returns_bad_request()
    {
        var client = _factory.CreateAuthenticatedClient();
        var response = await client.PostAsJsonAsync("/api/orders", new
        {
            userId = "",
            items = new[] { "sku-1" }
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private sealed record OrderDto(string Id, string UserId, string[] Items, string Status);
}

// ─── Security Headers ───────────────────────────────────────────
public sealed class SecurityHeaderTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;
    public SecurityHeaderTests(TestApiFactory factory) => _factory = factory;

    [Theory]
    [InlineData("X-Content-Type-Options", "nosniff")]
    [InlineData("X-Frame-Options", "DENY")]
    [InlineData("Referrer-Policy", "no-referrer")]
    public async Task Response_contains_security_header(string name, string expected)
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/version");

        Assert.True(response.Headers.TryGetValues(name, out var values),
            $"Expected header {name} missing.");
        Assert.Contains(expected, values!);
    }

    [Fact]
    public async Task Response_contains_csp_header()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/version");
        Assert.True(response.Headers.Contains("Content-Security-Policy"));
    }
}
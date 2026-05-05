using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// ─── Configuration & Secrets ────────────────────────────────────
// Hard-coded secret YOK. JWT key configuration / environment'tan alınır.
// dotnet user-secrets set "Jwt:Key" "..." veya environment variable.
var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("Jwt:Key configuration is required.");
if (Encoding.UTF8.GetByteCount(jwtKey) < 32)
    throw new InvalidOperationException("Jwt:Key must be at least 256 bits (32 bytes).");

var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "test-api";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "test-api-clients";

// Test kullanıcısının parolasını da configuration'dan oku (asla hard-code etme)
var testUsername = builder.Configuration["TestUser:Username"]
    ?? throw new InvalidOperationException("TestUser:Username is required.");
var testPasswordHash = builder.Configuration["TestUser:PasswordHash"]
    ?? throw new InvalidOperationException("TestUser:PasswordHash is required (PBKDF2 base64).");
var testPasswordSalt = builder.Configuration["TestUser:PasswordSalt"]
    ?? throw new InvalidOperationException("TestUser:PasswordSalt is required (base64).");

// ─── Services ───────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddHealthChecks();
builder.Services.AddProblemDetails();

// CORS — wildcard YOK, sadece izinli originler
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? Array.Empty<string>();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins)
                  .WithMethods("GET", "POST", "PUT", "DELETE")
                  .WithHeaders("Authorization", "Content-Type")
                  .AllowCredentials();
        }
    });
});

// Rate limiting — brute-force ve DoS'a karşı
builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    options.AddPolicy("login", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// JWT Authentication
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });
builder.Services.AddAuthorization();

// HSTS
builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(365);
    options.IncludeSubDomains = true;
    options.Preload = true;
});

var app = builder.Build();

// ─── Middleware Pipeline ────────────────────────────────────────
// Global exception handler — stack trace'i asla client'a sızdırma
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var feature = context.Features.Get<IExceptionHandlerFeature>();
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        if (feature?.Error is not null)
            logger.LogError(feature.Error, "Unhandled exception on {Path}", context.Request.Path);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "An unexpected error occurred.",
            Type = "https://tools.ietf.org/html/rfc7231#section-6.6.1"
        });
    });
});

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}
app.UseHttpsRedirection();

// Güvenlik header'ları
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
    ctx.Response.Headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
    ctx.Response.Headers.Remove("Server");
    await next();
});

app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// ─── Stores ─────────────────────────────────────────────────────
var payments = new ConcurrentDictionary<string, Payment>();
var orders = new ConcurrentDictionary<string, Order>();

// ─── Health & Meta ──────────────────────────────────────────────
app.MapHealthChecks("/health");
app.MapGet("/api/version", () => Results.Ok(new { version = "1.0.0" }));

// ─── Payments ───────────────────────────────────────────────────
var paymentsGroup = app.MapGroup("/api/payments").RequireAuthorization();

paymentsGroup.MapGet("/", (string? status, int page = 1, int pageSize = 10) =>
{
    page = Math.Max(1, page);
    pageSize = Math.Clamp(pageSize, 1, 100); // unbounded query'i engelle

    var query = payments.Values.AsEnumerable();
    if (!string.IsNullOrWhiteSpace(status))
    {
        // Sabit allow-list — kullanıcı girdisini doğrudan filtreye verme
        var allowed = new[] { "AUTHORIZED", "PENDING", "FAILED", "REFUNDED" };
        if (!allowed.Contains(status, StringComparer.OrdinalIgnoreCase))
            return Results.BadRequest(new ProblemDetails { Title = "Invalid status filter" });

        query = query.Where(p => p.Status.Equals(status, StringComparison.OrdinalIgnoreCase));
    }

    var total = query.Count();
    var items = query.Skip((page - 1) * pageSize).Take(pageSize).ToList();
    return Results.Ok(new { page, pageSize, total, items });
});

paymentsGroup.MapGet("/{id}", (string id) =>
{
    if (!IsValidId(id)) return Results.BadRequest(new ProblemDetails { Title = "Invalid id" });
    return payments.TryGetValue(id, out var p)
        ? Results.Ok(p)
        : Results.NotFound();
});

paymentsGroup.MapPost("/", (CreatePaymentRequest req) =>
{
    var validation = Validate(req);
    if (validation is not null) return validation;

    var id = NewId();
    var payment = new Payment(id, req.Amount, req.Currency!.ToUpperInvariant(),
        "AUTHORIZED", DateTimeOffset.UtcNow);
    payments[id] = payment;
    return Results.Created($"/api/payments/{id}", payment);
});

paymentsGroup.MapPut("/{id}", (string id, UpdatePaymentRequest req) =>
{
    if (!IsValidId(id)) return Results.BadRequest(new ProblemDetails { Title = "Invalid id" });
    var validation = Validate(req);
    if (validation is not null) return validation;

    if (!payments.TryGetValue(id, out var existing))
        return Results.NotFound();

    var updated = existing with { Status = req.Status! };
    payments[id] = updated;
    return Results.Ok(updated);
});

paymentsGroup.MapDelete("/{id}", (string id) =>
{
    if (!IsValidId(id)) return Results.BadRequest(new ProblemDetails { Title = "Invalid id" });
    return payments.TryRemove(id, out _) ? Results.NoContent() : Results.NotFound();
});

// ─── Orders ─────────────────────────────────────────────────────
var ordersGroup = app.MapGroup("/api/orders").RequireAuthorization();

ordersGroup.MapGet("/", () => Results.Ok(orders.Values));

ordersGroup.MapGet("/{id}", (string id) =>
{
    if (!IsValidId(id)) return Results.BadRequest(new ProblemDetails { Title = "Invalid id" });
    return orders.TryGetValue(id, out var o) ? Results.Ok(o) : Results.NotFound();
});

ordersGroup.MapPost("/", (CreateOrderRequest req) =>
{
    var validation = Validate(req);
    if (validation is not null) return validation;

    var id = NewId();
    var order = new Order(id, req.UserId!, req.Items!, "PENDING");
    orders[id] = order;
    return Results.Created($"/api/orders/{id}", order);
});

// ─── Auth ───────────────────────────────────────────────────────
app.MapPost("/api/auth/login", (LoginRequest req) =>
{
    var validation = Validate(req);
    if (validation is not null) return validation;

    // Sabit-zamanlı karşılaştırma (timing attack'a karşı)
    var usernameOk = CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(req.Username!),
        Encoding.UTF8.GetBytes(testUsername));

    var saltBytes = Convert.FromBase64String(testPasswordSalt);
    var expectedHash = Convert.FromBase64String(testPasswordHash);
    var actualHash = Rfc2898DeriveBytes.Pbkdf2(
        req.Password!, saltBytes, iterations: 100_000,
        HashAlgorithmName.SHA256, expectedHash.Length);
    var passwordOk = CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);

    if (!usernameOk || !passwordOk)
        return Results.Unauthorized();

    var token = IssueJwt(req.Username!, jwtKey, jwtIssuer, jwtAudience);
    return Results.Ok(new { token });
}).RequireRateLimiting("login");

app.MapGet("/api/secure", (ClaimsPrincipal user) =>
    Results.Ok(new { message = "Authorized", user = user.Identity?.Name }))
   .RequireAuthorization();

app.Run();

// ─── Helpers ────────────────────────────────────────────────────
static string NewId() => Guid.NewGuid().ToString("N")[..12];

static bool IsValidId(string? id) =>
    !string.IsNullOrEmpty(id) && id.Length <= 64 &&
    id.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_');

static IResult? Validate<T>(T model) where T : class
{
    var ctx = new ValidationContext(model);
    var results = new List<ValidationResult>();
    if (Validator.TryValidateObject(model, ctx, results, validateAllProperties: true))
        return null;

    return Results.ValidationProblem(
        results.ToDictionary(
            r => r.MemberNames.FirstOrDefault() ?? "",
            r => new[] { r.ErrorMessage ?? "Invalid" }));
}

static string IssueJwt(string username, string key, string issuer, string audience)
{
    var creds = new SigningCredentials(
        new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
        SecurityAlgorithms.HmacSha256);

    var token = new JwtSecurityToken(
        issuer: issuer,
        audience: audience,
        claims: new[] { new Claim(ClaimTypes.Name, username) },
        notBefore: DateTime.UtcNow,
        expires: DateTime.UtcNow.AddMinutes(15),
        signingCredentials: creds);

    return new JwtSecurityTokenHandler().WriteToken(token);
}

// ─── Models ─────────────────────────────────────────────────────
public record Payment(string Id, decimal Amount, string Currency, string Status, DateTimeOffset CheckedAt);
public record Order(string Id, string UserId, string[] Items, string Status);

public record CreatePaymentRequest(
    [property: Range(0.01, 1_000_000)] decimal Amount,
    [property: Required, RegularExpression("^[A-Za-z]{3}$")] string? Currency);

public record UpdatePaymentRequest(
    [property: Required, RegularExpression("^(AUTHORIZED|PENDING|FAILED|REFUNDED)$")] string? Status);

public record CreateOrderRequest(
    [property: Required, StringLength(64, MinimumLength = 1)] string? UserId,
    [property: Required, MinLength(1), MaxLength(50)] string[]? Items);

public record LoginRequest(
    [property: Required, StringLength(64, MinimumLength = 1)] string? Username,
    [property: Required, StringLength(128, MinimumLength = 8)] string? Password);

public partial class Program;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddHealthChecks();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();
app.UseCors();

// In-memory store (test amaçlı)
var payments = new ConcurrentDictionary<string, Payment>();
var orders = new ConcurrentDictionary<string, Order>();

// Seed data
payments.TryAdd("p1", new Payment("p1", 100.50m, "USD", "AUTHORIZED", DateTimeOffset.UtcNow));
payments.TryAdd("p2", new Payment("p2", 250.00m, "EUR", "PENDING", DateTimeOffset.UtcNow));
orders.TryAdd("o1", new Order("o1", "user-1", new[] { "sku-1", "sku-2" }, "SHIPPED"));

// ─── Health & Meta ──────────────────────────────────────────────
app.MapHealthChecks("/health");

app.MapGet("/", () => Results.Ok(new
{
    service = "test-api",
    version = "1.0.0",
    time = DateTimeOffset.UtcNow,
    endpoints = new[]
    {
        "GET    /health",
        "GET    /api/payments",
        "GET    /api/payments/{id}",
        "POST   /api/payments",
        "PUT    /api/payments/{id}",
        "DELETE /api/payments/{id}",
        "GET    /api/orders",
        "GET    /api/orders/{id}",
        "POST   /api/orders",
        "GET    /api/echo?msg=...",
        "GET    /api/delay/{ms}",
        "GET    /api/status/{code}",
        "GET    /api/error",
        "GET    /api/random",
        "POST   /api/auth/login",
        "GET    /api/secure",
    }
}));

app.MapGet("/api/version", () => Results.Ok(new { version = "1.0.0", build = "test" }));

// ─── Payments CRUD ──────────────────────────────────────────────
app.MapGet("/api/payments", (string? status, int page = 1, int pageSize = 10) =>
{
    var query = payments.Values.AsEnumerable();
    if (!string.IsNullOrWhiteSpace(status))
        query = query.Where(p => p.Status.Equals(status, StringComparison.OrdinalIgnoreCase));

    var total = query.Count();
    var items = query.Skip((page - 1) * pageSize).Take(pageSize).ToList();

    return Results.Ok(new { page, pageSize, total, items });
});

app.MapGet("/api/payments/{id}", (string id) =>
    payments.TryGetValue(id, out var p)
        ? Results.Ok(p)
        : Results.NotFound(new { error = "Payment not found", id }));

app.MapPost("/api/payments", (CreatePaymentRequest req) =>
{
    if (req.Amount <= 0)
        return Results.BadRequest(new { error = "Amount must be > 0" });

    var id = Guid.NewGuid().ToString("N")[..8];
    var payment = new Payment(id, req.Amount, req.Currency ?? "USD", "AUTHORIZED", DateTimeOffset.UtcNow);
    payments[id] = payment;
    return Results.Created($"/api/payments/{id}", payment);
});

app.MapPut("/api/payments/{id}", (string id, UpdatePaymentRequest req) =>
{
    if (!payments.TryGetValue(id, out var existing))
        return Results.NotFound(new { error = "Payment not found", id });

    var updated = existing with { Status = req.Status ?? existing.Status };
    payments[id] = updated;
    return Results.Ok(updated);
});

app.MapDelete("/api/payments/{id}", (string id) =>
    payments.TryRemove(id, out _)
        ? Results.NoContent()
        : Results.NotFound(new { error = "Payment not found", id }));

// ─── Orders ─────────────────────────────────────────────────────
app.MapGet("/api/orders", () => Results.Ok(orders.Values));

app.MapGet("/api/orders/{id}", (string id) =>
    orders.TryGetValue(id, out var o)
        ? Results.Ok(o)
        : Results.NotFound(new { error = "Order not found", id }));

app.MapPost("/api/orders", (CreateOrderRequest req) =>
{
    var id = Guid.NewGuid().ToString("N")[..8];
    var order = new Order(id, req.UserId, req.Items, "PENDING");
    orders[id] = order;
    return Results.Created($"/api/orders/{id}", order);
});

// ─── Test Utility Endpoints ─────────────────────────────────────
app.MapGet("/api/echo", (string? msg) => Results.Ok(new { msg, at = DateTimeOffset.UtcNow }));

app.MapPost("/api/echo", (HttpRequest req) => Results.Ok(new
{
    method = req.Method,
    headers = req.Headers.ToDictionary(h => h.Key, h => h.Value.ToString()),
    query = req.Query.ToDictionary(q => q.Key, q => q.Value.ToString())
}));

app.MapGet("/api/delay/{ms:int}", async (int ms) =>
{
    ms = Math.Clamp(ms, 0, 30_000);
    await Task.Delay(ms);
    return Results.Ok(new { delayedMs = ms });
});

app.MapGet("/api/status/{code:int}", (int code) =>
    Results.StatusCode(code));

app.MapGet("/api/error", () =>
{
    throw new InvalidOperationException("Bilerek fırlatılan test hatası");
});

app.MapGet("/api/random", () => Results.Ok(new
{
    guid = Guid.NewGuid(),
    number = Random.Shared.Next(1, 1000),
    boolean = Random.Shared.Next(0, 2) == 1,
    timestamp = DateTimeOffset.UtcNow
}));

// ─── Fake Auth ──────────────────────────────────────────────────
app.MapPost("/api/auth/login", (LoginRequest req) =>
{
    if (req.Username == "test" && req.Password == "test")
        return Results.Ok(new { token = "fake-jwt-token-" + Guid.NewGuid().ToString("N")[..16] });
    return Results.Unauthorized();
});

app.MapGet("/api/secure", (HttpRequest req) =>
{
    var auth = req.Headers.Authorization.ToString();
    if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        return Results.Unauthorized();
    return Results.Ok(new { message = "Yetkili erişim", user = "test" });
});

app.Run();

// ─── Records ────────────────────────────────────────────────────
public record Payment(string Id, decimal Amount, string Currency, string Status, DateTimeOffset CheckedAt);
public record Order(string Id, string UserId, string[] Items, string Status);
public record CreatePaymentRequest(decimal Amount, string? Currency);
public record UpdatePaymentRequest(string? Status);
public record CreateOrderRequest(string UserId, string[] Items);
public record LoginRequest(string Username, string Password);

public partial class Program;
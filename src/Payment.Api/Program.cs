var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapHealthChecks("/health");
app.MapGet("/api/payments/{id}", (string id) =>
{
    // Simulate fetching payment status from a database or external service
    return Results.Ok(new
    {
        id,
        status = "AUTHORIZED",
        checkedAt = DateTimeOffset.UtcNow
    });
});

app.Run();

public partial class Program;

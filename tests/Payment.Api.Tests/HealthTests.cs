using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Payment.Api.Tests;

public sealed class HealthTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }
    // This test verifies that the /health endpoint is accessible and returns a successful status code.

    [Fact]
    public async Task Health_endpoint_returns_success()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");
        response.EnsureSuccessStatusCode();
    }
}
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace RealEstate.Api.Controllers;

public sealed record HealthResponse(string Status, string Database, DateTime TimeUtc);

[ApiController]
[Route("api/v1/health")]
[AllowAnonymous]
public class HealthController(HealthCheckService health) : ControllerBase
{
    /// <summary>Liveness and database connectivity. Returns 503 when the database is unreachable.</summary>
    [HttpGet]
    [ProducesResponseType<HealthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<HealthResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var report = await health.CheckHealthAsync(ct);
        var db = report.Entries.TryGetValue("database", out var e) ? e.Status.ToString() : "Unknown";
        var body = new HealthResponse(report.Status.ToString(), db, DateTime.UtcNow);
        return report.Status == HealthStatus.Healthy ? Ok(body) : StatusCode(StatusCodes.Status503ServiceUnavailable, body);
    }
}

using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZenLead.Application.Email;
using ZenLead.Infrastructure.Jobs;
using ZenLead.Infrastructure.Persistence;
using ZenLead.Infrastructure.Testing;

namespace ZenLead.Api.Controllers.V1.Test;

/// <summary>
/// Test-support endpoints for automated E2E runs (T00): reset the database, seed scenarios, run recorded jobs, move the clock.
/// This controller only exists in the E2E environment: <see cref="TestSupportControllerGate"/> removes it everywhere else, so the
/// routes 404. It is anonymous but only answers loopback callers.
/// </summary>
[ApiController]
[Route("api/v1/test")]
[AllowAnonymous]
[LoopbackOnly]
public class TestSupportController(
    ZenLeadDbContext db, SeedScenarios seeds, RecordedJobQueue jobs, RecordedJobRunner runner, AdjustableTimeProvider clock,
    IOptions<EmailOptions> email, PickupDirectory pickup) : ControllerBase
{
    /// <summary>Drops and re-creates the E2E database, clears the pickup folder and the job queue, and resets the clock.</summary>
    [HttpPost("reset")]
    public async Task<IActionResult> Reset(CancellationToken ct)
    {
        await db.Database.EnsureDeletedAsync(ct);
        if (db.Database.IsSqlServer()) await db.Database.MigrateAsync(ct);     // the real E2E database (LocalDB)
        else await db.Database.EnsureCreatedAsync(ct);

        if (Directory.Exists(pickup.Path))
            foreach (var file in Directory.EnumerateFiles(pickup.Path, "*.json")) System.IO.File.Delete(file);
        jobs.Clear();
        var now = clock.Reset();
        return Ok(new { reset = true, now, pickupDirectory = pickup.Path, emailProvider = email.Value.Provider });
    }

    [HttpPost("seed/{scenario}")]
    public async Task<IActionResult> Seed(string scenario, CancellationToken ct)
    {
        if (!SeedScenarios.Names.Contains(scenario, StringComparer.OrdinalIgnoreCase))
            return Problem(statusCode: 404, title: $"Unknown seed scenario '{scenario}'. Known: {string.Join(", ", SeedScenarios.Names)}.");
        try
        {
            return Ok(await seeds.RunAsync(scenario, ct));
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("SuperAdmin:"))
        {
            return Problem(statusCode: 400, title: ex.Message);
        }
    }

    /// <summary>Jobs enqueued since the last drain (Hangfire is off in E2E).</summary>
    [HttpGet("jobs")]
    public IActionResult PendingJobs() => Ok(jobs.Pending);

    /// <summary>Runs recorded jobs through the real job classes. <paramref name="type"/>: csv, discovery or classify (default: all).</summary>
    [HttpPost("jobs/run-pending")]
    public async Task<IActionResult> RunPending([FromQuery] string? type, CancellationToken ct)
    {
        RecordedJobType? only = null;
        if (!string.IsNullOrWhiteSpace(type))
        {
            if (!Enum.TryParse<RecordedJobType>(type, ignoreCase: true, out var parsed))
                return Problem(statusCode: 400, title: $"Unknown job type '{type}'. Known: csv, discovery, classify.");
            only = parsed;
        }
        var results = await runner.RunPendingAsync(only, ct);
        return Ok(new { ran = results.Count, failed = results.Count(r => !r.Succeeded), results });
    }

    /// <summary>One run of the campaign sender, synchronously (the minutely cron is never registered in E2E).</summary>
    [HttpPost("jobs/campaign-sender/run")]
    public async Task<IActionResult> RunSender([FromServices] CampaignSenderJob sender, CancellationToken ct)
    {
        await sender.ExecuteAsync(ct);
        return Ok(new { ran = true, now = clock.GetUtcNow() });
    }

    [HttpGet("clock")]
    public IActionResult Clock() => Ok(new { now = clock.GetUtcNow() });

    [HttpPost("clock/advance")]
    public IActionResult Advance([FromBody] AdvanceClockRequest request)
    {
        if (request.Minutes <= 0) return Problem(statusCode: 400, title: "'minutes' must be positive; use clock/set to go back.");
        return Ok(new { now = clock.Advance(TimeSpan.FromMinutes(request.Minutes)) });
    }

    [HttpPost("clock/set")]
    public IActionResult Set([FromBody] SetClockRequest request) => Ok(new { now = clock.Set(request.Utc.ToUniversalTime()) });
}

public sealed record AdvanceClockRequest(int Minutes);
public sealed record SetClockRequest(DateTimeOffset Utc);

/// <summary>Where <c>Email:Provider=Pickup</c> writes, resolved against the content root.</summary>
public sealed record PickupDirectory(string Path);

/// <summary>Rejects every caller that isn't on this machine (403). A missing remote address is treated as remote.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class LoopbackOnlyAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var remote = context.HttpContext.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote))
            context.Result = new ObjectResult(new ProblemDetails { Status = 403, Title = "Test-support endpoints only answer loopback callers." }) { StatusCode = 403 };
    }
}

/// <summary>Removes <see cref="TestSupportController"/> from MVC unless the environment is E2E, so its routes don't exist.</summary>
public sealed class TestSupportControllerGate(bool enabled) : IApplicationFeatureProvider<ControllerFeature>
{
    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        if (enabled) return;
        foreach (var controller in feature.Controllers.Where(c => c.AsType() == typeof(TestSupportController)).ToList())
            feature.Controllers.Remove(controller);
    }
}

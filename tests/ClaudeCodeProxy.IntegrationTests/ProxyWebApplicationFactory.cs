using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClaudeCodeProxy.IntegrationTests;

public sealed class ProxyWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _statsDirectory = Path.Combine(
        Path.GetTempPath(), "claude-code-proxy-stats-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_statsDirectory);
        builder.UseSetting("Stats:OutputPath", _statsDirectory);
        builder.UseSetting("Stats:DayBoundaryTimeZone", "America/New_York");
        builder.UseSetting("Stats:DayBoundaryTime", "00:00:00");
        builder.UseSetting("Stats:MonthlyTargetSpendUsd", "3100");

        // The updater downloads pricing and rewrites pricing.json; tests must not touch the network or that file.
        builder.ConfigureServices(services =>
        {
            var updater = services.SingleOrDefault(descriptor =>
                descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType == typeof(PricingUpdater));
            if (updater is not null) services.Remove(updater);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing || !Directory.Exists(_statsDirectory)) return;
        try
        {
            Directory.Delete(_statsDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A failed test should not be hidden by best-effort temporary cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // A failed test should not be hidden by best-effort temporary cleanup.
        }
    }
}

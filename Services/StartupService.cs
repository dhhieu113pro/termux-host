using System.Diagnostics;

namespace TermuxHost.Services;

public sealed class StartupService(
    ApplicationService applications,
    ILogger<StartupService> logger) : BackgroundService
{
    private readonly string _prefix = Environment.GetEnvironmentVariable("PREFIX")
        ?? "/data/data/com.termux/files/usr";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give runit a moment to finish bringing its supervise directories online.
        try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
        catch (OperationCanceledException) { return; }

        await RestoreApplicationsAsync(stoppingToken);
        await RestoreNgrokAsync(stoppingToken);
    }

    private async Task RestoreApplicationsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var apps = await applications.ListAsync(cancellationToken);
            foreach (var summary in apps)
            {
                try
                {
                    var app = await applications.GetAsync(summary.Id, cancellationToken);
                    if (app?.AutoStart != true) continue;

                    var status = await applications.StartAsync(summary.Id, cancellationToken);
                    logger.LogInformation("Restored app {AppId}: {Status}", summary.Id, status);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Unable to restore app {AppId}", summary.Id);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to restore TermuxHost applications");
        }
    }

    private async Task RestoreNgrokAsync(CancellationToken cancellationToken)
    {
        var runFile = Path.Combine(_prefix, "var", "service", "ngrok", "run");
        if (!File.Exists(runFile)) return;

        try
        {
            await RunAsync(Path.Combine(_prefix, "bin", "sv-enable"), ["ngrok"], cancellationToken);
            var status = await RunAsync(Path.Combine(_prefix, "bin", "sv"), ["up", "ngrok"], cancellationToken);
            logger.LogInformation("Restored ngrok: {Status}", status.Trim());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to restore ngrok");
        }
    }

    private static async Task<string> RunAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        if (!File.Exists(fileName)) return "command unavailable";

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (await stdout) + (await stderr);
    }
}

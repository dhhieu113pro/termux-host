using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace TermuxHost.Services;

public sealed class AppMonitoringService
{
    private readonly ApplicationService _applications;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly string _home = Environment.GetEnvironmentVariable("HOME") ?? Directory.GetCurrentDirectory();
    private readonly string _prefix = Environment.GetEnvironmentVariable("PREFIX") ?? "/data/data/com.termux/files/usr";

    public AppMonitoringService(ApplicationService applications) => _applications = applications;

    public async Task<AppRuntimeInfo> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var app = await _applications.GetAsync(id, cancellationToken)
            ?? throw new InvalidOperationException("Application not found.");

        var healthUrl = $"http://127.0.0.1:{app.Port}/health";
        var healthy = false;
        string health = "Unavailable";
        try
        {
            using var response = await _http.GetAsync(healthUrl, cancellationToken);
            healthy = response.IsSuccessStatusCode;
            health = $"HTTP {(int)response.StatusCode}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            health = "No response";
        }

        var (tunnelPort, publicUrl) = await GetNgrokAsync(cancellationToken);
        if (tunnelPort != app.Port) publicUrl = null;

        return new AppRuntimeInfo(app.Id, app.Port, healthy, health, healthUrl, publicUrl);
    }

    public async Task<string> GetLogsAsync(string id, int lines, CancellationToken cancellationToken = default)
    {
        _ = await _applications.GetAsync(id, cancellationToken)
            ?? throw new InvalidOperationException("Application not found.");

        lines = Math.Clamp(lines, 1, 1000);
        var logDir = Path.Combine(_home, "hosting", "apps", id, "logs");
        if (!Directory.Exists(logDir)) return "No logs yet.";

        var files = Directory.GetFiles(logDir)
            .Where(x => !x.EndsWith(".s", StringComparison.Ordinal))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0) return "No logs yet.";

        var all = await File.ReadAllLinesAsync(files[^1], cancellationToken);
        return string.Join(Environment.NewLine, all.TakeLast(lines));
    }

    private async Task<(int? Port, string? PublicUrl)> GetNgrokAsync(CancellationToken cancellationToken)
    {
        int? port = null;
        try
        {
            var runPath = Path.Combine(_prefix, "var", "service", "ngrok", "run");
            if (File.Exists(runPath))
            {
                var script = await File.ReadAllTextAsync(runPath, cancellationToken);
                var match = Regex.Match(script, @"ngrok\s+http\s+(\d+)", RegexOptions.IgnoreCase);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var parsed)) port = parsed;
            }
        }
        catch { }

        if (port is null) return (null, null);
        try
        {
            var tunnels = await _http.GetFromJsonAsync<NgrokApiResponse>("http://127.0.0.1:4040/api/tunnels", cancellationToken);
            var url = tunnels?.Tunnels?.FirstOrDefault(x => x.PublicUrl?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true)?.PublicUrl
                ?? tunnels?.Tunnels?.FirstOrDefault()?.PublicUrl;
            return (port, url);
        }
        catch { return (port, null); }
    }

    private sealed class NgrokApiResponse { public List<NgrokTunnel>? Tunnels { get; set; } }
    private sealed class NgrokTunnel { public string? PublicUrl { get; set; } }
}

public sealed record AppRuntimeInfo(string Id, int Port, bool Healthy, string Health, string HealthUrl, string? PublicUrl);

using System.Diagnostics;
using System.Net.Http.Json;

namespace TermuxHost.Services;

public sealed class NgrokService
{
    private const string NgrokPath = "/data/data/com.termux/files/usr/bin/ngrok";
    private const string SvPath = "/data/data/com.termux/files/usr/bin/sv";
    private const string SvEnablePath = "/data/data/com.termux/files/usr/bin/sv-enable";
    private const string ServiceName = "ngrok";
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(2) };

    private static string Home => Environment.GetEnvironmentVariable("HOME") ?? "/data/data/com.termux/files/home";
    private static string Prefix => Environment.GetEnvironmentVariable("PREFIX") ?? "/data/data/com.termux/files/usr";
    private static string ServiceDir => Path.Combine(Prefix, "var", "service", ServiceName);
    private static string LogDir => Path.Combine(Home, "termux-host", "logs", "ngrok");

    public async Task<NgrokResult> SetTokenAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
            return new(false, "Token is required.");

        if (!File.Exists(NgrokPath))
            return new(false, "ngrok is not installed.");

        var result = await RunAsync(NgrokPath, ["config", "add-authtoken", token.Trim()], cancellationToken);
        return new(result.ExitCode == 0, result.ExitCode == 0 ? "Token saved." : result.Error.Trim());
    }

    public async Task<NgrokResult> StartAsync(int port, CancellationToken cancellationToken)
    {
        if (port is < 1 or > 65535)
            return new(false, "Port must be between 1 and 65535.");

        if (!File.Exists(NgrokPath))
            return new(false, "ngrok is not installed.");

        Directory.CreateDirectory(Path.Combine(ServiceDir, "log"));
        Directory.CreateDirectory(LogDir);

        var runFile = Path.Combine(ServiceDir, "run");
        var logRunFile = Path.Combine(ServiceDir, "log", "run");

        await File.WriteAllTextAsync(runFile, $"""#!/data/data/com.termux/files/usr/bin/sh
export HOME="{Home}"
export PREFIX="{Prefix}"
export PATH="{Prefix}/bin:/system/bin:/system/xbin"
exec ngrok http {port} --log=stdout 2>&1
""", cancellationToken);

        await File.WriteAllTextAsync(logRunFile, $"""#!/data/data/com.termux/files/usr/bin/sh
mkdir -p "{LogDir}"
exec svlogd -tt "{LogDir}"
""", cancellationToken);

        await RunAsync("/data/data/com.termux/files/usr/bin/chmod", ["+x", runFile, logRunFile], cancellationToken);
        await RunAsync(SvEnablePath, [ServiceName], cancellationToken, allowMissing: true);
        var up = await RunAsync(SvPath, ["up", ServiceName], cancellationToken, allowMissing: true);

        return new(up.ExitCode == 0, up.ExitCode == 0 ? $"ngrok started for port {port}." : up.Error.Trim());
    }

    public async Task<NgrokResult> StopAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(SvPath, ["down", ServiceName], cancellationToken, allowMissing: true);
        return new(result.ExitCode == 0, result.ExitCode == 0 ? "ngrok stopped." : result.Error.Trim());
    }

    public async Task<NgrokStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(SvPath, ["status", ServiceName], cancellationToken, allowMissing: true);
        var text = string.Join(" ", new[] { result.Output.Trim(), result.Error.Trim() }.Where(x => x.Length > 0));
        var running = result.ExitCode == 0 && text.StartsWith("run:", StringComparison.OrdinalIgnoreCase);
        var publicUrl = running ? await TryGetPublicUrlAsync(cancellationToken) : null;
        return new(running, text.Length == 0 ? "Not configured" : text, publicUrl);
    }

    public async Task<string> GetLogsAsync(int lines, CancellationToken cancellationToken)
    {
        lines = Math.Clamp(lines, 1, 500);
        if (!Directory.Exists(LogDir)) return "No ngrok logs yet.";

        var files = Directory.GetFiles(LogDir)
            .Where(x => !x.EndsWith(".s", StringComparison.Ordinal))
            .OrderBy(x => x)
            .ToArray();

        if (files.Length == 0) return "No ngrok logs yet.";

        var file = files[^1];
        var all = await File.ReadAllLinesAsync(file, cancellationToken);
        return string.Join(Environment.NewLine, all.TakeLast(lines));
    }

    private async Task<string?> TryGetPublicUrlAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.GetFromJsonAsync<NgrokApiResponse>("http://127.0.0.1:4040/api/tunnels", cancellationToken);
            return response?.Tunnels?.FirstOrDefault(x => x.PublicUrl?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true)?.PublicUrl
                ?? response?.Tunnels?.FirstOrDefault()?.PublicUrl;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> args, CancellationToken cancellationToken, bool allowMissing = false)
    {
        if (!File.Exists(fileName))
            return new(allowMissing ? 127 : 1, "", $"Command not found: {fileName}");

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
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new(process.ExitCode, await outputTask, await errorTask);
    }

    private sealed class NgrokApiResponse
    {
        public List<NgrokTunnel>? Tunnels { get; set; }
    }

    private sealed class NgrokTunnel
    {
        public string? PublicUrl { get; set; }
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}

public sealed record NgrokResult(bool Success, string Message);
public sealed record NgrokStatus(bool Running, string Status, string? PublicUrl);

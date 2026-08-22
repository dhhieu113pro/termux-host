using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TermuxHost.Services;

public sealed class ApplicationService
{
    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex EnvKeyPattern = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    private readonly string _home = Environment.GetEnvironmentVariable("HOME") ?? Directory.GetCurrentDirectory();
    private readonly string _prefix = Environment.GetEnvironmentVariable("PREFIX") ?? "/data/data/com.termux/files/usr";
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string AppsRoot => Path.Combine(_home, "hosting", "apps");

    public async Task<IReadOnlyList<ApplicationSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(AppsRoot);
        var result = new List<ApplicationSummary>();

        foreach (var file in Directory.EnumerateFiles(AppsRoot, "app.json", SearchOption.AllDirectories))
        {
            try
            {
                var app = await ReadAsync(file, cancellationToken);
                if (app is not null)
                {
                    result.Add(new ApplicationSummary(app.Id, app.Name, app.Port, app.WorkingDirectory, app.Dll, await GetStatusAsync(app.Id, cancellationToken)));
                }
            }
            catch
            {
                // Ignore malformed app definitions so one bad file does not break the whole dashboard.
            }
        }

        return result.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<ApplicationView?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        var app = await ReadAsync(ConfigPath(id), cancellationToken);
        if (app is null) return null;

        return new ApplicationView(
            app.Id,
            app.Name,
            app.Port,
            app.WorkingDirectory,
            app.Dll,
            app.AutoStart,
            app.Environment,
            app.Secrets.Keys.OrderBy(x => x).ToArray(),
            await GetStatusAsync(id, cancellationToken));
    }

    public async Task<ApplicationView> SaveAsync(ApplicationSaveRequest request, bool restart, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var configPath = ConfigPath(request.Id);
            var existing = await ReadAsync(configPath, cancellationToken);
            var secrets = existing?.Secrets is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(existing.Secrets, StringComparer.Ordinal);

            foreach (var item in request.Secrets ?? [])
            {
                if (!EnvKeyPattern.IsMatch(item.Key))
                    throw new InvalidOperationException($"Invalid secret key: {item.Key}");

                if (item.Remove)
                    secrets.Remove(item.Key);
                else if (!string.IsNullOrEmpty(item.Value))
                    secrets[item.Key] = item.Value;
            }

            var environment = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in request.Environment ?? [])
            {
                if (!EnvKeyPattern.IsMatch(item.Key))
                    throw new InvalidOperationException($"Invalid environment key: {item.Key}");
                environment[item.Key] = item.Value ?? string.Empty;
            }

            environment["ASPNETCORE_URLS"] = $"http://0.0.0.0:{request.Port}";

            var app = new ApplicationDefinition
            {
                Id = request.Id,
                Name = request.Name.Trim(),
                Port = request.Port,
                WorkingDirectory = request.WorkingDirectory.Trim(),
                Dll = request.Dll.Trim(),
                AutoStart = request.AutoStart,
                Environment = environment,
                Secrets = secrets
            };

            Directory.CreateDirectory(AppDirectory(app.Id));
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(app, _jsonOptions), cancellationToken);
            await WriteServiceAsync(app, cancellationToken);

            if (app.AutoStart)
                await RunCommandAsync("sv-enable", [ServiceName(app.Id)], cancellationToken, ignoreFailure: true);
            else
                await RunCommandAsync("sv-disable", [ServiceName(app.Id)], cancellationToken, ignoreFailure: true);

            if (restart)
                await RunCommandAsync("sv", ["restart", ServiceName(app.Id)], cancellationToken, ignoreFailure: true);

            return (await GetAsync(app.Id, cancellationToken))!;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> RestartAsync(string id, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        await RunCommandAsync("sv", ["restart", ServiceName(id)], cancellationToken, ignoreFailure: true);
        return await GetStatusAsync(id, cancellationToken);
    }

    public async Task<string> StartAsync(string id, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        await RunCommandAsync("sv", ["up", ServiceName(id)], cancellationToken, ignoreFailure: true);
        return await GetStatusAsync(id, cancellationToken);
    }

    public async Task<string> StopAsync(string id, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        await RunCommandAsync("sv", ["down", ServiceName(id)], cancellationToken, ignoreFailure: true);
        return await GetStatusAsync(id, cancellationToken);
    }

    private async Task WriteServiceAsync(ApplicationDefinition app, CancellationToken cancellationToken)
    {
        var serviceDir = Path.Combine(_prefix, "var", "service", ServiceName(app.Id));
        var logDir = Path.Combine(serviceDir, "log");
        Directory.CreateDirectory(logDir);
        Directory.CreateDirectory(Path.Combine(AppDirectory(app.Id), "logs"));

        var lines = new List<string>
        {
            "#!/data/data/com.termux/files/usr/bin/sh",
            $"export HOME={ShQuote(_home)}",
            $"export PREFIX={ShQuote(_prefix)}",
            $"export PATH={ShQuote($"{_prefix}/bin:/system/bin:/system/xbin")}" 
        };

        foreach (var pair in app.Environment.OrderBy(x => x.Key, StringComparer.Ordinal))
            lines.Add($"export {pair.Key}={ShQuote(pair.Value)}");
        foreach (var pair in app.Secrets.OrderBy(x => x.Key, StringComparer.Ordinal))
            lines.Add($"export {pair.Key}={ShQuote(pair.Value)}");

        lines.Add($"cd {ShQuote(app.WorkingDirectory)}");
        lines.Add($"exec dotnet {ShQuote(app.Dll)} 2>&1");

        var runPath = Path.Combine(serviceDir, "run");
        await File.WriteAllTextAsync(runPath, string.Join('\n', lines) + "\n", cancellationToken);

        var logRunPath = Path.Combine(logDir, "run");
        var logPath = Path.Combine(AppDirectory(app.Id), "logs");
        await File.WriteAllTextAsync(logRunPath,
            $"#!/data/data/com.termux/files/usr/bin/sh\nexec svlogd -tt {ShQuote(logPath)}\n", cancellationToken);

        await RunCommandAsync("chmod", ["+x", runPath, logRunPath], cancellationToken, ignoreFailure: true);
    }

    private async Task<string> GetStatusAsync(string id, CancellationToken cancellationToken)
    {
        var result = await RunCommandAsync("sv", ["status", ServiceName(id)], cancellationToken, ignoreFailure: true);
        return string.IsNullOrWhiteSpace(result) ? "unknown" : result.Trim();
    }

    private async Task<ApplicationDefinition?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ApplicationDefinition>(stream, _jsonOptions, cancellationToken);
    }

    private async Task<string> RunCommandAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken, bool ignoreFailure)
    {
        try
        {
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
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = (await stdout) + (await stderr);
            if (!ignoreFailure && process.ExitCode != 0) throw new InvalidOperationException(output.Trim());
            return output;
        }
        catch when (ignoreFailure)
        {
            return string.Empty;
        }
    }

    private string AppDirectory(string id) => Path.Combine(AppsRoot, id);
    private string ConfigPath(string id) => Path.Combine(AppDirectory(id), "app.json");
    private static string ServiceName(string id) => $"termux-host-app-{id}";

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !IdPattern.IsMatch(id))
            throw new InvalidOperationException("App id must contain lowercase letters, numbers, and hyphens only.");
    }

    private static void ValidateRequest(ApplicationSaveRequest request)
    {
        ValidateId(request.Id);
        if (string.IsNullOrWhiteSpace(request.Name)) throw new InvalidOperationException("Name is required.");
        if (request.Port is < 1 or > 65535) throw new InvalidOperationException("Port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(request.WorkingDirectory)) throw new InvalidOperationException("Working directory is required.");
        if (string.IsNullOrWhiteSpace(request.Dll) || request.Dll.Contains('/') || request.Dll.Contains('\\'))
            throw new InvalidOperationException("Startup DLL must be a file name such as MyApp.dll.");
    }

    private static string ShQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private sealed class ApplicationDefinition
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int Port { get; set; }
        public string WorkingDirectory { get; set; } = string.Empty;
        public string Dll { get; set; } = string.Empty;
        public bool AutoStart { get; set; } = true;
        public Dictionary<string, string> Environment { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Secrets { get; set; } = new(StringComparer.Ordinal);
    }
}

public sealed record ApplicationSummary(string Id, string Name, int Port, string WorkingDirectory, string Dll, string Status);
public sealed record ApplicationView(string Id, string Name, int Port, string WorkingDirectory, string Dll, bool AutoStart, IReadOnlyDictionary<string, string> Environment, IReadOnlyList<string> SecretKeys, string Status);
public sealed record ApplicationSaveRequest(string Id, string Name, int Port, string WorkingDirectory, string Dll, bool AutoStart, IReadOnlyList<ApplicationSettingItem>? Environment, IReadOnlyList<ApplicationSecretItem>? Secrets);
public sealed record ApplicationSettingItem(string Key, string? Value);
public sealed record ApplicationSecretItem(string Key, string? Value, bool Remove = false);

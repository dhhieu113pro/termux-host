using System.Text.Json;
using System.Text.RegularExpressions;

namespace TermuxHost.Services;

public sealed class PublicRouteService
{
    private static readonly Regex RoutePattern = new("^/[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.Compiled);
    private readonly ApplicationService _applications;
    private readonly string _home = Environment.GetEnvironmentVariable("HOME") ?? Directory.GetCurrentDirectory();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private string ConfigPath => Path.Combine(_home, "termux-host", "public-routes.json");

    public PublicRouteService(ApplicationService applications) => _applications = applications;

    public async Task<IReadOnlyList<PublicRouteView>> ListAsync(CancellationToken ct = default)
    {
        var routes = await ReadAsync(ct);
        var apps = await _applications.ListAsync(ct);
        var byId = apps.ToDictionary(x => x.Id, StringComparer.Ordinal);
        return routes
            .Select(x => new PublicRouteView(x.AppId, byId.TryGetValue(x.AppId, out var app) ? app.Name : x.AppId,
                x.Path, byId.TryGetValue(x.AppId, out app) ? app.Port : 0, x.Enabled))
            .OrderBy(x => x.Path, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<PublicRouteView> SaveAsync(PublicRouteSaveRequest request, CancellationToken ct = default)
    {
        var path = Normalize(request.Path);
        var app = await _applications.GetAsync(request.AppId, ct) ?? throw new InvalidOperationException("Application not found.");
        await _gate.WaitAsync(ct);
        try
        {
            var routes = await ReadAsync(ct);
            if (routes.Any(x => !string.Equals(x.AppId, request.AppId, StringComparison.Ordinal) && string.Equals(x.Path, path, StringComparison.Ordinal)))
                throw new InvalidOperationException($"Route {path} is already used by another application.");
            routes.RemoveAll(x => string.Equals(x.AppId, request.AppId, StringComparison.Ordinal));
            routes.Add(new PublicRouteDefinition { AppId = request.AppId, Path = path, Enabled = request.Enabled });
            await WriteAsync(routes, ct);
            return new PublicRouteView(app.Id, app.Name, path, app.Port, request.Enabled);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(string appId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var routes = await ReadAsync(ct);
            routes.RemoveAll(x => string.Equals(x.AppId, appId, StringComparison.Ordinal));
            await WriteAsync(routes, ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<ResolvedPublicRoute?> ResolveAsync(PathString requestPath, CancellationToken ct = default)
    {
        var value = requestPath.Value ?? "/";
        var routes = await ReadAsync(ct);
        foreach (var route in routes.Where(x => x.Enabled).OrderByDescending(x => x.Path.Length))
        {
            if (!value.Equals(route.Path, StringComparison.OrdinalIgnoreCase) && !value.StartsWith(route.Path + "/", StringComparison.OrdinalIgnoreCase)) continue;
            var app = await _applications.GetAsync(route.AppId, ct);
            if (app is null) return null;
            var remaining = value.Length == route.Path.Length ? "/" : value[route.Path.Length..];
            return new ResolvedPublicRoute(route.AppId, route.Path, app.Port, remaining);
        }
        return null;
    }

    private async Task<List<PublicRouteDefinition>> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(ConfigPath)) return [];
        try
        {
            await using var stream = File.OpenRead(ConfigPath);
            return await JsonSerializer.DeserializeAsync<List<PublicRouteDefinition>>(stream, _json, ct) ?? [];
        }
        catch { return []; }
    }

    private async Task WriteAsync(List<PublicRouteDefinition> routes, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        await File.WriteAllTextAsync(ConfigPath, JsonSerializer.Serialize(routes, _json), ct);
    }

    private static string Normalize(string path)
    {
        path = (path ?? string.Empty).Trim().ToLowerInvariant();
        if (!path.StartsWith('/')) path = "/" + path;
        path = path.TrimEnd('/');
        if (!RoutePattern.IsMatch(path)) throw new InvalidOperationException("Route must look like /app1 or /local-coding.");
        if (path is "/api" or "/market" or "/applications" or "/terminal" or "/ngrok" or "/routes")
            throw new InvalidOperationException("That route is reserved by TermuxHost.");
        return path;
    }

    private sealed class PublicRouteDefinition { public string AppId { get; set; } = ""; public string Path { get; set; } = ""; public bool Enabled { get; set; } = true; }
}

public sealed record PublicRouteView(string AppId, string AppName, string Path, int Port, bool Enabled);
public sealed record PublicRouteSaveRequest(string AppId, string Path, bool Enabled = true);
public sealed record ResolvedPublicRoute(string AppId, string Prefix, int Port, string ForwardPath);

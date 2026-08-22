using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace TermuxHost.Services;

public sealed class MarketService
{
    private const string CatalogUrl = "https://raw.githubusercontent.com/dhhieu113pro/termux-host/main/market/catalog.json";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly ApplicationService _applications;
    private readonly string _home = Environment.GetEnvironmentVariable("HOME") ?? Directory.GetCurrentDirectory();
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public MarketService(ApplicationService applications)
    {
        _applications = applications;
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TermuxHost", "1.0"));
    }

    public async Task<IReadOnlyList<MarketAppView>> ListAsync(CancellationToken cancellationToken = default)
    {
        var catalog = await GetJsonAsync<MarketCatalog>(CatalogUrl, cancellationToken) ?? new();
        var installed = (await _applications.ListAsync(cancellationToken)).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var result = new List<MarketAppView>();

        foreach (var item in catalog.Apps)
        {
            try
            {
                var release = await GetLatestReleaseAsync(item.Repository, cancellationToken);
                result.Add(new MarketAppView(item.Id, item.Name, item.Description, item.Category, item.Repository,
                    item.Featured, item.Verified, installed.Contains(item.Id), release?.TagName));
            }
            catch
            {
                result.Add(new MarketAppView(item.Id, item.Name, item.Description, item.Category, item.Repository,
                    item.Featured, item.Verified, installed.Contains(item.Id), null));
            }
        }
        return result;
    }

    public async Task<MarketManifest> GetManifestAsync(string id, CancellationToken cancellationToken = default)
    {
        var entry = await FindAsync(id, cancellationToken);
        return await GetJsonAsync<MarketManifest>(entry.Manifest, cancellationToken)
            ?? throw new InvalidOperationException("Market manifest is unavailable.");
    }

    public async Task<MarketInstallResult> InstallAsync(string id, IReadOnlyDictionary<string, string>? settings, CancellationToken cancellationToken = default)
    {
        var entry = await FindAsync(id, cancellationToken);
        var manifest = await GetManifestAsync(id, cancellationToken);
        if (!string.Equals(manifest.Runtime, "dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only .NET market applications are supported right now.");

        var release = await GetLatestReleaseAsync(entry.Repository, cancellationToken)
            ?? throw new InvalidOperationException("This application has no GitHub Release yet.");
        var package = release.Assets.FirstOrDefault(x => x.Name == manifest.PackageAsset)
            ?? throw new InvalidOperationException($"Release {release.TagName} does not contain {manifest.PackageAsset}.");
        var checksum = release.Assets.FirstOrDefault(x => x.Name == manifest.ChecksumAsset)
            ?? throw new InvalidOperationException($"Release {release.TagName} does not contain {manifest.ChecksumAsset}.");

        var appRoot = Path.Combine(_home, "hosting", "apps", manifest.Id);
        var releasesRoot = Path.Combine(appRoot, "releases");
        var releaseDir = Path.Combine(releasesRoot, SafeVersion(release.TagName));
        Directory.CreateDirectory(releasesRoot);
        Directory.CreateDirectory(Path.Combine(_home, "workspaces"));

        var temp = Path.Combine(Path.GetTempPath(), $"termuxhost-{manifest.Id}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            var zip = Path.Combine(temp, package.Name);
            await DownloadAsync(package.BrowserDownloadUrl, zip, cancellationToken);
            var expected = (await _http.GetStringAsync(checksum.BrowserDownloadUrl, cancellationToken)).Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(zip), cancellationToken)).ToLowerInvariant();
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Package SHA-256 verification failed.");

            if (Directory.Exists(releaseDir)) Directory.Delete(releaseDir, true);
            Directory.CreateDirectory(releaseDir);
            ZipFile.ExtractToDirectory(zip, releaseDir, overwriteFiles: true);
            if (!File.Exists(Path.Combine(releaseDir, manifest.Entrypoint)))
                throw new InvalidOperationException($"Package does not contain {manifest.Entrypoint}.");

            var environment = new List<ApplicationSettingItem>();
            foreach (var option in manifest.Configuration.Where(x => !x.Secret))
            {
                var value = settings is not null && settings.TryGetValue(option.Key, out var supplied) ? supplied : option.Default;
                value = ExpandHome(value ?? string.Empty);
                if (option.Required && string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"{option.Label} is required.");
                environment.Add(new ApplicationSettingItem(option.Key, value));
            }

            var secrets = new List<ApplicationSecretItem>();
            foreach (var option in manifest.Configuration.Where(x => x.Secret))
            {
                var value = settings is not null && settings.TryGetValue(option.Key, out var supplied) ? supplied : option.Default;
                if (option.Required && string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"{option.Label} is required.");
                if (!string.IsNullOrEmpty(value)) secrets.Add(new ApplicationSecretItem(option.Key, value));
            }

            await _applications.SaveAsync(new ApplicationSaveRequest(manifest.Id, manifest.Name, manifest.Port,
                releaseDir, manifest.Entrypoint, manifest.AutoStart, environment, secrets), restart: true, cancellationToken);
            await _applications.StartAsync(manifest.Id, cancellationToken);

            return new MarketInstallResult(manifest.Id, manifest.Name, release.TagName, manifest.Port,
                manifest.HealthCheck, $"http://127.0.0.1:{manifest.Port}{manifest.HealthCheck}");
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    private async Task<MarketCatalogEntry> FindAsync(string id, CancellationToken cancellationToken)
    {
        var catalog = await GetJsonAsync<MarketCatalog>(CatalogUrl, cancellationToken) ?? new();
        return catalog.Apps.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Application is not in the TermuxHost market.");
    }

    private async Task<GitHubRelease?> GetLatestReleaseAsync(string repository, CancellationToken cancellationToken) =>
        await GetJsonAsync<GitHubRelease>($"https://api.github.com/repos/{repository}/releases/latest", cancellationToken);

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode) return default;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, _json, cancellationToken);
    }

    private async Task DownloadAsync(string url, string path, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(path);
        await source.CopyToAsync(target, cancellationToken);
    }

    private string ExpandHome(string value) => value == "~" ? _home : value.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(_home, value[2..]) : value;
    private static string SafeVersion(string value) => string.Concat(value.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_'));

    private sealed class MarketCatalog { public int SchemaVersion { get; set; } public List<MarketCatalogEntry> Apps { get; set; } = []; }
    private sealed class MarketCatalogEntry { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public string Description { get; set; } = ""; public string Category { get; set; } = ""; public string Repository { get; set; } = ""; public string Manifest { get; set; } = ""; public bool Featured { get; set; } public bool Verified { get; set; } }
    private sealed class GitHubRelease { public string TagName { get; set; } = ""; public List<GitHubAsset> Assets { get; set; } = []; }
    private sealed class GitHubAsset { public string Name { get; set; } = ""; public string BrowserDownloadUrl { get; set; } = ""; }
}

public sealed record MarketAppView(string Id, string Name, string Description, string Category, string Repository, bool Featured, bool Verified, bool Installed, string? LatestVersion);
public sealed record MarketInstallResult(string Id, string Name, string Version, int Port, string HealthCheck, string HealthUrl);
public sealed class MarketManifest
{
    public int SchemaVersion { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public string Runtime { get; set; } = "";
    public string RuntimeVersion { get; set; } = "";
    public string Entrypoint { get; set; } = "";
    public int Port { get; set; }
    public string HealthCheck { get; set; } = "/";
    public string PackageAsset { get; set; } = "";
    public string ChecksumAsset { get; set; } = "";
    public bool AutoStart { get; set; } = true;
    public List<MarketConfiguration> Configuration { get; set; } = [];
}
public sealed class MarketConfiguration { public string Key { get; set; } = ""; public string Label { get; set; } = ""; public string Description { get; set; } = ""; public string? Default { get; set; } public bool Required { get; set; } public bool Secret { get; set; } }
public sealed record MarketInstallRequest(Dictionary<string, string>? Settings);

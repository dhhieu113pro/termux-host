using System.Text.Json;
using TermuxHost.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddSingleton<ShellService>();
builder.Services.AddSingleton<NgrokService>();
builder.Services.AddSingleton<ApplicationService>();
builder.Services.AddSingleton<MarketService>();

var app = builder.Build();

app.UseStaticFiles();
app.MapRazorPages();

app.MapPost("/api/shell", async (ShellRequest request, ShellService shell, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Command)) return Results.BadRequest(new { error = "Command is required." });
    return Results.Ok(await shell.ExecuteAsync(request.Command, cancellationToken));
});

app.MapGet("/api/shell/stream", async (HttpContext context, string command, ShellService shell) =>
{
    if (string.IsNullOrWhiteSpace(command)) { context.Response.StatusCode = 400; await context.Response.WriteAsync("Command is required."); return; }
    context.Response.StatusCode = 200;
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache, no-transform";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.Headers["X-Accel-Buffering"] = "no";
    await context.Response.StartAsync(context.RequestAborted);
    try
    {
        await foreach (var item in shell.StreamAsync(command, context.RequestAborted))
        {
            var json = JsonSerializer.Serialize(new { data = item.Data });
            await context.Response.WriteAsync($"event: {item.Type}\ndata: {json}\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
});

app.MapGet("/api/system", async (ShellService shell, CancellationToken cancellationToken) =>
{
    var hostname = await shell.ExecuteAsync("hostname", cancellationToken);
    var uptime = await shell.ExecuteAsync("uptime -p 2>/dev/null || uptime", cancellationToken);
    var dotnet = await shell.ExecuteAsync("dotnet --version", cancellationToken);
    var git = await shell.ExecuteAsync("git --version", cancellationToken);
    var ip = await shell.ExecuteAsync("ip route get 1.1.1.1 2>/dev/null | awk '{for(i=1;i<=NF;i++) if($i==\"src\"){print $(i+1); exit}}' || true", cancellationToken);
    if (string.IsNullOrWhiteSpace(ip.StdOut)) ip = await shell.ExecuteAsync("ip -4 addr 2>/dev/null | awk '/inet / && $2 !~ /^127\\./ {split($2,a,\"/\"); print a[1]; exit}'", cancellationToken);
    return Results.Ok(new { hostname = hostname.StdOut.Trim(), uptime = uptime.StdOut.Trim(), dotnet = dotnet.StdOut.Trim(), git = git.StdOut.Trim(), ip = ip.StdOut.Trim() });
});

app.MapGet("/api/ngrok/status", async (NgrokService ngrok, CancellationToken ct) => Results.Ok(await ngrok.GetStatusAsync(ct)));
app.MapPost("/api/ngrok/token", async (NgrokTokenRequest request, NgrokService ngrok, CancellationToken ct) => { var r = await ngrok.SetTokenAsync(request.Token, ct); return r.Success ? Results.Ok(r) : Results.BadRequest(r); });
app.MapPost("/api/ngrok/start", async (NgrokStartRequest request, NgrokService ngrok, CancellationToken ct) => { var r = await ngrok.StartAsync(request.Port, ct); return r.Success ? Results.Ok(r) : Results.BadRequest(r); });
app.MapPost("/api/ngrok/stop", async (NgrokService ngrok, CancellationToken ct) => { var r = await ngrok.StopAsync(ct); return r.Success ? Results.Ok(r) : Results.BadRequest(r); });
app.MapGet("/api/ngrok/logs", async (int? lines, NgrokService ngrok, CancellationToken ct) => Results.Text(await ngrok.GetLogsAsync(lines ?? 100, ct), "text/plain"));

app.MapGet("/api/apps", async (ApplicationService applications, CancellationToken ct) => Results.Ok(await applications.ListAsync(ct)));
app.MapGet("/api/apps/{id}", async (string id, ApplicationService applications, CancellationToken ct) => { try { var r = await applications.GetAsync(id, ct); return r is null ? Results.NotFound() : Results.Ok(r); } catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); } });
app.MapPut("/api/apps/{id}", async (string id, bool? restart, ApplicationSaveRequest request, ApplicationService applications, CancellationToken ct) => { if (id != request.Id) return Results.BadRequest(new { error = "Route id must match application id." }); try { return Results.Ok(await applications.SaveAsync(request, restart ?? false, ct)); } catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); } });
app.MapPost("/api/apps/{id}/start", async (string id, ApplicationService applications, CancellationToken ct) => { try { return Results.Ok(new { status = await applications.StartAsync(id, ct) }); } catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); } });
app.MapPost("/api/apps/{id}/stop", async (string id, ApplicationService applications, CancellationToken ct) => { try { return Results.Ok(new { status = await applications.StopAsync(id, ct) }); } catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); } });
app.MapPost("/api/apps/{id}/restart", async (string id, ApplicationService applications, CancellationToken ct) => { try { return Results.Ok(new { status = await applications.RestartAsync(id, ct) }); } catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); } });

app.MapGet("/api/market", async (MarketService market, CancellationToken ct) => Results.Ok(await market.ListAsync(ct)));
app.MapGet("/api/market/{id}/manifest", async (string id, MarketService market, CancellationToken ct) => { try { return Results.Ok(await market.GetManifestAsync(id, ct)); } catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); } });
app.MapPost("/api/market/{id}/install", async (string id, MarketInstallRequest request, MarketService market, CancellationToken ct) => { try { return Results.Ok(await market.InstallAsync(id, request.Settings, ct)); } catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException) { return Results.BadRequest(new { error = ex.Message }); } });

app.Run();

public sealed record ShellRequest(string Command);
public sealed record NgrokTokenRequest(string Token);
public sealed record NgrokStartRequest(int Port);

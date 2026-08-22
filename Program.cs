using System.Text.Json;
using TermuxHost.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddSingleton<ShellService>();
builder.Services.AddSingleton<NgrokService>();
builder.Services.AddSingleton<ApplicationService>();

var app = builder.Build();

app.UseStaticFiles();
app.MapRazorPages();

app.MapPost("/api/shell", async (ShellRequest request, ShellService shell, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Command))
    {
        return Results.BadRequest(new { error = "Command is required." });
    }

    var result = await shell.ExecuteAsync(request.Command, cancellationToken);
    return Results.Ok(result);
});

app.MapGet("/api/shell/stream", async (HttpContext context, string command, ShellService shell) =>
{
    if (string.IsNullOrWhiteSpace(command))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("Command is required.");
        return;
    }

    context.Response.StatusCode = StatusCodes.Status200OK;
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
            await context.Response.WriteAsync($"event: {item.Type}\n", context.RequestAborted);
            await context.Response.WriteAsync($"data: {json}\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        // Browser disconnected or command was cancelled by the client.
    }
});

app.MapGet("/api/system", async (ShellService shell, CancellationToken cancellationToken) =>
{
    var hostname = await shell.ExecuteAsync("hostname", cancellationToken);
    var uptime = await shell.ExecuteAsync("uptime -p 2>/dev/null || uptime", cancellationToken);
    var dotnet = await shell.ExecuteAsync("dotnet --version", cancellationToken);
    var git = await shell.ExecuteAsync("git --version", cancellationToken);
    var ip = await shell.ExecuteAsync("ip -4 addr show wlan0 2>/dev/null | awk '/inet / {print $2}' | cut -d/ -f1 | head -n1", cancellationToken);

    return Results.Ok(new
    {
        hostname = hostname.StdOut.Trim(),
        uptime = uptime.StdOut.Trim(),
        dotnet = dotnet.StdOut.Trim(),
        git = git.StdOut.Trim(),
        ip = ip.StdOut.Trim()
    });
});

app.MapGet("/api/ngrok/status", async (NgrokService ngrok, CancellationToken cancellationToken) =>
    Results.Ok(await ngrok.GetStatusAsync(cancellationToken)));

app.MapPost("/api/ngrok/token", async (NgrokTokenRequest request, NgrokService ngrok, CancellationToken cancellationToken) =>
{
    var result = await ngrok.SetTokenAsync(request.Token, cancellationToken);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapPost("/api/ngrok/start", async (NgrokStartRequest request, NgrokService ngrok, CancellationToken cancellationToken) =>
{
    var result = await ngrok.StartAsync(request.Port, cancellationToken);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapPost("/api/ngrok/stop", async (NgrokService ngrok, CancellationToken cancellationToken) =>
{
    var result = await ngrok.StopAsync(cancellationToken);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapGet("/api/ngrok/logs", async (int? lines, NgrokService ngrok, CancellationToken cancellationToken) =>
    Results.Text(await ngrok.GetLogsAsync(lines ?? 100, cancellationToken), "text/plain"));

app.MapGet("/api/apps", async (ApplicationService applications, CancellationToken cancellationToken) =>
    Results.Ok(await applications.ListAsync(cancellationToken)));

app.MapGet("/api/apps/{id}", async (string id, ApplicationService applications, CancellationToken cancellationToken) =>
{
    try
    {
        var result = await applications.GetAsync(id, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPut("/api/apps/{id}", async (string id, bool? restart, ApplicationSaveRequest request, ApplicationService applications, CancellationToken cancellationToken) =>
{
    if (!string.Equals(id, request.Id, StringComparison.Ordinal))
        return Results.BadRequest(new { error = "Route id must match application id." });

    try
    {
        return Results.Ok(await applications.SaveAsync(request, restart ?? false, cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/apps/{id}/start", async (string id, ApplicationService applications, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(new { status = await applications.StartAsync(id, cancellationToken) }); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/apps/{id}/stop", async (string id, ApplicationService applications, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(new { status = await applications.StopAsync(id, cancellationToken) }); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/apps/{id}/restart", async (string id, ApplicationService applications, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(new { status = await applications.RestartAsync(id, cancellationToken) }); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.Run();

public sealed record ShellRequest(string Command);
public sealed record NgrokTokenRequest(string Token);
public sealed record NgrokStartRequest(int Port);

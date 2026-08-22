using TermuxHost.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddSingleton<ShellService>();
builder.Services.AddSingleton<NgrokService>();

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

app.Run();

public sealed record ShellRequest(string Command);
public sealed record NgrokTokenRequest(string Token);
public sealed record NgrokStartRequest(int Port);

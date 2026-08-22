using System.Diagnostics;

namespace TermuxHost.Services;

public sealed class ShellService
{
    private const string TermuxShell = "/data/data/com.termux/files/usr/bin/bash";

    public async Task<ShellResult> ExecuteAsync(string command, CancellationToken cancellationToken = default)
    {
        var shell = File.Exists(TermuxShell) ? TermuxShell : "/bin/bash";

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = shell,
                Arguments = "-lc \"" + EscapeForDoubleQuotes(command) + "\"",
                WorkingDirectory = Environment.GetEnvironmentVariable("HOME") ?? Directory.GetCurrentDirectory(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        return new ShellResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    private static string EscapeForDoubleQuotes(string value) =>
        value.Replace("\\", "\\\\")
             .Replace("\"", "\\\"")
             .Replace("$", "\\$")
             .Replace("`", "\\`");
}

public sealed record ShellResult(int ExitCode, string StdOut, string StdErr);

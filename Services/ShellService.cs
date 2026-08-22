using System.Diagnostics;

namespace TermuxHost.Services;

public sealed class ShellService
{
    private const string TermuxShell = "/data/data/com.termux/files/usr/bin/bash";

    public async Task<ShellResult> ExecuteAsync(string command, CancellationToken cancellationToken = default)
    {
        var shell = File.Exists(TermuxShell) ? TermuxShell : "/bin/bash";

        var startInfo = new ProcessStartInfo
        {
            FileName = shell,
            WorkingDirectory = Environment.GetEnvironmentVariable("HOME") ?? Directory.GetCurrentDirectory(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-lc");
        startInfo.ArgumentList.Add(command);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        return new ShellResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }
}

public sealed record ShellResult(int ExitCode, string StdOut, string StdErr);

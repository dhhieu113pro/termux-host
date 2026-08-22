using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace TermuxHost.Services;

public sealed class ShellService
{
    private const string TermuxShell = "/data/data/com.termux/files/usr/bin/bash";

    public async Task<ShellResult> ExecuteAsync(string command, CancellationToken cancellationToken = default)
    {
        var shell = File.Exists(TermuxShell) ? TermuxShell : "/bin/bash";

        var startInfo = CreateStartInfo(shell, command);
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

    public async IAsyncEnumerable<ShellStreamEvent> StreamAsync(
        string command,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var shell = File.Exists(TermuxShell) ? TermuxShell : "/bin/bash";
        var channel = Channel.CreateUnbounded<ShellStreamEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        using var process = new Process
        {
            StartInfo = CreateStartInfo(shell, command),
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
                channel.Writer.TryWrite(new ShellStreamEvent("stdout", args.Data));
        };

        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
                channel.Writer.TryWrite(new ShellStreamEvent("stderr", args.Data));
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var completion = Task.Run(async () =>
            {
                try
                {
                    await process.WaitForExitAsync(cancellationToken);
                    process.WaitForExit();
                    channel.Writer.TryWrite(new ShellStreamEvent("exit", process.ExitCode.ToString()));
                    channel.Writer.TryComplete();
                }
                catch (OperationCanceledException)
                {
                    TryKill(process);
                    channel.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    channel.Writer.TryWrite(new ShellStreamEvent("error", ex.Message));
                    channel.Writer.TryComplete(ex);
                }
            }, CancellationToken.None);

            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
                yield return item;

            await completion;
        }
        finally
        {
            if (!process.HasExited)
                TryKill(process);
        }
    }

    private static ProcessStartInfo CreateStartInfo(string shell, string command)
    {
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
        return startInfo;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The process may already have exited.
        }
    }
}

public sealed record ShellResult(int ExitCode, string StdOut, string StdErr);
public sealed record ShellStreamEvent(string Type, string Data);

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WireView2.Services;

/// <summary>Flashes firmware to the WireView Pro II's STM32 DFU bootloader
/// (0483:df11) by driving the system dfu-util binary. Requires the dfu-util
/// package and the shipped udev rule for unprivileged USB access.</summary>
public static class DfuUtilFlasher
{
    public const string DfuVid = "0483";
    public const string DfuPid = "df11";

    private static readonly Regex ProgressRegex = new(@"(\d{1,3})\s*%", RegexOptions.Compiled);

    /// <summary>Returns the dfu-util version string, or null if not installed.</summary>
    public static async Task<string?> GetDfuUtilVersionAsync()
    {
        try
        {
            var (exitCode, stdout, _) = await RunAsync("dfu-util", "--version",
                TimeSpan.FromSeconds(5), null, CancellationToken.None).ConfigureAwait(false);
            if (exitCode != 0) return null;
            // First line: "dfu-util 0.11"
            string first = stdout.Split('\n')[0].Trim();
            return first.Length > 0 ? first : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Polls `dfu-util -l` until the bootloader shows up (device re-enumerates
    /// a moment after CMD_BOOTLOADER). Returns false on timeout.</summary>
    public static async Task<bool> WaitForDfuDeviceAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (exitCode, stdout, _) = await RunAsync("dfu-util", "-l",
                    TimeSpan.FromSeconds(5), null, ct).ConfigureAwait(false);
                if (exitCode == 0 && stdout.Contains($"[{DfuVid}:{DfuPid}]", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // dfu-util hiccup; keep polling until the deadline.
            }
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>Downloads a flat firmware image to internal flash and reboots the
    /// device (DfuSe ":leave"). Progress is 0..1, parsed from dfu-util output.
    /// Throws with dfu-util's output on failure.</summary>
    public static async Task FlashAsync(string binPath, uint baseAddress,
        IProgress<double>? progress, CancellationToken ct)
    {
        string args = $"-d {DfuVid}:{DfuPid} -a 0 -s 0x{baseAddress:X8}:leave -D \"{binPath}\"";
        var (exitCode, stdout, stderr) = await RunAsync("dfu-util", args,
            TimeSpan.FromMinutes(5), progress, ct).ConfigureAwait(false);

        // dfu-util exits 74 ("EX_IOERR") on some versions when the device detaches
        // right after ":leave" even though the download finished — treat a completed
        // "File downloaded successfully" as success regardless of the exit code.
        bool downloaded = stdout.Contains("File downloaded successfully", StringComparison.OrdinalIgnoreCase)
                          || stderr.Contains("File downloaded successfully", StringComparison.OrdinalIgnoreCase);
        if (exitCode != 0 && !downloaded)
        {
            string detail = (stderr.Length > 0 ? stderr : stdout).Trim();
            if (detail.Length > 600) detail = "…" + detail[^600..];
            throw new InvalidOperationException(
                $"dfu-util failed (exit {exitCode}).\n{detail}");
        }
    }

    private static async Task<(int exitCode, string stdout, string stderr)> RunAsync(
        string fileName, string args, TimeSpan timeout, IProgress<double>? progress,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.Start();

        // dfu-util redraws its progress bar with '\r', so read char-by-char instead
        // of line-by-line and surface the latest percentage as it arrives.
        var stdoutTask = Task.Run(async () =>
        {
            var buffer = new char[256];
            var line = new StringBuilder();
            int n;
            while ((n = await process.StandardOutput.ReadAsync(buffer, 0, buffer.Length)
                       .ConfigureAwait(false)) > 0)
            {
                for (int i = 0; i < n; i++)
                {
                    char c = buffer[i];
                    stdout.Append(c);
                    if (c is '\r' or '\n')
                    {
                        var m = ProgressRegex.Match(line.ToString());
                        if (m.Success && int.TryParse(m.Groups[1].Value, out int pct))
                            progress?.Report(Math.Clamp(pct, 0, 100) / 100.0);
                        line.Clear();
                    }
                    else
                    {
                        line.Append(c);
                    }
                }
            }
        }, ct);
        var stderrTask = Task.Run(async () =>
        {
            string text = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            stderr.Append(text);
        }, ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"{fileName} did not finish within {timeout.TotalSeconds:0}s.");
        }
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}

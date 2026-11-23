using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WireView2.Device;

/// <summary>Flashes firmware to the WireView Pro II's STM32 DFU bootloader
/// (0483:df11) by driving the system dfu-util binary. Requires the dfu-util
/// package and the shipped udev rule for unprivileged USB access.</summary>
public static partial class DfuUtilFlasher
{
    public const string DfuVid = "0483";
    public const string DfuPid = "df11";

    /// <summary>The dfu-util executable: "dfu-util" from PATH, or the
    /// WIREVIEW_DFU_UTIL environment variable when set (a test hook: a fake
    /// dfu-util drives the whole flash flow without a device).</summary>
    public static string DfuUtilPath { get; } =
        Environment.GetEnvironmentVariable("WIREVIEW_DFU_UTIL") is { Length: > 0 } custom ? custom : "dfu-util";

    // Source-generated: no regex parser or interpreter at run time (NativeAOT).
    [GeneratedRegex(@"(\d{1,3})\s*%")]
    private static partial Regex ProgressRegex();

    /// <summary>Returns the dfu-util version string, or null if not installed.</summary>
    public static async Task<string?> GetDfuUtilVersionAsync()
    {
        try
        {
            var (exitCode, stdout, _) = await RunAsync(DfuUtilPath, "--version",
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

    [GeneratedRegex(@"path=""([^""]+)""")]
    private static partial Regex PathRegex();

    /// <summary>The USB port paths of the bootloaders `dfu-util -l` lists
    /// (each listed once, though dfu-util prints a line per flash area).</summary>
    internal static List<string> ParseDfuPaths(string listing)
    {
        var paths = new List<string>();
        foreach (var line in listing.Split('\n'))
        {
            if (!line.Contains($"[{DfuVid}:{DfuPid}]", StringComparison.OrdinalIgnoreCase)) continue;
            var m = PathRegex().Match(line);
            string path = m.Success ? m.Groups[1].Value : "";
            if (!paths.Contains(path)) paths.Add(path);
        }
        return paths;
    }

    /// <summary>The USB port paths of the STM32 bootloaders attached now, empty when
    /// none or when dfu-util cannot list them.</summary>
    public static async Task<List<string>> ListDfuPathsAsync(CancellationToken ct)
    {
        try
        {
            var (exitCode, stdout, _) = await RunAsync(DfuUtilPath, "-l",
                TimeSpan.FromSeconds(5), null, ct).ConfigureAwait(false);
            return exitCode == 0 ? ParseDfuPaths(stdout) : new List<string>();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>Polls `dfu-util -l` until the bootloader shows up (device re-enumerates
    /// a moment after CMD_BOOTLOADER): the one on USB port <paramref name="usbPath"/>
    /// (e.g. "3-4") when given, which is the port the device was on, else any.
    /// Returns false on timeout.</summary>
    public static async Task<bool> WaitForDfuDeviceAsync(TimeSpan timeout, CancellationToken ct, string? usbPath = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var paths = await ListDfuPathsAsync(ct).ConfigureAwait(false);
            if (usbPath == null ? paths.Count > 0 : paths.Contains(usbPath))
                return true;
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>Downloads a flat firmware image to internal flash and reboots the
    /// device (DfuSe ":leave"): the bootloader on USB port <paramref name="usbPath"/>
    /// when given, else the only one attached (refused when there are several, so
    /// another device's bootloader is never flashed). Progress is 0..1, parsed from
    /// dfu-util output. Throws with dfu-util's output on failure.</summary>
    public static async Task FlashAsync(string binPath, uint baseAddress,
        IProgress<double>? progress, CancellationToken ct, string? usbPath = null)
    {
        if (usbPath == null && (await ListDfuPathsAsync(ct).ConfigureAwait(false)).Count > 1)
            throw new InvalidOperationException(
                "More than one device is in firmware update mode. Unplug all but the one to update and try again.");
        string target = usbPath == null ? "" : $" -p {usbPath}";
        string args = $"-d {DfuVid}:{DfuPid}{target} -a 0 -s 0x{baseAddress:X8}:leave -D \"{binPath}\"";
        var (exitCode, stdout, stderr) = await RunAsync(DfuUtilPath, args,
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
                        var m = ProgressRegex().Match(line.ToString());
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

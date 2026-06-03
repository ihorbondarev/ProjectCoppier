using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ProjectCloner.Core.Config;
using ProjectCloner.Core.Infrastructure;
using ProjectCloner.Core.Models;

namespace ProjectCloner.Core.Update;

public sealed record UpdateInfo(string Version, string AssetName, string DownloadUrl);

public interface IUpdateService
{
    /// <summary>Returns update info when a newer release is available for this platform, otherwise null.</summary>
    Task<UpdateInfo?> CheckForUpdateAsync(UpdateSettings settings, IProgress<ProgressReport>? log = null, CancellationToken ct = default);

    /// <summary>
    /// Downloads the asset, extracts it, and launches a detached helper that swaps the files after
    /// this process exits and relaunches the app. Returns true when the helper was started
    /// (the caller should then shut the application down).
    /// </summary>
    Task<bool> DownloadAndApplyAsync(UpdateInfo update, IProgress<ProgressReport>? log = null, CancellationToken ct = default);
}

/// <summary>Self-update via GitHub Releases. Picks the asset whose name contains the current runtime identifier.</summary>
public sealed class UpdateService : IUpdateService
{
    private readonly HttpClient _http;

    public UpdateService(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
            _http.DefaultRequestHeaders.Add("User-Agent", "ProjectCloner-Updater");
    }

    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    public async Task<UpdateInfo?> CheckForUpdateAsync(UpdateSettings settings, IProgress<ProgressReport>? log = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(settings.GitHubOwner) || string.IsNullOrWhiteSpace(settings.GitHubRepo))
        {
            log.Warning("GitHub update repository is not configured.");
            return null;
        }

        var url = $"https://api.github.com/repos/{settings.GitHubOwner}/{settings.GitHubRepo}/releases/latest";
        log.Info($"Checking {url}");

        using var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            log.Warning($"GitHub API {(int)response.StatusCode} — no update info.");
            return null;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (!TryParseVersion(tag, out var latest))
        {
            log.Warning($"Could not parse release tag '{tag}'.");
            return null;
        }

        if (latest <= CurrentVersion)
        {
            log.Info($"Up to date (current {CurrentVersion}, latest {latest}).");
            return null;
        }

        var rid = RuntimeInformation.RuntimeIdentifier;
        var asset = FindAssetForRid(root, rid);
        if (asset is null)
        {
            log.Warning($"Release {latest} has no asset for runtime '{rid}'.");
            return null;
        }

        log.Success($"Update available: {latest} ({asset.Value.name}).");
        return new UpdateInfo(latest.ToString(), asset.Value.name, asset.Value.url);
    }

    public async Task<bool> DownloadAndApplyAsync(UpdateInfo update, IProgress<ProgressReport>? log = null, CancellationToken ct = default)
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"projectcloner-update-{update.Version}");
        if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        Directory.CreateDirectory(workDir);

        var archivePath = Path.Combine(workDir, update.AssetName);
        log.Step($"Downloading {update.AssetName}…");
        await using (var src = await _http.GetStreamAsync(update.DownloadUrl, ct))
        await using (var dst = File.Create(archivePath))
            await src.CopyToAsync(dst, ct);

        var extractDir = Path.Combine(workDir, "extracted");
        Directory.CreateDirectory(extractDir);
        log.Info("Extracting update…");
        ZipFile.ExtractToDirectory(archivePath, extractDir, overwriteFiles: true);

        var installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var executable = Environment.ProcessPath ?? Path.Combine(installDir, "ProjectCloner.App");

        log.Step("Launching updater helper; the application will restart.");
        StartHelper(Environment.ProcessId, extractDir, installDir, executable, workDir);
        return true;
    }

    private static void StartHelper(int pid, string sourceDir, string installDir, string executable, string workDir)
    {
        if (OperatingSystem.IsWindows())
        {
            var script = Path.Combine(workDir, "apply-update.bat");
            File.WriteAllText(script, BuildWindowsHelper(), Encoding.ASCII);
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                ArgumentList = { "/c", script, pid.ToString(), sourceDir, installDir, executable },
                UseShellExecute = true,
                CreateNoWindow = true
            });
        }
        else
        {
            var script = Path.Combine(workDir, "apply-update.sh");
            File.WriteAllText(script, BuildUnixHelper());
            Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/bash",
                ArgumentList = { script, pid.ToString(), sourceDir, installDir, executable },
                UseShellExecute = false
            });
        }
    }

    private static string BuildUnixHelper() =>
        """
        #!/bin/bash
        PID="$1"; SRC="$2"; DST="$3"; EXE="$4"
        while kill -0 "$PID" 2>/dev/null; do sleep 0.5; done
        cp -R "$SRC"/. "$DST"/
        chmod +x "$EXE" 2>/dev/null
        "$EXE" &

        """;

    private static string BuildWindowsHelper() =>
        """
        @echo off
        set PID=%1
        set SRC=%2
        set DST=%3
        set EXE=%4
        :waitloop
        tasklist /FI "PID eq %PID%" 2>nul | find "%PID%" >nul
        if not errorlevel 1 (
            timeout /t 1 /nobreak >nul
            goto waitloop
        )
        xcopy /E /Y /I "%SRC%\*" "%DST%\" >nul
        start "" "%EXE%"

        """;

    private static bool TryParseVersion(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var cleaned = tag.TrimStart('v', 'V');
        return Version.TryParse(cleaned, out version!);
    }

    private static (string name, string url)? FindAssetForRid(JsonElement root, string rid)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
            var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (name is not null && url is not null && name.Contains(rid, StringComparison.OrdinalIgnoreCase))
                return (name, url);
        }
        return null;
    }
}

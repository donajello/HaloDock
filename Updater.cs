using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace HaloDock;

/// <summary>Opt-in update check against the project's GitHub releases.</summary>
public static class Updater
{
    public const string Repo = "donajello/HaloDock";
    public const string ReleasesUrl = "https://github.com/" + Repo + "/releases";

    public sealed record Release(Version Version, string Tag, string PageUrl, string? ExeUrl);

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HaloDock/" + CurrentText); // GitHub's API requires one
        return http;
    }

    public static Version Current
    {
        get
        {
            var v = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public static string CurrentText => Current.ToString(3);

    /// <summary>
    /// A published single-file build can replace itself. A development build (bin\Debug, with HaloDock.dll
    /// next to the exe) can only point at the download page.
    /// </summary>
    public static bool CanSelfUpdate =>
        Environment.ProcessPath is { } exe
        && !File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "HaloDock.dll"));

    /// <summary>The latest published release, or null if it isn't newer than this build.</summary>
    public static async Task<Release?> FindNewerAsync()
    {
        var json = await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        version = new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        if (version <= Current) return null;

        var exe = root.GetProperty("assets").EnumerateArray()
            .Where(a => a.GetProperty("name").GetString()?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true)
            .Select(a => a.GetProperty("browser_download_url").GetString())
            .FirstOrDefault();
        return new Release(version, tag, root.GetProperty("html_url").GetString() ?? ReleasesUrl, exe);
    }

    /// <summary>
    /// Downloads the new exe, then hands over to a small script that waits for this process to exit,
    /// swaps the exe in place and starts it again. The caller should shut the app down afterwards.
    /// </summary>
    public static async Task InstallAsync(Release release, IProgress<double>? progress = null)
    {
        if (release.ExeUrl == null) throw new InvalidOperationException("That release has no exe to download.");
        var target = Environment.ProcessPath ?? throw new InvalidOperationException("Can't tell where HaloDock is installed.");

        var dir = Path.Combine(Path.GetTempPath(), "HaloDock-update");
        Directory.CreateDirectory(dir);
        var download = Path.Combine(dir, $"HaloDock-{release.Version.ToString(3)}.exe");

        using (var response = await Http.GetAsync(release.ExeUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var input = await response.Content.ReadAsStreamAsync();
            await using var output = File.Create(download);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        // Sanity check: a Windows executable starts with "MZ".
        var header = new byte[2];
        await using (var check = File.OpenRead(download)) _ = await check.ReadAsync(header);
        if (header[0] != 'M' || header[1] != 'Z') throw new InvalidDataException("The download wasn't a valid program.");

        var pid = Environment.ProcessId;
        var script = Path.Combine(dir, "apply-update.cmd");
        File.WriteAllText(script, string.Join("\r\n",
            "@echo off",
            ":wait",
            $"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul",
            "if not errorlevel 1 ( timeout /t 1 /nobreak >nul & goto wait )",
            $"move /y \"{download}\" \"{target}\" >nul",
            $"start \"\" \"{target}\"",
            "del \"%~f0\""), Encoding.ASCII);

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    public static void OpenReleasesPage(string? url = null) =>
        Process.Start(new ProcessStartInfo(url ?? ReleasesUrl) { UseShellExecute = true });
}

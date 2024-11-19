namespace P2ModLoader.Helper;

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Linq;

public class GitHubRelease {
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = string.Empty;
    [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = new();
    [JsonPropertyName("body")] public string Body { get; set; } = string.Empty;
}

public class GitHubAsset {
    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = string.Empty;
}

public static class AutoUpdater {
    private const string OWNER = "SurDno";
    private const string REPO = "P2ModLoader";

    public static readonly string CurrentVersion;
    private static readonly string UpdateDirectory;
    private static readonly HttpClient Client;
    private static readonly string BaseDirectory;
    private static readonly JsonSerializerOptions JsonOptions;

    static AutoUpdater() {
        var versionInfo = Assembly.GetExecutingAssembly().GetName().Version!;
        CurrentVersion = $"{versionInfo.Major}.{versionInfo.Minor}.{versionInfo.Build}";
        BaseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        UpdateDirectory = Path.Combine(BaseDirectory, "Updates");
        Client = new HttpClient();
        Client.DefaultRequestHeaders.Add("User-Agent", "Auto-Updater");
        JsonOptions = new JsonSerializerOptions {
            PropertyNameCaseInsensitive = true
        };
    }
    
    public static async Task CheckForUpdatesAsync() {
        Logger.LogInfo("Initiating update check...");
        try {
            var releases = await GetAllReleasesAsync();
            if (releases == null || releases.Count == 0 || releases[0].Assets.Count == 0) {
                Logger.LogWarning("No releases found.");
                return;
            }

            var latestRelease = releases[0];
            var newVersion = latestRelease.TagName.TrimStart('v');
            Logger.LogInfo($"Latest version is: {newVersion}, current version is: {CurrentVersion}");

            if (!IsNewer(newVersion)) return;

            var releaseNotes = GetCumulativeReleaseNotes(releases);
            var message = $"A new update is available ({newVersion}).\n" +
                          $"Changes from current version ({CurrentVersion}):\n\n" +
                          
                          $"{releaseNotes}\n\n" +
                          
                          $"Do you want to update?";
            
            var result = MessageBox.Show(message, "Update Available", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (result == DialogResult.Yes)
                await DownloadAndInstallUpdateAsync(latestRelease);
        } catch (Exception ex) {
            ErrorHandler.Handle("Failed to check for updates", ex);
        }
    }

    private static async Task<List<GitHubRelease>?> GetAllReleasesAsync() {
        var response = await Client.GetStringAsync($"https://api.github.com/repos/{OWNER}/{REPO}/releases");
        return JsonSerializer.Deserialize<List<GitHubRelease>>(response, JsonOptions) ?? new List<GitHubRelease>();
    }

    private static string GetCumulativeReleaseNotes(List<GitHubRelease> releases) {
        var relevantReleases = releases
            .Where(r => IsNewer(r.TagName))
            .OrderBy(r => Version.Parse(r.TagName));

        var notes = new System.Text.StringBuilder();
        foreach (var release in relevantReleases) {
            var version = release.TagName.TrimStart('v');
            notes.AppendLine($"{version}:");
            notes.AppendLine(release.Body.Trim());
            notes.AppendLine();
        }

        return notes.ToString().TrimEnd();
    }

    private static bool IsNewer(string latestVersion) => Version.Parse(latestVersion) > Version.Parse(CurrentVersion);

    private static async Task DownloadAndInstallUpdateAsync(GitHubRelease release) {
        Directory.CreateDirectory(UpdateDirectory);

        var assetUrl = release.Assets[0].BrowserDownloadUrl;
        var zipPath = Path.Combine(UpdateDirectory, "update.zip");

        await using var stream = await Client.GetStreamAsync(assetUrl);
        await using var fileStream = File.Create(zipPath);
        await stream.CopyToAsync(fileStream);

        var extractPath = Path.Combine(UpdateDirectory, "extracted");
        if (Directory.Exists(extractPath))
            Directory.Delete(extractPath, true);
        Directory.CreateDirectory(extractPath);

        ZipFile.ExtractToDirectory(zipPath, extractPath);

        var updateScript = CreateUpdateScript(extractPath);
        Process.Start(new ProcessStartInfo {
            FileName = "cmd.exe",
            Arguments = $"/c {updateScript}",
            WindowStyle = ProcessWindowStyle.Hidden
        });

        Environment.Exit(0);
    }

    private static string CreateUpdateScript(string updatePath) {
        var scriptPath = Path.Combine(UpdateDirectory, "update.bat");
        var currentExe = Process.GetCurrentProcess().MainModule!.FileName;

        var script = $"""
                      @echo off
                      timeout /t 2 /nobreak

                      rem Delete all files in the main directory except Updates folder
                      for /F "delims=" %%i in ('dir /b "{BaseDirectory}"') do (
                          if /I not "%%i"=="Updates" if /I not "%%i"=="Settings" if /I not "%%i"=="Logs" (
                              if exist "{BaseDirectory}%%i\*" (
                                  rd /s /q "{BaseDirectory}%%i"
                              ) else (
                                  del /q "{BaseDirectory}%%i"
                              )
                          )
                      )

                      rem Copy all files from update to main directory
                      xcopy "{updatePath}\*" "{BaseDirectory}" /E /H /C /I /Y

                      rem Clean up Updates directory
                      rd /s /q "{updatePath}"
                      del /q "{UpdateDirectory}\update.zip"

                      rem Start the updated application
                      start "" "{currentExe}"

                      rem Delete this script
                      del "%~f0"
                      """;

        File.WriteAllText(scriptPath, script);
        return scriptPath;
    }
}
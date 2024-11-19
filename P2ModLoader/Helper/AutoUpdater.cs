namespace P2ModLoader.Helper;

using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;
using System.Reflection;

public static class AutoUpdater {
    private const string OWNER = "SurDno";
    private const string REPO = "P2ModLoader";
    
    private static readonly string CurrentVersion;
    private static readonly string UpdateDirectory;
    private static readonly HttpClient Client;

    static AutoUpdater() {
        CurrentVersion = Assembly.GetExecutingAssembly().GetName().Version!.ToString();
        UpdateDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Updates");
        Client = new HttpClient();
        Client.DefaultRequestHeaders.Add("User-Agent", "Auto-Updater");
    }

    public static async Task CheckForUpdatesAsync() {
        try {
            var latestRelease = await GetLatestReleaseAsync();
            var latestVersion = latestRelease.tag_name.TrimStart('v');

            if (!IsNewer(latestVersion)) return;

            var message = $"A new update is available ({latestVersion}). Current version is {CurrentVersion}. " +
                          $"Do you want to update?";
            var result = MessageBox.Show(message, "Update Available", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (result == DialogResult.Yes)
                await DownloadAndInstallUpdateAsync(latestRelease);
        } catch (Exception ex) {
            ErrorHandler.Handle("Failed to check for updates", ex);
        }
    }

    private static async Task<dynamic> GetLatestReleaseAsync() {
        var response = await Client.GetStringAsync($"https://api.github.com/repos/{OWNER}/{REPO}/releases/latest");
        return JsonSerializer.Deserialize<dynamic>(response);
    }

    private static bool IsNewer(string latestVersion) => Version.Parse(latestVersion) > Version.Parse(CurrentVersion);
    
    private static async Task DownloadAndInstallUpdateAsync(dynamic release) {
        Directory.CreateDirectory(UpdateDirectory);

        var assetUrl = release.assets[0].browser_download_url.ToString();
        var fileName = Path.Combine(UpdateDirectory, release.assets[0].name.ToString());

        await using var stream = await Client.GetStreamAsync(assetUrl);
        await using var fileStream = File.Create(fileName);
        await stream.CopyToAsync(fileStream);

        Process.Start(new ProcessStartInfo {
            FileName = "cmd.exe",
            Arguments = $"/c {CreateUpdateScript(fileName)}",
            WindowStyle = ProcessWindowStyle.Hidden
        });

        Environment.Exit(0);
    }

    private static string CreateUpdateScript(string updateFile) {
        var currentExe = Process.GetCurrentProcess().MainModule.FileName;
        var scriptPath = Path.Combine(UpdateDirectory, "update.bat");

        var script = $"""

                      @echo off
                      timeout /t 2 /nobreak
                      del "{currentExe}"
                      move /y "{updateFile}" "{currentExe}"
                      start "" "{currentExe}"
                      del "%~f0"

                      """;

        File.WriteAllText(scriptPath, script);
        return scriptPath;
    }
}
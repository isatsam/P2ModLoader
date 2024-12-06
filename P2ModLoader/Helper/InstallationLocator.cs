using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace P2ModLoader.Helper;

public static class InstallationLocator {
    private const string STEAM_32_BIT_PATH = @"SOFTWARE\Valve\Steam";
    private const string STEAM_64_BIT_PATH = @"SOFTWARE\Wow6432Node\Valve\Steam";

    private const string STEAM_LINUX_PATH = @"\.local\share\Steam"; // example: /home/username/.local/share/Steam
    
    private const string PATHOLOGIC_2_STEAM_APP_ID = "230410";
    
    private const string STEAM_LIBRARY_FOLDERS_PATH = @"config\libraryfolders.vdf";
    private const string PATHOLOGIC_STEAM_RELATIVE_PATH = @"steamapps\common\Warframe";

    private const string APPDATA_PATH = @"AppData\LocalLow\Ice-Pick Lodge\Pathologic 2";
    
    public static string? FindSteam() {
        var steamPath = GetSteamPathFromRegistry(STEAM_32_BIT_PATH);
        steamPath ??= GetSteamPathFromRegistry(STEAM_64_BIT_PATH);

        return steamPath;
    }

    public static string? FindSteamLinux()
    {
        if (!Path.Exists(@"Z:\home"))
        {
            // Likely not using Wine (i.e. on Windows), or non-standard Wine setup
            return null;
        }

        var user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        user = user[(user.LastIndexOf('\\')+1)..]; // "DEVICENAME/username" -> "username"

        var steamPath = Path.Join(@"Z:\home\", user, STEAM_LINUX_PATH);
        return steamPath;
    }

    public static string? FindGog()
    {
        // TODO
        Logger.LogWarning("FindGog -- not implemented");
        var gogPath = "";
        return gogPath;
    }

    public static string? FindInstall()
    {
        var steamPath = FindSteam();
        if  (!string.IsNullOrEmpty(steamPath))
        {
            Logger.LogInfo("Found Steam installation:   " + steamPath);
            return FindSteamInstall(steamPath);
        };


        steamPath = FindSteamLinux();
        if (!string.IsNullOrEmpty(steamPath))
        {
            Logger.LogInfo("Found Steam installation:   " + steamPath);
            return FindSteamInstall(steamPath);
        }

        var gogPath = FindGog();
        if (!string.IsNullOrEmpty(gogPath))
        {
            Logger.LogInfo("Found GOG installation:   " + gogPath);
            return FindGogInstall(gogPath);
        }

        return null;
        }

    private static string? FindGogInstall(string gogPath)
    {
        return null;
    }

    private static string? FindSteamInstall(string steamPath) {
        var libraryFoldersPath = Path.Combine(steamPath, STEAM_LIBRARY_FOLDERS_PATH);
        Logger.LogInfo("libraryFoldersPath: " + libraryFoldersPath + " ; " + File.Exists(libraryFoldersPath));
        if (!File.Exists(libraryFoldersPath))
        {
            libraryFoldersPath = Path.Combine(steamPath, STEAM_LIBRARY_FOLDERS_PATH.ToLower());
            if (!File.Exists(libraryFoldersPath))
            {
                return null;
            }
        }

        var installPath = FindPathologicSteamPath(libraryFoldersPath);
        Logger.LogInfo("installPath: " + installPath);
        return installPath;
    }

    private static string? GetSteamPathFromRegistry(string registryPath) {
        using var key = Registry.LocalMachine.OpenSubKey(registryPath);
        return key?.GetValue("InstallPath") as string;
    }

    private static string? FindPathologicSteamPath(string libraryFoldersPath) {
        var content = File.ReadAllText(libraryFoldersPath);

        var pathRegex = new Regex("\"path\"\\s+\"([^\"]+)\"");
        var appRegex = new Regex($"\"{PATHOLOGIC_2_STEAM_APP_ID}\"\\s+\"[^\"]+\"");

        var lines = content.Split('\n');
        string? currentPath = null;
        var foundApp = false;

        foreach (var t in lines) {
            var line = t.Trim();

            var pathMatch = pathRegex.Match(line);
            if (pathMatch.Success)
            {
                currentPath = pathMatch.Groups[1].Value.Replace(@"\\", @"\");
            }

            if (currentPath != null && appRegex.IsMatch(line)) {
                foundApp = true;
                break;
            }

            if (line == "}")
                currentPath = null;
        }

        if (!foundApp)
            return null;

        return currentPath != null ? Path.Combine(currentPath, PATHOLOGIC_STEAM_RELATIVE_PATH) : null;
    }

    public static string? FindAppData() {
        var userPath = Environment.GetEnvironmentVariable("USERPROFILE");
        var appdataPath = Path.Combine(userPath!, APPDATA_PATH);
        
        return Directory.Exists(appdataPath) ? appdataPath : null;
    }
}
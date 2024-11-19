using P2ModLoader.Data;

namespace P2ModLoader.Helper;

public static class SettingsHolder {
	private static string? _installPath;
	private static bool _allowStartupWithConflicts;
	private static bool _isPatched = true;
	private static bool _checkForUpdatesOnStartup = false;
	private static List<SavedModState> _lastKnownModState = new();

	public static event Action? InstallPathChanged,
		StartupWithConflictsChanged,
		PatchStatusChanged,
		ModStateChanged,
		CheckForUpdatesOnStartupChanged;

	public static string? InstallPath {
		get => _installPath;
		set {
			var newPath = value;
			var isValid = newPath != null && File.Exists(Path.Combine(newPath, "Pathologic.exe"));
        
			if (_installPath == newPath) return;
        
			_installPath = isValid ? newPath : null;
			InstallPathChanged?.Invoke();
		}
	}

	public static bool AllowStartupWithConflicts {
		get => _allowStartupWithConflicts;
		set {
			_allowStartupWithConflicts = value;
			StartupWithConflictsChanged?.Invoke();
		}
	}

	public static bool IsPatched {
		get => _isPatched;
		set {
			if (_isPatched == value) return;
			_isPatched = value;
			PatchStatusChanged?.Invoke();
		}
	}

	public static bool CheckForUpdatesOnStartup {
		get => _checkForUpdatesOnStartup;
		set {
			if (_checkForUpdatesOnStartup == value) return;
			_checkForUpdatesOnStartup = value;
			CheckForUpdatesOnStartupChanged?.Invoke();
		}
	}

	public static IReadOnlyList<SavedModState> LastKnownModState {
		get => _lastKnownModState.AsReadOnly();
		set {
			_lastKnownModState = value.ToList();
			ModStateChanged?.Invoke();
		}
	}

	public static void UpdateModState(IEnumerable<Mod> mods) {
		_lastKnownModState = mods.Select(mod => new SavedModState(
			mod.FolderName,
			mod.IsEnabled,
			mod.LoadOrder
		)).ToList();
		ModStateChanged?.Invoke();
	}

}
namespace P2ModLoader.Data;

public class Mod {
	private string? _dependencyError = string.Empty;
	
	public ModInfo Info { get; }
	public string FolderPath { get; }
	public string FolderName => new DirectoryInfo(FolderPath.TrimEnd('/')).Name;
	public bool IsEnabled { get; set; }
	public int LoadOrder { get; set; }
	
	public string? DependencyError {
		set => _dependencyError = value;
		get => string.IsNullOrEmpty(_dependencyError) ? "" : $"\r\nDependency error: {_dependencyError}";
	}

	public Mod(string folderPath) {
		FolderPath = folderPath;
		var infoPath = Path.Combine(folderPath, "ModInfo.ltx");
		Info = ModInfo.FromFile(infoPath);
		IsEnabled = false;
		LoadOrder = 0;
	}

	public string GetModificationTypes() {
		var modTypes = new List<string>();
		
		AddToListIfDefinitionExists(modTypes, "dll", ".dll files");
		AddToListIfDefinitionExists(modTypes, "cs",  "code");
		//AddToListIfDefinitionExists(modTypes, "xml", "virtual machine");
        
		return modTypes.Count > 0 ? $"Modifies: {string.Join(", ", modTypes)}" : "No modifications detected";
	}

	private void AddToListIfDefinitionExists(List<string> modTypes, string extension, string result) {
		if (Directory.GetFiles(FolderPath, $"*.{extension}", SearchOption.AllDirectories).Length != 0) 
			modTypes.Add(result);
	}
}
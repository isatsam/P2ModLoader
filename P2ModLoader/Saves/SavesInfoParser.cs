namespace P2ModLoader.Saves;

public static class SavesInfoParser {
	private static readonly Dictionary<string, string> LocationName = new() {
		["None"] = "Unknown",
		["AndrewBar"] = "The Broken Heart Pub",
		["Grief"] = "Bad Grief's Nest",
		["Olgimski"] = "The Lump: Olgimsky's Seat",
		["Bachelor_MN"] = "Bachelor's Place",
		["Lara"] = "The Shelter",
		["Rubin_Flat"] = "Rubin's Flat",
	};

	public static string? Parse(string save, bool isLast) {
		if (string.IsNullOrEmpty(save)) return null;

		var parts = save.Split('-');
		if (parts.Length < 6) return save;

		var day = parts[0].TrimStart('0').PadLeft(2);
		var time = $"{parts[1]}:{parts[2]}";
		var location = LocationName.GetValueOrDefault(parts[3], parts[3]);
		var lastAdd = isLast ? " (last)" : "";
		var ingameData = $"Day {day}, {time}, {location}{lastAdd}";

		var formattedDateTime = ParseDate(save)!.Value.ToString("dd/MM/yyyy HH:mm:ss");

		return $"{ingameData} - {formattedDateTime}";
	}


	public static DateTime? ParseDate(string save) {
		var parts = save.Split('-');
		if (parts.Length < 6) return null;

		var dt = parts[5].Replace(".xml", "").Split('_').Select(int.Parse).ToArray();
		return new DateTime(dt[0], dt[1], dt[2], dt[3], dt[4], dt[5]);
	}
}
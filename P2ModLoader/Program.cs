using P2ModLoader.Forms;
using P2ModLoader.Helper;

namespace P2ModLoader;

internal static class Program {
	[STAThread]
	private static void Main() {
		SettingsSaver.LoadSettings();

		ApplicationConfiguration.Initialize();
		Application.Run(new MainForm());
	}
}



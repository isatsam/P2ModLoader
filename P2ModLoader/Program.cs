using P2ModLoader.Forms;

namespace P2ModLoader;

internal static class Program {
	[STAThread]
	private static void Main() {
		ApplicationConfiguration.Initialize();
		Application.Run(new MainForm());
	}
}



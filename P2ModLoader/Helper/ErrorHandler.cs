using System;
using System.Windows.Forms;

namespace P2ModLoader.Helper;

public static class ErrorHandler {
	
	public static void Handle(string msg, Exception? e, bool skipLogging = false) {
		var message = e != null ? e.Message : string.Empty;
		var stackTrace = e != null ? e.StackTrace : string.Empty;
		
		if (e != null && !skipLogging)
			Logger.LogError(message + "\n" + stackTrace);
		
		MessageBox.Show($"{msg}: {message}.\n\n{stackTrace}\n\nSee P2ModLoader.log in your Logs directory for more info.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
	}
}
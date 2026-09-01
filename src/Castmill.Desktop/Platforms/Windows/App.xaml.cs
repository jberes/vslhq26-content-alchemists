using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Castmill.Desktop.WinUI;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : MauiWinUIApplication
{
	/// <summary>
	/// Initializes the singleton application object.  This is the first line of authored code
	/// executed, and as such is the logical equivalent of main() or WinMain().
	/// </summary>
	public App()
	{
		RedirectWebViewUserData();
		this.InitializeComponent();
	}

	/// <summary>
	/// WebView2 defaults its user-data folder to the directory holding the executable. An
	/// installed build lives under Program Files, which standard users cannot write to, so
	/// that default makes CoreWebView2 fail to initialise and the app shows a blank window
	/// with no exception. Point it at LocalAppData instead, before any WebView is created.
	/// </summary>
	private static void RedirectWebViewUserData()
	{
		if (Environment.GetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER") is { Length: > 0 })
		{
			return;
		}

		var folder = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"Castmill",
			"WebView2");
		Directory.CreateDirectory(folder);
		Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", folder);
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}


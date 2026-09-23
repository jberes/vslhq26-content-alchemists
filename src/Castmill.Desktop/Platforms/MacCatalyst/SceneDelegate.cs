using Foundation;

namespace Castmill.Desktop;

/// <summary>
/// macOS 26+/iOS 26+ UIKit traps at launch unless the app adopts the UIScene lifecycle.
/// UIKit resolves UISceneDelegateClassName through the Objective-C registrar, so the
/// delegate has to be a type registered by this app — pointing Info.plist straight at
/// MauiUISceneDelegate resolves to nothing and UIKit hands back an empty window.
/// </summary>
[Register("SceneDelegate")]
public class SceneDelegate : MauiUISceneDelegate
{
}

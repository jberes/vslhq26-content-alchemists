namespace Castmill.Desktop;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        // Sized for the md breakpoint (1180) with room to spare; the layout is fluid, so
        // this is a comfortable default rather than a supported minimum (ADR-F10).
        return new Window(new MainPage())
        {
            Title = "Castmill",
            Width = 1280,
            Height = 860,
            MinimumWidth = 1024,
            MinimumHeight = 700,
        };
    }
}

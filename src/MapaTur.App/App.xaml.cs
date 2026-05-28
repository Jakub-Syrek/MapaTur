namespace MapaTur.App;

/// <summary>
/// Root MAUI application object.
/// </summary>
public partial class App : Microsoft.Maui.Controls.Application
{
    /// <summary>Initializes the application and loads its resource dictionary.</summary>
    public App()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override Window CreateWindow(IActivationState? activationState)
    {
        return new Window(new AppShell());
    }
}
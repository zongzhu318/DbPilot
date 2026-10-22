using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DbPilot.Views;

namespace DbPilot;
public sealed partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        if(OperatingSystem.IsMacOS())ConfigureMacAppMenu();
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
            ConfigureMacWindowMenu(desktop.MainWindow);
        }
        base.OnFrameworkInitializationCompleted();
        ApplyMacDockIcon();
    }
}

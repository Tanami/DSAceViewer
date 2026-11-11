using System.Windows;

namespace DSAceLogViewer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow(e.Args.FirstOrDefault(argument => !argument.StartsWith("--")));
        MainWindow = window;
        window.Show();
    }
}

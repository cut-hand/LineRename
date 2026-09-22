using Microsoft.UI.Xaml;

namespace LineRename.App;

public partial class App : Application
{
    private Window? window;

    public App()
    {
        UnhandledException += (_, args) =>
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LineRename");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "last-error.log"), args.Message + Environment.NewLine + args.Exception);
        };
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow();
        window.Activate();
    }
}

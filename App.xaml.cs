using Microsoft.UI.Xaml;
using GameCloudSync.Core;

namespace GameCloudSync;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        // необработанные ошибки пишем в файл, чтобы было с чем разбираться
        UnhandledException += (_, e) =>
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Dir);
                File.AppendAllText(Path.Combine(AppPaths.Dir, "crash.log"), $"[{DateTime.Now:s}] {e.Exception}\n\n");
            }
            catch (IOException) { }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // второй запуск: просим уже открытую программу показаться и выходим
        if (!SingleInstance.TryAcquire())
        {
            SingleInstance.SignalExisting();
            Exit();
            return;
        }

        try
        {
            AppPaths.MigrateOldData();
            Autostart.MigrateOldEntry();
        }
        catch (Exception) { }  // не мешаем запуску, если перенос не удался

        var cfg = AppConfig.Load();
        L.Init(cfg.Language);
        MainWindow = new MainWindow();
        MainWindow.UpdateTray(cfg.MinimizeToTray);
        MainWindow.Activate();

        // автозапуск с Windows: сразу прячемся в трей
        if (cfg.MinimizeToTray && Environment.GetCommandLineArgs().Contains(Autostart.TrayArg))
            MainWindow.AppWindow.Hide();
    }
}

using Microsoft.UI.Xaml;
using GameCloudSync.Core;

namespace GameCloudSync;

/// <summary>
/// Окно приложения: заголовок, страница, значок в трее и скрытие вместо закрытия.
/// </summary>
public sealed partial class MainWindow : Window
{
    readonly IntPtr _hwnd;
    readonly WindowHook _hook;
    readonly TrayIcon _tray;
    bool _exiting;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 760));

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _hook = new WindowHook(_hwnd);
        _hook.Message += (msg, _, _) =>
        {
            if (msg != SingleInstance.ShowMessage) return false;
            ShowFromTray();  // программу запустили ещё раз — показываем это окно
            return true;
        };
        _tray = new TrayIcon(_hwnd, _hook, Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"), "Game Cloud Sync");
        _tray.OpenRequested += ShowFromTray;
        _tray.ExitRequested += ExitApp;

        // крестик прячет окно в трей, если это включено в настройках
        AppWindow.Closing += (_, e) =>
        {
            if (_exiting || !AppConfig.Load().MinimizeToTray) return;
            e.Cancel = true;
            AppWindow.Hide();
        };
        Closed += (_, _) => _tray.Dispose();

        Reload();
    }

    /// <summary>Пересоздаёт главную страницу (после смены языка).</summary>
    public void Reload()
    {
        AppTitleBar.Subtitle = L.T("app.subtitle");
        _tray.OpenText = L.T("tray.open");
        _tray.ExitText = L.T("tray.exit");
        RootFrame.Navigate(typeof(MainPage));
        RootFrame.BackStack.Clear();
    }

    /// <summary>Показывает или убирает значок в трее.</summary>
    public void UpdateTray(bool enabled) => _tray.Visible = enabled;

    public void ShowFromTray()
    {
        AppWindow.Show();
        Activate();
        Native.SetForegroundWindow(_hwnd);
    }

    void ExitApp()
    {
        _exiting = true;
        _tray.Dispose();
        Application.Current.Exit();
    }
}

using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GameCloudSync.Core;

/// <summary>Автозапуск с Windows через HKCU\...\Run.</summary>
public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "GameCloudSync";
    public const string TrayArg = "--tray";

    static string Command => $"\"{Environment.ProcessPath}\" {TrayArg}";

    public static bool IsEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(ValueName) is string v && v.Contains(Environment.ProcessPath ?? "", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool enabled)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) k.SetValue(ValueName, Command);
        else k.DeleteValue(ValueName, false);
    }

    /// <summary>Заменяет запись автозапуска со старым названием программы на новую.</summary>
    public static void MigrateOldEntry()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (k?.GetValue("SteamSaveSync") is null) return;
        k.DeleteValue("SteamSaveSync", false);
        Set(true);
    }
}

/// <summary>Один экземпляр программы: второй запуск показывает окно первого и завершается.</summary>
public static class SingleInstance
{
    static Mutex? _mutex;

    /// <summary>Сообщение «покажи окно», которое второй экземпляр рассылает первому.</summary>
    public static readonly uint ShowMessage = Native.RegisterWindowMessage("GameCloudSync_Show");

    public static bool TryAcquire()
    {
        _mutex = new Mutex(true, @"Local\GameCloudSync_SingleInstance", out var created);
        return created;
    }

    public static void SignalExisting() => Native.PostMessage(Native.HWND_BROADCAST, ShowMessage, 0, 0);
}

/// <summary>
/// Перехват сообщений окна (WinUI 3 не даёт своего API для трея),
/// через замену оконной процедуры.
/// </summary>
public sealed class WindowHook
{
    readonly IntPtr _hwnd;
    readonly IntPtr _oldProc;
    readonly Native.WndProc _proc;  // держим ссылку, иначе делегат соберёт GC
    public event Func<uint, IntPtr, IntPtr, bool>? Message;

    public WindowHook(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _proc = Proc;
        _oldProc = Native.SetWindowLongPtr(hwnd, Native.GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_proc));
    }

    IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (Message?.Invoke(msg, wParam, lParam) == true) return IntPtr.Zero;
        return Native.CallWindowProc(_oldProc, hwnd, msg, wParam, lParam);
    }
}

/// <summary>Значок в области уведомлений с меню «Открыть / Выход».</summary>
public sealed class TrayIcon : IDisposable
{
    const uint CallbackMessage = 0x8000 + 1;  // WM_APP + 1
    const uint WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B;
    static readonly uint TaskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");

    readonly IntPtr _hwnd;
    readonly IntPtr _icon;
    Native.NOTIFYICONDATA _data;
    bool _added;

    public event Action? OpenRequested;
    public event Action? ExitRequested;
    public string OpenText { get; set; } = "Open";
    public string ExitText { get; set; } = "Exit";

    public TrayIcon(IntPtr hwnd, WindowHook hook, string iconPath, string tooltip)
    {
        _hwnd = hwnd;
        _icon = Native.LoadImage(IntPtr.Zero, iconPath, 1 /* IMAGE_ICON */, 0, 0, 0x10 | 0x40 /* LOADFROMFILE | DEFAULTSIZE */);
        _data = new Native.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = 1,
            uFlags = 0x1 | 0x2 | 0x4,  // NIF_MESSAGE | NIF_ICON | NIF_TIP
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = tooltip,
        };
        hook.Message += OnMessage;
    }

    public bool Visible
    {
        get => _added;
        set
        {
            if (value == _added) return;
            _added = value && Native.Shell_NotifyIcon(0 /* NIM_ADD */, ref _data);
            if (!value) Native.Shell_NotifyIcon(2 /* NIM_DELETE */, ref _data);
        }
    }

    bool OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == TaskbarCreated && _added)
        {
            // проводник перезапустился — значок нужно добавить заново
            Native.Shell_NotifyIcon(0, ref _data);
            return false;
        }
        if (msg != CallbackMessage) return false;
        switch ((uint)(lParam.ToInt64() & 0xFFFF))
        {
            case WM_LBUTTONUP:
                OpenRequested?.Invoke();
                break;
            case WM_RBUTTONUP:
            case WM_CONTEXTMENU:
                ShowMenu();
                break;
        }
        return true;
    }

    void ShowMenu()
    {
        var menu = Native.CreatePopupMenu();
        Native.AppendMenu(menu, 0, 1, OpenText);
        Native.AppendMenu(menu, 0x800 /* MF_SEPARATOR */, 0, null);
        Native.AppendMenu(menu, 0, 2, ExitText);
        Native.GetCursorPos(out var pt);
        Native.SetForegroundWindow(_hwnd);
        var cmd = Native.TrackPopupMenu(menu, 0x100 | 0x2 /* RETURNCMD | RIGHTBUTTON */, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        Native.DestroyMenu(menu);
        Native.PostMessage(_hwnd, 0 /* WM_NULL */, 0, 0);
        if (cmd == 1) OpenRequested?.Invoke();
        else if (cmd == 2) ExitRequested?.Invoke();
    }

    public void Dispose()
    {
        Visible = false;
        if (_icon != IntPtr.Zero) Native.DestroyIcon(_icon);
    }
}

static class Native
{
    public const int GWLP_WNDPROC = -4;
    public static readonly IntPtr HWND_BROADCAST = 0xFFFF;

    public delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    public static extern IntPtr CallWindowProc(IntPtr prev, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool AppendMenu(IntPtr menu, uint flags, nuint id, string? text);

    [DllImport("user32.dll")]
    public static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    public static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);
}

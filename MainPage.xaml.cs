using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using GameCloudSync.Core;

namespace GameCloudSync;

public sealed partial class MainPage : Page
{
    readonly AppConfig _cfg = AppConfig.Load();
    readonly ObservableCollection<GameItem> _games = [];
    readonly SemaphoreSlim _busy = new(1, 1);
    Dictionary<string, List<string>> _manifest = [];
    DispatcherQueueTimer? _watcher;
    HashSet<string> _running = [];

    public MainPage()
    {
        InitializeComponent();
        GamesList.ItemsSource = _games;
        AutoToggle.IsOn = _cfg.Auto;
        Unloaded += (_, _) => _watcher?.Stop();
        Loaded += async (_, _) =>
        {
#if !DEBUG
            // в релизе — только строка статуса, без журнала и путей к папкам
            PathsText.Visibility = Visibility.Collapsed;
            LogExpander.Visibility = Visibility.Collapsed;
#endif
            await RunAsync(LoadAllAsync);
            _watcher = DispatcherQueue.CreateTimer();
            _watcher.Interval = TimeSpan.FromSeconds(3);
            _running = await Task.Run(() => Running.Keys(_games.Select(g => g.Game).ToList()));
            _watcher.Tick += (_, _) => WatchGames();
            _watcher.Start();
        };
    }

    // ------------------------------------------------------------ helpers

    enum Level { Debug, Info, Success, Warning, Error }

    /// <summary>
    /// Пишет в журнал (виден только в Debug-сборке) и, кроме уровня Debug,
    /// показывает сообщение в строке статуса.
    /// </summary>
    void Log(string msg, Level level = Level.Info)
    {
        void Append()
        {
            LogBox.Text += $"[{DateTime.Now:HH:mm:ss}] {msg}\n";
            LogBox.SelectionStart = LogBox.Text.Length;
            if (level == Level.Debug) return;
            StatusBar.Message = msg;
            StatusBar.Severity = level switch
            {
                Level.Success => InfoBarSeverity.Success,
                Level.Warning => InfoBarSeverity.Warning,
                Level.Error => InfoBarSeverity.Error,
                _ => InfoBarSeverity.Informational,
            };
            StatusBar.IsOpen = true;
        }
        if (DispatcherQueue.HasThreadAccess) Append();
        else DispatcherQueue.TryEnqueue(Append);
    }

    /// <summary>Выполняет одну операцию за раз, показывает спиннер и пишет ошибки в журнал.</summary>
    async Task RunAsync(Func<Task> work, bool wait = false)
    {
        if (wait) await _busy.WaitAsync();
        else if (!await _busy.WaitAsync(0))
        {
            Log(L.T("busy.wait"));
            return;
        }
        Busy.IsActive = true;
        try { await work(); }
        catch (OperationCanceledException) { Log(L.T("op.cancelled"), Level.Warning); }
        catch (Exception e) { Log(L.F("error", e.Message), Level.Error); }
        finally
        {
            Busy.IsActive = false;
            _busy.Release();
        }
    }

    async Task<bool> ConfirmAsync(string title, string text, string ok)
    {
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = text,
            PrimaryButtonText = ok,
            CloseButtonText = L.T("dlg.cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary;
    }

    Task InfoAsync(string title, string text) =>
        new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = text, CloseButtonText = L.T("dlg.ok") }.ShowAsync().AsTask();

    GameItem? Selected => GamesList.SelectedItem as GameItem;

    /// <summary>Игра, к строке которой относится нажатая кнопка; заодно выделяет эту строку.</summary>
    GameItem? ItemOf(object sender)
    {
        var item = (sender as FrameworkElement)?.Tag as GameItem;
        if (item is not null) GamesList.SelectedItem = item;
        return item;
    }

    List<string> CustomOf(string key) => _cfg.Custom.GetValueOrDefault(key) ?? [];

    Task<Dictionary<string, List<string>>> ScanAsync(GameItem g) =>
        Task.Run(() => Saves.Find(g.Game, _manifest, CustomOf(g.Game.Key)));

    // ------------------------------------------------------------ загрузка

    async Task LoadAllAsync()
    {
        if (await RefreshDriveStatusAsync()) await Drive.MergeDuplicateRootsAsync();
        var steam = await Task.Run(Steam.InstalledGames);
        var gog = await Task.Run(Gog.InstalledGames);
        Log(L.F("games.found", steam.Count, gog.Count), Level.Debug);
        var games = steam.Concat(gog).ToList();
        _manifest = await Manifest.LoadAsync(m => Log(m, Level.Debug));

        var scanned = await Task.Run(() => games
            .Select(g => (g, saves: Saves.Find(g, _manifest, CustomOf(g.Key))))
            .ToList());

        _games.Clear();
        foreach (var (g, saves) in scanned
                     .OrderBy(x => !_cfg.Selected.Contains(x.g.Key))
                     .ThenBy(x => x.saves.Count == 0)
                     .ThenBy(x => x.g.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var item = new GameItem(g)
            {
                Saves = saves,
                IsChecked = _cfg.Selected.Contains(g.Key),
                LastBackup = _cfg.Last.GetValueOrDefault(g.Key) ?? "—",
            };
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(GameItem.IsChecked)) return;
                _cfg.Selected.Remove(item.Game.Key);
                if (item.IsChecked) _cfg.Selected.Add(item.Game.Key);
                _cfg.Save();
                UpdateSelectAll();
            };
            _games.Add(item);
        }
        UpdateSelectAll();
    }

    async Task<bool> RefreshDriveStatusAsync()
    {
        var ok = await Drive.IsConnectedAsync();
        DriveStatus.Text = ok ? L.F("drive.connected", AppPaths.RemoteRoot) : L.T("drive.notConnected");
        DriveIcon.Glyph = ok ? "" : "";
        DriveIcon.Foreground = (Brush)Application.Current.Resources[ok ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush"];
        ConnectButton.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        DisconnectButton.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        return ok;
    }

    // ------------------------------------------------------------ Google Drive

    async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (Drive.RcloneExe is null &&
            !await ConfirmAsync(L.T("rclone.title"), L.T("rclone.text"), L.T("rclone.download")))
            return;

        await RunAsync(async () =>
        {
            if (Drive.RcloneExe is null) await Drive.DownloadRcloneAsync(m => Log(m));
            if (await Drive.IsConnectedAsync())
            {
                Log(L.T("drive.already"));
                return;
            }
            Log(L.T("drive.opening"));
            DriveStatus.Text = L.T("drive.waitingBrowser");
            var (_, _, err) = await Drive.ConnectAsync();
            if (await RefreshDriveStatusAsync())
                Log(L.T("drive.connectedLog"), Level.Success);
            else
                Log(L.F("drive.connectFailed", err.Trim() is { Length: > 0 } t ? t[^Math.Min(300, t.Length)..] : L.T("drive.unknownError")), Level.Error);
        });
    }

    async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync(L.T("disconnect.title"), L.T("disconnect.text"), L.T("btn.disconnect")))
            return;
        await RunAsync(async () =>
        {
            await Drive.DisconnectAsync();
            await RefreshDriveStatusAsync();
            Log(L.T("drive.disconnectedLog"));
        });
    }

    // ------------------------------------------------------------ бэкап

    async void SaveChecked_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(() => BackupManyAsync(_games.Where(g => g.IsChecked).ToList()));

    async void SaveGame_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) await RunAsync(() => BackupManyAsync([item]));
    }

    async Task BackupManyAsync(List<GameItem> items)
    {
        if (items.Count == 0)
        {
            Log(L.T("backup.nothing"), Level.Warning);
            return;
        }
        if (!await Drive.IsConnectedAsync())
        {
            Log(L.T("drive.connectFirst"), Level.Warning);
            return;
        }
        var ct = BeginProgress();
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                var (item, index) = (items[i], i);
                await BackupAsync(item, f => ReportProgress(ProgressLabel("progress.saving", item.Name, index, items.Count),
                                                            (index + f) / items.Count), ct);
            }
        }
        finally { EndProgress(); }
    }

    /// <param name="report">Доля выполненного для этой игры: упаковка — первые 20%, отправка — остальное.</param>
    async Task BackupAsync(GameItem item, Action<double> report, CancellationToken ct)
    {
        var g = item.Game;
        report(0);
        item.Saves = await ScanAsync(item);
        if (!item.HasSaves)
        {
            Log(L.F("backup.noSaves", g.Name), Level.Warning);
            return;
        }
        var tmp = Path.Combine(AppPaths.Dir, $"upload_{g.Store}_{g.Id}.zip");
        File.Delete(tmp);
        try
        {
            var n = await Task.Run(() => Saves.MakeZip(item.Saves, tmp, g, f => report(f * 0.2), ct), ct);
            Log(L.F("backup.uploading", g.Name, n), Level.Debug);
            var dir = await Drive.RemoteDirAsync(g);
            await Drive.UploadAsync(tmp, $"{dir}/{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.zip",
                                    f => report(0.2 + f * 0.8), ct);
        }
        finally { File.Delete(tmp); }
        report(1);

        foreach (var old in (await Drive.ListVersionsAsync(g)).Skip(AppPaths.KeepVersions))
            await Drive.DeleteAsync(old.Path);

        item.LastBackup = _cfg.Last[g.Key] = DateTime.Now.ToString("g", L.Culture);
        _cfg.Save();
        Log(L.F("backup.done", g.Name), Level.Success);
    }

    // ------------------------------------------------------------ восстановление

    async void LoadChecked_Click(object sender, RoutedEventArgs e)
    {
        var items = _games.Where(g => g.IsChecked).ToList();
        if (items.Count == 0)
        {
            Log(L.T("backup.nothing"), Level.Warning);
            return;
        }
        if (!await ConfirmAsync(L.T("loadChecked.title"), L.F("loadChecked.text", items.Count), L.T("btn.load")))
            return;

        await RunAsync(async () =>
        {
            if (!await Drive.IsConnectedAsync()) { Log(L.T("drive.connectFirst"), Level.Warning); return; }
            var running = await Task.Run(() => Running.Keys(items.Select(i => i.Game).ToList()));
            var ct = BeginProgress();
            try
            {
                for (var i = 0; i < items.Count; i++)
                {
                    var (item, index) = (items[i], i);
                    void Report(double f) => ReportProgress(ProgressLabel("progress.loading", item.Name, index, items.Count),
                                                            (index + f) / items.Count);
                    Report(0);
                    if (running.Contains(item.Game.Key))
                    {
                        Log(L.F("restore.skipRunning", item.Name), Level.Warning);
                        continue;
                    }
                    var versions = await Drive.ListVersionsAsync(item.Game);
                    if (versions.Count == 0) Log(L.F("restore.noBackups", item.Name), Level.Warning);
                    else await RestoreAsync(item, versions[0], Report, ct);
                }
            }
            finally { EndProgress(); }
        });
    }

    async void LoadGame_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        if (Running.Keys([item.Game]).Contains(item.Game.Key))
        {
            await InfoAsync(L.T("restore.title"), L.T("restore.closeGame"));
            return;
        }

        List<RemoteFile> versions = [];
        await RunAsync(async () =>
        {
            if (!await Drive.IsConnectedAsync()) { Log(L.T("drive.connectFirst"), Level.Warning); return; }
            versions = await Drive.ListVersionsAsync(item.Game);
            if (versions.Count == 0) Log(L.F("restore.noBackups", item.Name), Level.Warning);
        });
        if (versions.Count == 0) return;

        var list = new ListView
        {
            ItemsSource = versions.Select(v => $"{v.Name[..^4].Replace('_', ' ')}    ({Fmt.Size(v.Size)})").ToList(),
            SelectedIndex = 0,
            MaxHeight = 300,
        };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = L.T("restore.pick"),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(list);
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = L.F("restore.dialogTitle", item.Name),
            Content = panel,
            PrimaryButtonText = L.T("btn.restore"),
            CloseButtonText = L.T("dlg.cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary || list.SelectedIndex < 0) return;
        var version = versions[list.SelectedIndex];

        await RunAsync(async () =>
        {
            var ct = BeginProgress();
            try
            {
                await RestoreAsync(item, version, f => ReportProgress(ProgressLabel("progress.loading", item.Name, 0, 1), f), ct);
            }
            finally { EndProgress(); }
        });
    }

    /// <param name="report">Доля выполненного: страховочная копия — 10%, скачивание — 80%, распаковка — 10%.</param>
    async Task RestoreAsync(GameItem item, RemoteFile version, Action<double> report, CancellationToken ct)
    {
        var g = item.Game;
        var current = await ScanAsync(item);
        if (current.Count > 0)
        {
            // локальная страховка перед перезаписью
            Directory.CreateDirectory(AppPaths.LocalBackups);
            var safe = Path.Combine(AppPaths.LocalBackups, $"{g.Store}_{g.Id}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.zip");
            try { await Task.Run(() => Saves.MakeZip(current, safe, g, f => report(f * 0.1), ct), ct); }
            catch (OperationCanceledException)
            {
                File.Delete(safe);
                throw;
            }
            Log(L.F("restore.safety", g.Name, safe), Level.Debug);
        }
        var tmp = Path.Combine(AppPaths.Dir, $"download_{g.Store}_{g.Id}.zip");
        Log(L.F("restore.downloading", g.Name, version.Name), Level.Debug);
        try
        {
            await Drive.DownloadAsync(version.Path, tmp, f => report(0.1 + f * 0.8), ct);
            ct.ThrowIfCancellationRequested();
            // дальше файлы уже перезаписываются — отменять поздно
            SetCancelable(false);
            var (n, roots) = await Task.Run(() => Saves.ExtractZip(tmp, g, f => report(0.9 + f * 0.1)));
            Log(L.F("restore.done", g.Name, n, string.Join("; ", roots)), Level.Debug);
            Log(L.F("restore.ok", g.Name), Level.Success);
        }
        finally
        {
            File.Delete(tmp);
            SetCancelable(true);
        }
        item.Saves = await ScanAsync(item);
        ShowPaths();
    }

    // ------------------------------------------------------------ прогресс и отмена

    CancellationTokenSource? _cts;

    /// <summary>Показывает полоску прогресса и возвращает токен, который срабатывает по кнопке «Отмена».</summary>
    CancellationToken BeginProgress()
    {
        _cts = new CancellationTokenSource();
        ProgressText.Text = "";
        ProgressPercent.Text = "0%";
        ProgressBarControl.Value = 0;
        CancelButton.IsEnabled = true;
        ProgressPanel.Visibility = Visibility.Visible;
        return _cts.Token;
    }

    void ReportProgress(string text, double fraction)
    {
        void Apply()
        {
            if (_cts is null) return;
            if (!_cts.IsCancellationRequested) ProgressText.Text = text;
            var percent = Math.Clamp(fraction * 100, 0, 100);
            ProgressBarControl.Value = percent;
            ProgressPercent.Text = $"{percent:0}%";
        }
        if (DispatcherQueue.HasThreadAccess) Apply();
        else DispatcherQueue.TryEnqueue(Apply);
    }

    void SetCancelable(bool cancelable)
    {
        void Apply() => CancelButton.IsEnabled = cancelable && _cts is { IsCancellationRequested: false };
        if (DispatcherQueue.HasThreadAccess) Apply();
        else DispatcherQueue.TryEnqueue(Apply);
    }

    void EndProgress()
    {
        ProgressPanel.Visibility = Visibility.Collapsed;
        _cts?.Dispose();
        _cts = null;
    }

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CancelButton.IsEnabled = false;
        ProgressText.Text = L.T("op.cancelling");
    }

    /// <summary>«Сохраняю ELDEN RING» или «Сохраняю ELDEN RING (2/5)», если игр несколько.</summary>
    static string ProgressLabel(string key, string name, int index, int count) =>
        count == 1 ? L.F(key + ".one", name) : L.F(key, name, index + 1, count);

    // ------------------------------------------------------------ свои папки

    async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        var picker = new FolderPicker(App.MainWindow!.AppWindow.Id);
        var result = await picker.PickSingleFolderAsync();
        if (result is null) return;
        var path = Path.GetFullPath(result.Path).TrimEnd('\\');
        if (!_cfg.Custom.TryGetValue(item.Game.Key, out var list)) _cfg.Custom[item.Game.Key] = list = [];
        if (!list.Contains(path)) list.Add(path);
        _cfg.Save();
        item.Saves = await ScanAsync(item);
        ShowPaths();
    }

    async void ClearFolders_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item || !_cfg.Custom.Remove(item.Game.Key)) return;
        _cfg.Save();
        item.Saves = await ScanAsync(item);
        ShowPaths();
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await RunAsync(LoadAllAsync);

    void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        // если отмечено не всё — отмечаем всё, иначе снимаем все галочки
        var check = _games.Any(g => !g.IsChecked);
        foreach (var g in _games) g.IsChecked = check;
        UpdateSelectAll();
    }

    void UpdateSelectAll()
    {
        var count = _games.Count(g => g.IsChecked);
        SelectAllBox.IsChecked = count == 0 ? false : count == _games.Count ? true : null;
        CheckedCountText.Text = L.F("list.checkedCount", count, _games.Count);
    }

    void GamesList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowPaths();

    void ShowPaths()
    {
        if (Selected is not { } item) return;
        PathsText.Text = item.HasSaves
            ? L.F("paths.found", string.Join(";  ", item.Saves.Keys))
            : L.T("paths.none");
    }

    // ------------------------------------------------------------ настройки

    async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var tray = new ToggleSwitch { Header = L.T("settings.tray"), IsOn = _cfg.MinimizeToTray };
        var autostart = new ToggleSwitch { Header = L.T("settings.autostart"), IsOn = Autostart.IsEnabled };
        var language = new ComboBox { Header = L.T("lang.label"), MinWidth = 260 };
        language.Items.Add(L.F("lang.auto", L.NameOf(L.Detect())));
        foreach (var (_, name) in L.Languages) language.Items.Add(name);
        var idx = Array.FindIndex(L.Languages, l => l.Code == _cfg.Language);
        language.SelectedIndex = idx < 0 ? 0 : idx + 1;

        var panel = new StackPanel { Spacing = 20, MinWidth = 400 };
        panel.Children.Add(Setting(tray, "settings.tray.desc"));
        panel.Children.Add(Setting(autostart, "settings.autostart.desc"));
        panel.Children.Add(language);

        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = L.T("settings.title"),
            Content = panel,
            PrimaryButtonText = L.T("dlg.save"),
            CloseButtonText = L.T("dlg.cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        _cfg.MinimizeToTray = tray.IsOn;
        try { Autostart.Set(autostart.IsOn); }
        catch (Exception ex) { Log(L.F("error", ex.Message), Level.Error); }
        var newLanguage = language.SelectedIndex <= 0 ? "" : L.Languages[language.SelectedIndex - 1].Code;
        var languageChanged = newLanguage != _cfg.Language;
        _cfg.Language = newLanguage;
        _cfg.Save();
        App.MainWindow?.UpdateTray(_cfg.MinimizeToTray);

        if (languageChanged)
        {
            L.Init(newLanguage);
            // пересоздаём страницу, чтобы все надписи взяли новый язык
            DispatcherQueue.TryEnqueue(() => App.MainWindow?.Reload());
        }
    }

    static StackPanel Setting(ToggleSwitch toggle, string descriptionKey)
    {
        var p = new StackPanel { Spacing = 2 };
        p.Children.Add(toggle);
        p.Children.Add(new TextBlock
        {
            Text = L.T(descriptionKey),
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        return p;
    }

    // ------------------------------------------------------------ авто-режим

    void AutoToggle_Toggled(object sender, RoutedEventArgs e)
    {
        _cfg.Auto = AutoToggle.IsOn;
        _cfg.Save();
    }

    bool _watching;

    async void WatchGames()
    {
        if (_watching) return;  // предыдущая проверка ещё не закончилась
        _watching = true;
        HashSet<string> now;
        try { now = await Task.Run(() => Running.Keys(_games.Select(g => g.Game).ToList())); }
        finally { _watching = false; }
        var prev = _running;
        _running = now;

        foreach (var started in _games.Where(g => now.Contains(g.Game.Key) && !prev.Contains(g.Game.Key)))
            Log(L.F("game.started", started.Name), Level.Debug);

        var closed = _games.Where(g => prev.Contains(g.Game.Key) && !now.Contains(g.Game.Key)).ToList();
        foreach (var c in closed) Log(L.F("game.closed", c.Name), Level.Debug);
        var toBackup = closed.Where(c => _cfg.Auto && c.IsChecked).ToList();
        if (toBackup.Count == 0) return;
        // небольшая пауза, чтобы игра успела дописать сейв
        await Task.Delay(5000);
        await RunAsync(() => BackupManyAsync(toBackup), wait: true);
    }
}

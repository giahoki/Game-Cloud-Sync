using System.ComponentModel;
using System.Runtime.CompilerServices;
using GameCloudSync.Core;

namespace GameCloudSync;

/// <summary>Строка списка игр.</summary>
public sealed class GameItem(Game game) : INotifyPropertyChanged
{
    public Game Game { get; } = game;
    public string Name => Game.Name;
    public string StoreLabel => Game.StoreLabel;

    Dictionary<string, List<string>> _saves = [];
    public Dictionary<string, List<string>> Saves
    {
        get => _saves;
        set { _saves = value; OnChanged(); OnChanged(nameof(SavesInfo)); OnChanged(nameof(HasSaves)); }
    }

    public bool HasSaves => _saves.Count > 0;

    public string SavesInfo
    {
        get
        {
            var files = _saves.Values.SelectMany(f => f).ToList();
            if (files.Count == 0) return L.T("saves.none");
            long size = files.Sum(f => File.Exists(f) ? new FileInfo(f).Length : 0);
            return L.F("saves.info", files.Count, Fmt.Size(size));
        }
    }

    bool _checked;
    public bool IsChecked
    {
        get => _checked;
        set { if (_checked != value) { _checked = value; OnChanged(); } }
    }

    string _last = "—";
    public string LastBackup
    {
        get => _last;
        set { _last = value; OnChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

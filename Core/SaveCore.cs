using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace GameCloudSync.Core;

/// <summary>Пути и настройки приложения.</summary>
public static class AppPaths
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameCloudSync");
    public static readonly string Config = Path.Combine(Dir, "config.json");
    public static readonly string RcloneConf = Path.Combine(Dir, "rclone.conf");
    public static readonly string ManifestYaml = Path.Combine(Dir, "manifest.yaml");
    public static readonly string ManifestIndex = Path.Combine(Dir, "manifest_index_v2.json");
    public static readonly string LocalBackups = Path.Combine(Dir, "local_backups");

    public const string ManifestUrl = "https://raw.githubusercontent.com/mtkennerly/ludusavi-manifest/master/data/manifest.yaml";
    public const string RcloneUrl = "https://downloads.rclone.org/rclone-current-windows-amd64.zip";
    public const string Remote = "gdrive";
    public const string RemoteRoot = "GameCloudSync";
    public const int KeepVersions = 10;

    /// <summary>Переносит настройки из папки старого названия программы (Steam Save Sync).</summary>
    public static void MigrateOldData()
    {
        var old = Path.Combine(Path.GetDirectoryName(Dir)!, "SteamSaveSync");
        if (Directory.Exists(old) && !Directory.Exists(Dir))
            Directory.Move(old, Dir);
    }
}

public class AppConfig
{
    public List<string> Selected { get; set; } = [];
    public Dictionary<string, List<string>> Custom { get; set; } = [];
    public Dictionary<string, string> Last { get; set; } = [];
    public bool Auto { get; set; } = true;
    /// <summary>Крестик прячет окно в трей, чтобы автобэкап продолжал работать.</summary>
    public bool MinimizeToTray { get; set; } = true;
    /// <summary>Код языка интерфейса; пустая строка — по языку Windows.</summary>
    public string Language { get; set; } = "";

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static AppConfig Load()
    {
        try { return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(AppPaths.Config)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.Dir);
        var tmp = AppPaths.Config + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));
        File.Move(tmp, AppPaths.Config, true);
    }
}

/// <summary>Установленная игра из Steam или GOG.</summary>
/// <param name="Store">"steam" или "gog".</param>
/// <param name="Id">ID игры в магазине.</param>
/// <param name="InstallPath">Папка с игрой.</param>
/// <param name="Root">Библиотека (для Steam) или папка, где лежит папка игры.</param>
public record Game(string Store, string Id, string Name, string InstallPath, string Root)
{
    /// <summary>Ключ в настройках. У Steam — просто appid, чтобы старые настройки не потерялись.</summary>
    public string Key => Store == "steam" ? Id : $"{Store}:{Id}";
    public string InstallDir => Path.GetFileName(InstallPath);
    public string StoreLabel => Store == "gog" ? "GOG" : "Steam";
}

// ------------------------------------------------------------------ Steam

public static class Steam
{
    public static string Path_ => (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string)
                                  is { } p ? Path.GetFullPath(p) : @"C:\Program Files (x86)\Steam";

    public static string RunningAppId() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "RunningAppID", 0)?.ToString() ?? "0";

    /// <summary>Минимальный парсер текстового VDF (KeyValues).</summary>
    public static Dictionary<string, object> ParseVdf(string text)
    {
        var root = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<Dictionary<string, object>>();
        var cur = root;
        string? key = null;
        foreach (Match m in Regex.Matches(text, "\"((?:[^\"\\\\]|\\\\.)*)\"|([{}])"))
        {
            if (m.Groups[2].Value == "{")
            {
                var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                cur[key ?? ""] = d;
                stack.Push(cur);
                cur = d;
                key = null;
            }
            else if (m.Groups[2].Value == "}")
                cur = stack.Count > 0 ? stack.Pop() : root;
            else if (key is null)
                key = m.Groups[1].Value.Replace(@"\\", @"\");
            else
            {
                cur[key] = m.Groups[1].Value.Replace(@"\\", @"\");
                key = null;
            }
        }
        return root;
    }

    public static List<Game> InstalledGames()
    {
        var sp = Path_;
        var libs = new List<string> { sp };
        try
        {
            var vdf = ParseVdf(File.ReadAllText(Path.Combine(sp, "steamapps", "libraryfolders.vdf")));
            if (vdf.GetValueOrDefault("libraryfolders") is Dictionary<string, object> lf)
                foreach (var v in lf.Values)
                    if (v is Dictionary<string, object> d && d.GetValueOrDefault("path") is string p)
                        libs.Add(Path.GetFullPath(p));
        }
        catch (IOException) { }

        var games = new Dictionary<string, Game>();
        foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                Dictionary<string, object>? st;
                try { st = ParseVdf(File.ReadAllText(acf)).GetValueOrDefault("AppState") as Dictionary<string, object>; }
                catch (IOException) { continue; }
                if (st?.GetValueOrDefault("appid") is not string appId) continue;
                var name = st.GetValueOrDefault("name") as string ?? appId;
                // пропускаем Steamworks Redist, Proton и прочие служебные «игры»
                if (appId == "228980" || name.StartsWith("Steamworks") || name.StartsWith("Proton")) continue;
                var dir = Path.Combine(lib, "steamapps", "common", st.GetValueOrDefault("installdir") as string ?? "");
                games[appId] = new Game("steam", appId, name, dir, lib);
            }
        }
        return games.Values.ToList();
    }
}

// ------------------------------------------------------------------ GOG

public static class Gog
{
    public static List<Game> InstalledGames()
    {
        var games = new List<Game>();
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var root = hklm.OpenSubKey(@"SOFTWARE\GOG.com\Games");
            if (root is null) continue;
            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                if (k is null) continue;
                // у DLC заполнено dependsOn — их пропускаем, сейвы у них общие с игрой
                if (k.GetValue("dependsOn") is string dep && dep.Trim().Length > 0) continue;
                if (k.GetValue("path") is not string path || !Directory.Exists(path)) continue;
                var id = k.GetValue("gameID") as string ?? sub;
                if (games.Any(g => g.Id == id)) continue;
                path = Path.GetFullPath(path).TrimEnd('\\');
                games.Add(new Game("gog", id, k.GetValue("gameName") as string ?? id, path,
                                   Path.GetDirectoryName(path) ?? path));
            }
        }
        return games;
    }
}

// ------------------------------------------------------------------ запущенные игры

public static class Running
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    const uint QueryLimitedInformation = 0x1000;

    static IEnumerable<string> ProcessPaths()
    {
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                var h = OpenProcess(QueryLimitedInformation, false, p.Id);
                if (h == IntPtr.Zero) continue;
                try
                {
                    var sb = new StringBuilder(1024);
                    var size = sb.Capacity;
                    if (QueryFullProcessImageName(h, 0, sb, ref size)) yield return sb.ToString();
                }
                finally { CloseHandle(h); }
            }
        }
    }

    /// <summary>Ключи игр, которые сейчас запущены (Steam — по реестру, GOG — по exe из папки игры).</summary>
    public static HashSet<string> Keys(IEnumerable<Game> games)
    {
        var result = new HashSet<string>();
        var steamId = Steam.RunningAppId();
        if (steamId != "0") result.Add(steamId);

        var gog = games.Where(g => g.Store == "gog").ToList();
        if (gog.Count == 0) return result;
        foreach (var exe in ProcessPaths())
            foreach (var g in gog)
                if (exe.StartsWith(g.InstallPath + "\\", StringComparison.OrdinalIgnoreCase))
                    result.Add(g.Key);
        return result;
    }
}

// ------------------------------------------------------------------ Ludusavi manifest

public static class Manifest
{
    class Entry
    {
        public Dictionary<string, FileMeta?>? Files { get; set; }
        public StoreInfo? Steam { get; set; }
        public StoreInfo? Gog { get; set; }
        public ExtraIds? Id { get; set; }
        public Dictionary<string, object?>? InstallDir { get; set; }
    }
    class StoreInfo { public long? Id { get; set; } }
    class ExtraIds
    {
        public List<long>? SteamExtra { get; set; }
        public List<long>? GogExtra { get; set; }
    }
    class FileMeta
    {
        public List<string>? Tags { get; set; }
        public List<When>? When { get; set; }
    }
    class When
    {
        public string? Os { get; set; }
        public string? Store { get; set; }
    }

    public static async Task<Dictionary<string, List<string>>> LoadAsync(Action<string> log, bool force = false)
    {
        var fresh = File.Exists(AppPaths.ManifestIndex) &&
                    DateTime.Now - File.GetLastWriteTime(AppPaths.ManifestIndex) < TimeSpan.FromDays(7);
        if (fresh && !force) return ReadIndex();
        // база уже скачана недавно — только пересобираем индекс (например, после обновления программы)
        if (!force && File.Exists(AppPaths.ManifestYaml) &&
            DateTime.Now - File.GetLastWriteTime(AppPaths.ManifestYaml) < TimeSpan.FromDays(7))
        {
            log(L.T("manifest.reindex"));
            try
            {
                var index = await Task.Run(BuildIndex);
                log(L.F("manifest.ready", index.Count));
                return index;
            }
            catch (Exception e) { log(L.F("manifest.parseFailed", e.Message)); }
        }
        try
        {
            log(L.T("manifest.downloading"));
            Directory.CreateDirectory(AppPaths.Dir);
            using (var http = new HttpClient())
            await using (var s = await http.GetStreamAsync(AppPaths.ManifestUrl))
            await using (var f = File.Create(AppPaths.ManifestYaml + ".part"))
                await s.CopyToAsync(f);
            File.Move(AppPaths.ManifestYaml + ".part", AppPaths.ManifestYaml, true);
            log(L.T("manifest.parsing"));
            var index = await Task.Run(BuildIndex);
            log(L.F("manifest.ready", index.Count));
            return index;
        }
        catch (Exception e)
        {
            log(L.F("manifest.failed", e.Message));
            return ReadIndex();
        }
    }

    static Dictionary<string, List<string>> ReadIndex()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(AppPaths.ManifestIndex)) ?? []; }
        catch { return []; }
    }

    static Dictionary<string, List<string>> BuildIndex()
    {
        var des = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        using var reader = new StreamReader(AppPaths.ManifestYaml, Encoding.UTF8);
        var data = des.Deserialize<Dictionary<string, Entry?>>(reader);
        // ключи: "steam:<id>", "gog:<id>" и запасной "dir:<папка игры>" для игр без ID в базе
        var index = new Dictionary<string, List<string>>();
        foreach (var entry in data.Values)
        {
            if (entry?.Files is null) continue;

            List<string> PathsFor(string? store)
            {
                var paths = new List<string>();
                foreach (var (path, meta) in entry.Files)
                {
                    if (meta?.Tags is { Count: > 0 } tags && !tags.Contains("save")) continue;
                    if (meta?.When is { Count: > 0 } whens &&
                        !whens.Any(w => (w.Os is null or "windows") && (w.Store is null || w.Store == store))) continue;
                    paths.Add(path);
                }
                return paths;
            }

            void Add(string store, IEnumerable<long> ids)
            {
                var paths = PathsFor(store);
                if (paths.Count == 0) return;
                foreach (var id in ids) index.TryAdd($"{store}:{id}", paths);
            }

            Add("steam", new[] { entry.Steam?.Id }.OfType<long>().Concat(entry.Id?.SteamExtra ?? []));
            Add("gog", new[] { entry.Gog?.Id }.OfType<long>().Concat(entry.Id?.GogExtra ?? []));
            if (entry.InstallDir is { Count: > 0 } && PathsFor(null) is { Count: > 0 } any)
                foreach (var dir in entry.InstallDir.Keys)
                    index.TryAdd($"dir:{dir.ToLowerInvariant()}", any);
        }
        File.WriteAllText(AppPaths.ManifestIndex, JsonSerializer.Serialize(index));
        return index;
    }
}

// ------------------------------------------------------------------ поиск и упаковка сейвов

public static class Saves
{
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    static string? Expand(string path, Game g)
    {
        var repl = new Dictionary<string, string>
        {
            ["<base>"] = g.InstallPath,
            ["<root>"] = g.Root,
            ["<game>"] = g.InstallDir,
            ["<home>"] = Home,
            ["<storeUserId>"] = "*",
            ["<storeGameId>"] = g.Id,
            ["<osUserName>"] = Environment.UserName,
            ["<winAppData>"] = AppData,
            ["<winLocalAppData>"] = LocalAppData,
            ["<winLocalAppDataLow>"] = Path.Combine(Path.GetDirectoryName(LocalAppData)!, "LocalLow"),
            ["<winDocuments>"] = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            ["<winPublic>"] = Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public",
            ["<winProgramData>"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            ["<winDir>"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        };
        foreach (var (k, v) in repl) path = path.Replace(k, v);
        return path.Contains('<') ? null : path.Replace('/', '\\');  // linux/mac плейсхолдеры
    }

    /// <summary>Разворачивает шаблон с * и ** в реальные пути (файлы и папки).</summary>
    static IEnumerable<string> Glob(string pattern)
    {
        var parts = pattern.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) yield break;
        IEnumerable<string> current = [parts[0] + "\\"];
        for (var i = 1; i < parts.Length; i++)
        {
            var part = parts[i];
            var last = i == parts.Length - 1;
            current = current.SelectMany(dir => Step(dir, part, last)).ToList();
        }
        foreach (var p in current) yield return p;

        static IEnumerable<string> Step(string dir, string part, bool last)
        {
            if (!Directory.Exists(dir)) return [];
            try
            {
                if (part == "**")
                    return Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories).Prepend(dir);
                if (!part.Contains('*') && !part.Contains('?'))
                {
                    var p = Path.Combine(dir, part);
                    return Directory.Exists(p) || (last && File.Exists(p)) ? [p] : [];
                }
                return last ? Directory.EnumerateFileSystemEntries(dir, part) : Directory.EnumerateDirectories(dir, part);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        }
    }

    /// <summary>{папка-корень: [файлы]}.</summary>
    public static Dictionary<string, List<string>> Find(Game g, Dictionary<string, List<string>> manifest, List<string> custom)
    {
        var patterns = new List<string>();
        if (g.Store == "steam")
            patterns.Add(Path.Combine(Steam.Path_, "userdata", "*", g.Id, "remote"));
        var known = manifest.GetValueOrDefault($"{g.Store}:{g.Id}")
                    ?? manifest.GetValueOrDefault($"dir:{g.InstallDir.ToLowerInvariant()}")
                    ?? [];
        foreach (var p in known)
            if (Expand(p, g) is { } e) patterns.Add(e);
        patterns.AddRange(custom);

        var gameDir = g.InstallPath;
        var groups = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pat in patterns)
        foreach (var m0 in Glob(pat))
        {
            var m = Path.GetFullPath(m0).TrimEnd('\\');
            if (Directory.Exists(m))
            {
                // защита от случайного бэкапа целой папки игры
                if (string.Equals(m, gameDir, StringComparison.OrdinalIgnoreCase) && !custom.Contains(m)) continue;
                string[] files;
                try { files = Directory.GetFiles(m, "*", SearchOption.AllDirectories); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                if (files.Length > 0)
                {
                    if (!groups.TryGetValue(m, out var set)) groups[m] = set = new(StringComparer.OrdinalIgnoreCase);
                    set.UnionWith(files);
                }
            }
            else if (File.Exists(m))
            {
                var dir = Path.GetDirectoryName(m)!;
                if (!groups.TryGetValue(dir, out var set)) groups[dir] = set = new(StringComparer.OrdinalIgnoreCase);
                set.Add(m);
            }
        }

        // убираем вложенные корни (если A внутри B — файлы A и так есть в B)
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in groups.Keys.OrderBy(k => k.Length))
        {
            var parent = result.Keys.FirstOrDefault(k => r.StartsWith(k + "\\", StringComparison.OrdinalIgnoreCase));
            if (parent is not null) result[parent].UnionWith(groups[r]);
            else result[r] = new(groups[r], StringComparer.OrdinalIgnoreCase);
        }
        return result.ToDictionary(k => k.Key, v => v.Value.Order().ToList());
    }

    /// <summary>
    /// Пути в бэкапе храним относительно папки игры, Steam и профиля пользователя,
    /// чтобы на другом ПК (другой диск, другое имя пользователя) они легли на свои места.
    /// </summary>
    static (string token, string real)[] PortableRoots(Game? g) =>
    [
        .. g is null ? [] : new[] { ("<game>", g.InstallPath) },
        ("<steam>", Steam.Path_),
        ("<home>", Home),
    ];

    static string ToPortable(string path, Game? g)
    {
        foreach (var (token, real) in PortableRoots(g))
            if (path.Equals(real, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(real.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                return token + path[real.TrimEnd('\\').Length..];
        return path;
    }

    static string FromPortable(string path, Game? g)
    {
        foreach (var (token, real) in PortableRoots(g))
            if (path.StartsWith(token, StringComparison.Ordinal))
                return real.TrimEnd('\\') + path[token.Length..];
        return path;
    }

    /// <param name="progress">Доля упакованных байт, 0..1.</param>
    public static int MakeZip(Dictionary<string, List<string>> groups, string dest, Game? game = null,
                              Action<double>? progress = null, CancellationToken ct = default)
    {
        var roots = new Dictionary<string, string>();
        var count = 0;
        long total = groups.Values.SelectMany(f => f).Sum(f => File.Exists(f) ? new FileInfo(f).Length : 0), done = 0;
        using var zip = ZipFile.Open(dest, ZipArchiveMode.Create);
        var i = 0;
        foreach (var (root, files) in groups)
        {
            roots[i.ToString()] = ToPortable(root, game);
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // FileShare.ReadWrite — чтобы читать файлы, открытые игрой
                    using var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var entry = zip.CreateEntry($"{i}/{Path.GetRelativePath(root, f).Replace('\\', '/')}", CompressionLevel.Optimal);
                    entry.LastWriteTime = File.GetLastWriteTime(f);
                    using var dst = entry.Open();
                    var buffer = new byte[1 << 20];
                    int read;
                    while ((read = src.Read(buffer)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        dst.Write(buffer, 0, read);
                        done += read;
                        progress?.Invoke(total > 0 ? (double)done / total : 1);
                    }
                    count++;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            i++;
        }
        var meta = zip.CreateEntry("_savesync.json");
        using (var w = new StreamWriter(meta.Open()))
            w.Write(JsonSerializer.Serialize(new { created = DateTime.Now.ToString("s"), roots }));
        return count;
    }

    /// <remarks>Отменить распаковку нельзя: наполовину записанные сейвы хуже, чем никакие.</remarks>
    public static (int count, List<string> roots) ExtractZip(string src, Game? game = null, Action<double>? progress = null)
    {
        using var zip = ZipFile.OpenRead(src);
        using var metaStream = zip.GetEntry("_savesync.json")!.Open();
        var meta = JsonDocument.Parse(metaStream);
        var roots = meta.RootElement.GetProperty("roots").EnumerateObject()
            .ToDictionary(p => p.Name, p => FromPortable(p.Value.GetString()!, game));
        var n = 0;
        var entries = zip.Entries.Where(e => e.FullName != "_savesync.json" && !e.FullName.EndsWith('/')).ToList();
        foreach (var e in entries)
        {
            var slash = e.FullName.IndexOf('/');
            var target = Path.GetFullPath(Path.Combine(roots[e.FullName[..slash]], e.FullName[(slash + 1)..]));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, true);
            n++;
            progress?.Invoke((double)n / entries.Count);
        }
        return (n, roots.Values.ToList());
    }
}

// ------------------------------------------------------------------ rclone / Google Drive

/// <param name="Path">Полный путь для rclone, например "gdrive:GameCloudSync/1245620 - ELDEN RING/2026-10-25_14-30-00.zip".</param>
public record RemoteFile(string Name, long Size, string Path);

public static class Drive
{
    public static string? RcloneExe
    {
        get
        {
            var local = Path.Combine(AppPaths.Dir, "rclone.exe");
            if (File.Exists(local)) return local;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    var p = Path.Combine(dir.Trim(), "rclone.exe");
                    if (File.Exists(p)) return p;
                }
                catch (ArgumentException) { }
            }
            return null;
        }
    }

    public static async Task DownloadRcloneAsync(Action<string> log)
    {
        log(L.T("rclone.downloading"));
        Directory.CreateDirectory(AppPaths.Dir);
        using var http = new HttpClient();
        var bytes = await http.GetByteArrayAsync(AppPaths.RcloneUrl);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        var entry = zip.Entries.First(e => e.FullName.EndsWith("/rclone.exe"));
        entry.ExtractToFile(Path.Combine(AppPaths.Dir, "rclone.exe"), true);
        log(L.T("rclone.installed"));
    }

    public static Task<(int code, string stdout, string stderr)> RunAsync(params string[] args) =>
        RunAsync(args, null, default);

    /// <param name="progress">Если задан, rclone пишет статистику передачи, и сюда приходит доля переданных байт.</param>
    /// <param name="ct">Отмена убивает процесс rclone.</param>
    public static async Task<(int code, string stdout, string stderr)> RunAsync(
        string[] args, Action<double>? progress, CancellationToken ct)
    {
        var exe = RcloneExe ?? throw new InvalidOperationException(L.T("error.rcloneMissing"));
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add(AppPaths.RcloneConf);
        if (progress is not null)
            foreach (var a in new[] { "-v", "--use-json-log", "--stats", "500ms" }) psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        using var kill = ct.Register(() =>
        {
            try { p.Kill(true); }
            catch (InvalidOperationException) { }  // уже завершился
        });
        var outTask = p.StandardOutput.ReadToEndAsync();
        var err = new StringBuilder();
        while (await p.StandardError.ReadLineAsync() is { } line)
        {
            err.AppendLine(line);
            if (progress is not null && TryReadStats(line) is { } fraction) progress(fraction);
        }
        await p.WaitForExitAsync();
        ct.ThrowIfCancellationRequested();
        return (p.ExitCode, await outTask, err.ToString());
    }

    /// <summary>Доля переданных байт из строки JSON-лога rclone со статистикой.</summary>
    static double? TryReadStats(string line)
    {
        if (!line.StartsWith('{') || !line.Contains("\"stats\"")) return null;
        try
        {
            var stats = JsonDocument.Parse(line).RootElement.GetProperty("stats");
            var total = stats.GetProperty("totalBytes").GetDouble();
            return total > 0 ? Math.Clamp(stats.GetProperty("bytes").GetDouble() / total, 0, 1) : null;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    static async Task<string> RunCheckedAsync(string[] args, Action<double>? progress = null, CancellationToken ct = default)
    {
        var (code, so, se) = await RunAsync(args, progress, ct);
        if (code != 0) throw new InvalidOperationException(LastError(se) ?? $"rclone exit {code}");
        return so;
    }

    /// <summary>Последняя ошибка из вывода rclone (обычного или JSON-лога).</summary>
    static string? LastError(string stderr)
    {
        foreach (var line in stderr.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Reverse())
        {
            if (!line.StartsWith('{')) return line;
            try
            {
                var root = JsonDocument.Parse(line).RootElement;
                if (root.GetProperty("level").GetString() is "error" or "critical")
                    return root.GetProperty("msg").GetString();
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException) { }
        }
        return null;
    }

    public static async Task<bool> IsConnectedAsync()
    {
        if (RcloneExe is null || !File.Exists(AppPaths.RcloneConf)) return false;
        var (_, so, _) = await RunAsync("listremotes");
        return so.Contains(AppPaths.Remote + ":");
    }

    /// <summary>Открывает браузер для входа в Google и ждёт подтверждения.</summary>
    public static Task<(int code, string stdout, string stderr)> ConnectAsync() =>
        RunAsync("config", "create", AppPaths.Remote, "drive", "scope=drive.file");

    public static Task DisconnectAsync() => RunAsync("config", "delete", AppPaths.Remote);

    static string SafeName(string s)
    {
        var n = Regex.Replace(s, "[<>:\"/\\\\|?*]+", "").Trim();
        return n.Length == 0 ? "game" : n[..Math.Min(80, n.Length)];
    }

    static string RootDir => $"{AppPaths.Remote}:{AppPaths.RemoteRoot}";

    /// <summary>
    /// Папка игры на Drive: GameCloudSync/&lt;платформа&gt;/&lt;игра&gt;.
    /// Steam-игра узнаётся по ID (он одинаковый на любом ПК), остальные — по названию.
    /// </summary>
    /// <example>gdrive:GameCloudSync/Steam/1245620, gdrive:GameCloudSync/GOG/The Witcher 3 Wild Hunt - Game of the Year Edition</example>
    public static async Task<string> RemoteDirAsync(Game g)
    {
        var platform = $"{RootDir}/{g.StoreLabel}";
        await MergeDuplicatesAsync(platform);
        return $"{platform}/{(g.Store == "steam" ? g.Id : SafeName(g.Name))}";
    }

    /// <summary>
    /// Google Drive разрешает папки с одинаковыми именами (например, если два ПК создали папку одновременно),
    /// а rclone в таком случае видит только одну из них, поэтому дубликаты сливаем.
    /// Папки пользователя с такими же именами программа не видит и не трогает (доступ drive.file).
    /// </summary>
    static async Task MergeDuplicatesAsync(string path)
    {
        var (code, so, _) = await RunAsync("lsjson", path, "--dirs-only");
        if (code != 0 || string.IsNullOrWhiteSpace(so)) return;
        var names = JsonDocument.Parse(so).RootElement.EnumerateArray()
            .Select(e => e.GetProperty("Name").GetString()!)
            .ToList();
        if (names.Count != names.Distinct().Count())
            await RunAsync("dedupe", "--dedupe-mode", "merge", path);
    }

    /// <summary>Сливает задвоившиеся корневую папку программы и папки платформ.</summary>
    public static async Task MergeDuplicateRootsAsync()
    {
        await MergeDuplicatesAsync($"{AppPaths.Remote}:");
        await MergeDuplicatesAsync(RootDir);
    }

    public static async Task<List<RemoteFile>> ListVersionsAsync(Game g)
    {
        var dir = await RemoteDirAsync(g);
        var (code, so, _) = await RunAsync("lsjson", dir, "--files-only");
        if (code != 0 || string.IsNullOrWhiteSpace(so)) return [];
        return JsonDocument.Parse(so).RootElement.EnumerateArray()
            .Select(e =>
            {
                var name = e.GetProperty("Name").GetString()!;
                return new RemoteFile(name, e.GetProperty("Size").GetInt64(), $"{dir}/{name}");
            })
            .Where(f => f.Name.EndsWith(".zip"))
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .ToList();
    }

    public static Task UploadAsync(string local, string remote, Action<double>? progress = null, CancellationToken ct = default) =>
        RunCheckedAsync(["copyto", local, remote], progress, ct);

    public static Task DownloadAsync(string remote, string local, Action<double>? progress = null, CancellationToken ct = default) =>
        RunCheckedAsync(["copyto", remote, local], progress, ct);
    public static Task DeleteAsync(string remote) => RunAsync("deletefile", remote);
}

public static class Fmt
{
    public static string Size(double n)
    {
        foreach (var u in new[] { "unit.b", "unit.kb", "unit.mb", "unit.gb" })
        {
            if (n < 1024) return string.Format(L.Culture, "{0:0} {1}", n, L.T(u));
            n /= 1024;
        }
        return string.Format(L.Culture, "{0:0.0} {1}", n, L.T("unit.tb"));
    }
}

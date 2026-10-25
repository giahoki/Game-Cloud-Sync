using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace GameCloudSync.Core;

/// <summary>Версия программы и проверка новых релизов на GitHub (по тегу последнего релиза).</summary>
public static class Updater
{
    const string Repo = "giahoki/Game-Cloud-Sync";
    public const string ReleasesUrl = $"https://github.com/{Repo}/releases";

    /// <summary>Версия из &lt;Version&gt; в GameCloudSync.csproj, например «1.0».</summary>
    public static string Current { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0";

    /// <summary>Последний релиз, если он новее текущей версии, иначе null.</summary>
    public static async Task<(string Version, string Url)?> CheckAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"GameCloudSync/{Current}");  // без User-Agent GitHub API отвечает 403
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        using var resp = await http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        if (!resp.IsSuccessStatusCode) return null;  // 404 — релизов ещё нет
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        var url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() : null;

        return IsNewer(tag, Current) ? (tag.TrimStart('v', 'V'), url ?? ReleasesUrl) : null;
    }

    /// <summary>Сравнивает теги вида «v1.2» и «1.2.0»: недостающие части считаются нулями.</summary>
    public static bool IsNewer(string tag, string current) => Parse(tag) > Parse(current);

    static Version Parse(string s)
    {
        var parts = s.Trim().TrimStart('v', 'V').Split('-', '+')[0].Split('.')
            .Select(p => int.TryParse(p, out var n) ? n : 0)
            .Concat([0, 0, 0, 0]).Take(4).ToArray();
        return new Version(parts[0], parts[1], parts[2], parts[3]);
    }
}

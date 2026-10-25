using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace GameCloudSync.Core;

/// <summary>
/// Локализация интерфейса. Строки лежат в Strings/&lt;код&gt;.json (встроены в exe),
/// язык по умолчанию определяется по языку Windows, недостающие ключи берутся из en.json.
/// </summary>
public static class L
{
    public static readonly (string Code, string Name)[] Languages =
    [
        ("en", "English"),
        ("ru", "Русский"),
        ("uk", "Українська"),
        ("de", "Deutsch"),
        ("es", "Español"),
        ("fr", "Français"),
        ("pl", "Polski"),
        ("zh", "中文（简体）"),
    ];

    static Dictionary<string, string> _en = [];
    static Dictionary<string, string> _cur = [];

    public static string Code { get; private set; } = "en";
    public static CultureInfo Culture { get; private set; } = CultureInfo.CurrentUICulture;

    /// <param name="setting">Код языка из настроек или пустая строка для автоопределения.</param>
    public static void Init(string? setting)
    {
        Code = Languages.Any(l => l.Code == setting) ? setting! : Detect();
        _en = Load("en");
        _cur = Code == "en" ? _en : Load(Code);
        Culture = Code == Detect() ? CultureInfo.CurrentUICulture : CultureInfo.GetCultureInfo(Code == "zh" ? "zh-Hans" : Code);
    }

    public static string Detect()
    {
        // языка Windows нет среди переводов — английский
        var iso = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return Languages.Any(l => l.Code == iso) ? iso : "en";
    }

    public static string NameOf(string code) => Languages.FirstOrDefault(l => l.Code == code).Name ?? code;

    public static string T(string key) => _cur.GetValueOrDefault(key) ?? _en.GetValueOrDefault(key) ?? key;

    public static string F(string key, params object?[] args) => string.Format(Culture, T(key), args);

    static Dictionary<string, string> Load(string code)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Strings.{code}.json");
        return s is null ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? [];
    }
}

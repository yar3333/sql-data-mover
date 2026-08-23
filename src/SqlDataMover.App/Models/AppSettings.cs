namespace SqlDataMover.App.Models;

/// <summary>Настройки приложения, сохраняемые между запусками.</summary>
public sealed class AppSettings
{
    public const int MaxHistory = 10;

    /// <summary>История успешных подключений источника, свежие сверху.</summary>
    public List<string> SourceConnectionStrings { get; set; } = [];

    /// <summary>История успешных подключений приёмника, свежие сверху.</summary>
    public List<string> TargetConnectionStrings { get; set; } = [];

    /// <summary>Язык интерфейса ("en" или "ru"); если не задан — определяется по языку ОС при первом запуске.</summary>
    public string? Language { get; set; }

    /// <summary>
    /// Выбранные на шаге таблиц таблицы по строке подключения источника:
    /// ключ — точная строка подключения, значение — имена таблиц в формате "schema.table".
    /// Используется как выбор по умолчанию при повторном подключении к тому же источнику.
    /// </summary>
    public Dictionary<string, List<string>> SelectedTablesBySource { get; set; } = [];
}

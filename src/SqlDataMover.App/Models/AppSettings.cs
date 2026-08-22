namespace SqlDataMover.App.Models;

/// <summary>Настройки приложения, сохраняемые между запусками.</summary>
public sealed class AppSettings
{
    public const int MaxHistory = 10;

    /// <summary>История успешных подключений источника, свежие сверху.</summary>
    public List<string> SourceConnectionStrings { get; set; } = [];

    /// <summary>История успешных подключений приёмника, свежие сверху.</summary>
    public List<string> TargetConnectionStrings { get; set; } = [];
}

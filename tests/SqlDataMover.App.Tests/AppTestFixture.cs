using SqlDataMover.App.Localization;
using SqlDataMover.App.Models;

namespace SqlDataMover.App.Tests;

/// <summary>
/// Одна коллекция для всех тестов приложения: изолирует путь к файлу настроек
/// (тесты не должны трогать реальный %APPDATA%) и не даёт тест-классам
/// параллелиться между собой, т.к. реестр провайдеров — глобальное состояние.
/// </summary>
[CollectionDefinition("app")]
public sealed class AppTestCollection : ICollectionFixture<AppTestFixture>;

public sealed class AppTestFixture : IDisposable
{
    private readonly string _backupPath = AppSettingsStore.FilePath;
    private readonly string _tempPath;

    public AppTestFixture()
    {
        _tempPath = Path.Combine(
            Path.GetTempPath(),
            "SqlDataMoverTests",
            $"{Guid.NewGuid():N}.json"
        );
        AppSettingsStore.FilePath = _tempPath;
        // Детерминированные тесты: английский язык по умолчанию, независимо от языка ОС.
        AppStrings.Current.Language = "en";
    }

    public void Dispose()
    {
        AppSettingsStore.FilePath = _backupPath;
        try
        {
            File.Delete(_tempPath);
        }
        catch
        {
            // файл мог не создаться — не важно
        }
    }
}

using System.Globalization;
using SqlDataMover.App.Localization;
using SqlDataMover.App.Models;
using SqlDataMover.App.ViewModels;
using SqlDataMover.Core.Localization;

namespace SqlDataMover.App.Tests;

/// <summary>
/// Выбор языка: детект при первом запуске по языку ОС и переключение языка
/// с обновлением строк и сохранением настройки.
/// </summary>
[Collection("app")]
public class LanguageTests
{
    [Theory]
    [InlineData("ru-RU", "ru")]
    [InlineData("ru", "ru")]
    [InlineData("en-US", "en")]
    [InlineData("de-DE", "en")]
    public void Detect_initial_language_follows_os_ui_language(string osCulture, string expected)
    {
        Assert.Equal(expected, AppStrings.DetectInitialLanguage(new CultureInfo(osCulture)));
    }

    [Fact]
    public void Switching_language_updates_strings_core_and_settings()
    {
        AppStrings.Current.Language = "en";
        try
        {
            var vm = new MainViewModel();
            Assert.Equal("en", vm.SelectedLanguage?.Code);

            var russian = vm.Languages.Single(l => l.Code == "ru");
            vm.SelectedLanguage = russian;

            Assert.Equal("ru", AppStrings.Current.Language);
            Assert.Equal("ru", CoreStrings.Language);
            Assert.Equal("Копирование данных между серверами", AppStrings.Current.Subtitle);
            Assert.Equal("✓ подключено", AppStrings.Current.Connected);
            Assert.Equal(
                "ru",
                AppSettingsStore.Load().Language,
                StringComparer.OrdinalIgnoreCase
            );

            // Возвращаем английский, чтобы не влиять на остальные тесты коллекции.
            vm.SelectedLanguage = vm.Languages.Single(l => l.Code == "en");
            Assert.Equal("en", AppStrings.Current.Language);
            Assert.Equal("Copy data between SQL servers", AppStrings.Current.Subtitle);
            Assert.Equal(
                "en",
                AppSettingsStore.Load().Language,
                StringComparer.OrdinalIgnoreCase
            );
        }
        finally
        {
            AppStrings.Current.Language = "en";
        }
    }
}

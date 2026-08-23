using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SqlDataMover.App.Localization;
using SqlDataMover.App.Models;
using SqlDataMover.App.ViewModels;
using SqlDataMover.App.Views;

namespace SqlDataMover.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            ApplyInitialLanguage();

            var viewModel = new MainViewModel();
            var window = new MainWindow { DataContext = viewModel };
            window.Closed += async (_, _) => await viewModel.CleanupAsync();
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Первичный выбор языка: сохранённая настройка, иначе — язык интерфейса ОС
    /// (русский только если ОС на русском). Выбор запоминается, чтобы не зависеть
    /// от смены языка ОС в будущем.
    /// </summary>
    private static void ApplyInitialLanguage()
    {
        var settings = AppSettingsStore.Load();
        var language = settings.Language;
        if (string.IsNullOrEmpty(language))
        {
            language = AppStrings.DetectInitialLanguage();
            settings.Language = language;
            AppSettingsStore.Save(settings);
        }

        AppStrings.Current.Language = language;
    }
}

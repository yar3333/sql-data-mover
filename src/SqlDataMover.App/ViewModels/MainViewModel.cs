using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlDataMover.App.Localization;
using SqlDataMover.App.Models;

namespace SqlDataMover.App.ViewModels;

/// <summary>Корневая ViewModel мастера: навигация по шагам и оркестрация.</summary>
public partial class MainViewModel : ViewModelBase
{
    public ConnectionPageViewModel Connection { get; }
    public TablesPageViewModel Tables { get; }
    public MappingPageViewModel Mapping { get; }
    public PreviewPageViewModel Preview { get; }

    public IReadOnlyList<LanguageOption> Languages { get; } = AppStrings.Languages;

    [ObservableProperty]
    public partial LanguageOption? SelectedLanguage { get; set; }

    [ObservableProperty]
    public partial int CurrentStep { get; set; }

    public object CurrentPage =>
        CurrentStep switch
        {
            0 => Connection,
            1 => Tables,
            2 => Mapping,
            _ => Preview,
        };

    public bool IsStep0Active => CurrentStep == 0;
    public bool IsStep1Active => CurrentStep == 1;
    public bool IsStep2Active => CurrentStep == 2;
    public bool IsStep3Active => CurrentStep == 3;

    /// <summary>
    /// Идёт ли длительная операция (подключение, загрузка колонок, предпросмотр,
    /// копирование). На время таких операций навигация (вперёд/назад/заново) блокируется.
    /// </summary>
    public bool IsAnyBusy =>
        Connection.IsBusy || Tables.IsBusy || Mapping.IsBusy || Preview.IsBusy || Preview.IsCopying;

    public bool CanGoNext =>
        !IsAnyBusy
        && CurrentStep switch
        {
            0 => Connection.IsReady,
            1 => Tables.SelectedCount > 0,
            2 => Mapping.AllHaveMatchColumns,
            _ => false,
        };

    public bool CanGoBack => CurrentStep > 0 && !IsAnyBusy;

    /// <summary>«Заново» нельзя нажимать во время операции: сброс задиспозит провайдеры под ней.</summary>
    public bool CanRestart => !IsAnyBusy;

    public MainViewModel()
    {
        SelectedLanguage = Languages.FirstOrDefault(l => l.Code == AppStrings.Current.Language);

        Connection = new ConnectionPageViewModel();
        Tables = new TablesPageViewModel();
        Mapping = new MappingPageViewModel();
        Preview = new PreviewPageViewModel();

        Connection.PropertyChanged += (_, e) =>
        {
            if (
                e.PropertyName
                is nameof(ConnectionPageViewModel.IsReady)
                    or nameof(ConnectionPageViewModel.IsBusy)
            )
                NotifyNavigationState();
        };
        Connection.TablesLoaded += tables =>
        {
            // Выбор по умолчанию из прошлого запуска: таблицы, сохранённые для этого
            // же источника. Пользователь на шаге таблиц может поменять выбор.
            var settings = AppSettingsStore.Load();
            var saved = settings.SelectedTablesBySource.GetValueOrDefault(
                Connection.SourceConnectionString
            );
            Tables.LoadTables(tables, saved);
        };
        Tables.PropertyChanged += (_, e) =>
        {
            if (
                e.PropertyName
                is nameof(TablesPageViewModel.SelectedCount)
                    or nameof(TablesPageViewModel.IsBusy)
            )
                NotifyNavigationState();
        };
        Mapping.PropertyChanged += (_, e) =>
        {
            if (
                e.PropertyName
                is nameof(MappingPageViewModel.IsBusy)
                    or nameof(MappingPageViewModel.AllHaveMatchColumns)
            )
                NotifyNavigationState();
        };
        Preview.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PreviewPageViewModel.IsBusy))
                NotifyNavigationState();
        };
    }

    partial void OnCurrentStepChanged(int value)
    {
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(IsStep0Active));
        OnPropertyChanged(nameof(IsStep1Active));
        OnPropertyChanged(nameof(IsStep2Active));
        OnPropertyChanged(nameof(IsStep3Active));
        NotifyNavigationState();
    }

    /// <summary>Смена языка в шапке: применяем ко всему приложению и запоминаем выбор.</summary>
    partial void OnSelectedLanguageChanged(LanguageOption? value)
    {
        if (value is null || value.Code == AppStrings.Current.Language)
            return;

        AppStrings.Current.Language = value.Code;

        var settings = AppSettingsStore.Load();
        settings.Language = value.Code;
        AppSettingsStore.Save(settings);
    }

    /// <summary>
    /// Запоминает выбранные таблицы для текущего источника: при следующем подключении
    /// к той же строке подключения галочки будут расставлены по умолчанию.
    /// </summary>
    private void SaveSelectedTables()
    {
        var settings = AppSettingsStore.Load();
        settings.SelectedTablesBySource[Connection.SourceConnectionString] = Tables
            .GetSelectedTables()
            .Select(t => t.Table.ToString())
            .ToList();
        AppSettingsStore.Save(settings);
    }

    /// <summary>
    /// Запоминает поля сопоставления выбранных таблиц для текущего источника. Поля таблиц,
    /// не попавших в текущий прогон, сохраняются: если пользователь выберет их позже,
    /// конфигурация не потеряется.
    /// </summary>
    private void SaveMatchColumns()
    {
        var settings = AppSettingsStore.Load();
        if (
            !settings.MatchColumnsBySource.TryGetValue(
                Connection.SourceConnectionString,
                out var byTable
            )
        )
        {
            byTable = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            settings.MatchColumnsBySource[Connection.SourceConnectionString] = byTable;
        }

        foreach (var cfg in Mapping.GetConfigs())
            byTable[cfg.Table.ToString()] = cfg.MatchColumns.ToList();

        AppSettingsStore.Save(settings);
    }

    /// <summary>
    /// Уведомляет о состоянии навигации: кнопки «Далее/Назад» привязаны к
    /// <see cref="CanGoNext"/> и <see cref="CanGoBack"/>, поэтому вместе с
    /// CanExecuteChanged нужно поднимать и PropertyChanged самих свойств.
    /// </summary>
    private void NotifyNavigationState()
    {
        OnPropertyChanged(nameof(IsAnyBusy));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanRestart));
        GoNextCommand.NotifyCanExecuteChanged();
        GoBackCommand.NotifyCanExecuteChanged();
        RestartCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task GoNextAsync()
    {
        var fromStep = CurrentStep;

        if (fromStep == 1)
        {
            SaveSelectedTables();
            var configs = Tables.GetSelectedTables();
            var savedColumns = AppSettingsStore
                .Load()
                .MatchColumnsBySource.GetValueOrDefault(Connection.SourceConnectionString);
            await Mapping.InitializeAsync(configs, Connection.Source!, savedColumns);
        }
        else if (fromStep == 2)
        {
            SaveMatchColumns();
            // Переходим на шаг предпросмотра сразу, чтобы показать индикатор выполнения dry run.
            CurrentStep++;
            var configs = Mapping.GetConfigs();
            await Preview.RunPreviewAsync(configs, Connection.Source!, Connection.Target!);
            return;
        }

        // Пока выполнялась асинхронная работа, пользователь мог уйти назад —
        // не перепрыгиваем его на шаг вперёд.
        if (CurrentStep != fromStep)
            return;

        CurrentStep++;
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack() => CurrentStep--;

    [RelayCommand(CanExecute = nameof(CanRestart))]
    private async Task RestartAsync()
    {
        await Connection.CleanupAsync();
        Tables.ClearTables();
        Mapping.Reset();
        Preview.Reset();
        Connection.ResetStatus();
        CurrentStep = 0;
    }

    public async Task CleanupAsync() => await Connection.CleanupAsync();
}

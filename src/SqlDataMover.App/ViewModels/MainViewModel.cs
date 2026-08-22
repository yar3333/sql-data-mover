using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SqlDataMover.App.ViewModels;

/// <summary>Корневая ViewModel мастера: навигация по шагам и оркестрация.</summary>
public partial class MainViewModel : ViewModelBase
{
    public ConnectionPageViewModel Connection { get; }
    public TablesPageViewModel Tables { get; }
    public MappingPageViewModel Mapping { get; }
    public PreviewPageViewModel Preview { get; }

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

    public bool CanGoNext =>
        CurrentStep switch
        {
            0 => Connection.IsReady && !Connection.IsBusy,
            1 => Tables.SelectedCount > 0 && !Tables.IsBusy,
            2 => Mapping.AllHaveMatchColumns && !Mapping.IsBusy && !Preview.IsBusy,
            _ => false,
        };

    public bool CanGoBack => CurrentStep > 0;

    public MainViewModel()
    {
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
        Connection.TablesLoaded += tables => Tables.LoadTables(tables);
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

    /// <summary>
    /// Уведомляет о состоянии навигации: кнопки «Далее/Назад» привязаны к
    /// <see cref="CanGoNext"/> и <see cref="CanGoBack"/>, поэтому вместе с
    /// CanExecuteChanged нужно поднимать и PropertyChanged самих свойств.
    /// </summary>
    private void NotifyNavigationState()
    {
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoBack));
        GoNextCommand.NotifyCanExecuteChanged();
        GoBackCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task GoNextAsync()
    {
        if (CurrentStep == 1)
        {
            var configs = Tables.GetSelectedTables();
            await Mapping.InitializeAsync(configs, Connection.Source!);
        }
        else if (CurrentStep == 2)
        {
            // Переходим на шаг предпросмотра сразу, чтобы показать индикатор выполнения dry run.
            CurrentStep++;
            var configs = Mapping.GetConfigs();
            await Preview.RunPreviewAsync(configs, Connection.Source!, Connection.Target!);
            return;
        }

        CurrentStep++;
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack() => CurrentStep--;

    [RelayCommand]
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

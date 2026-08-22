using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SqlDataMover.App.ViewModels;

/// <summary>Корневая ViewModel мастера: навигация по шагам и оркестрация.</summary>
public partial class MainViewModel : ViewModelBase
{
    public ConnectionPageViewModel Connection { get; }
    public TablesPageViewModel Tables { get; }
    public MappingPageViewModel Mapping { get; }

    [ObservableProperty]
    public partial int CurrentStep { get; set; }

    public object CurrentPage =>
        CurrentStep switch
        {
            0 => Connection,
            1 => Tables,
            _ => Mapping,
        };

    public bool IsStep0Active => CurrentStep == 0;
    public bool IsStep1Active => CurrentStep == 1;
    public bool IsStep2Active => CurrentStep == 2;

    public bool CanGoNext =>
        CurrentStep switch
        {
            0 => Connection.IsReady && !Connection.IsBusy,
            1 => Tables.SelectedCount > 0 && !Tables.IsBusy,
            _ => false,
        };

    public bool CanGoBack => CurrentStep > 0;

    public MainViewModel()
    {
        Connection = new ConnectionPageViewModel();
        Tables = new TablesPageViewModel();
        Mapping = new MappingPageViewModel();

        Connection.PropertyChanged += (_, e) =>
        {
            if (
                e.PropertyName
                is nameof(ConnectionPageViewModel.IsReady)
                    or nameof(ConnectionPageViewModel.IsBusy)
            )
                GoNextCommand.NotifyCanExecuteChanged();
        };
        Connection.TablesLoaded += tables => Tables.LoadTables(tables);
        Tables.PropertyChanged += (_, e) =>
        {
            if (
                e.PropertyName
                is nameof(TablesPageViewModel.SelectedCount)
                    or nameof(TablesPageViewModel.IsBusy)
            )
                GoNextCommand.NotifyCanExecuteChanged();
        };
    }

    partial void OnCurrentStepChanged(int value)
    {
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(IsStep0Active));
        OnPropertyChanged(nameof(IsStep1Active));
        OnPropertyChanged(nameof(IsStep2Active));
        GoNextCommand.NotifyCanExecuteChanged();
        GoBackCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task GoNextAsync()
    {
        if (CurrentStep == 1)
        {
            var configs = Tables.GetSelectedTables();
            await Mapping.InitializeAsync(configs, Connection.Source!, Connection.Target!);
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
        Connection.ResetStatus();
        CurrentStep = 0;
    }

    public async Task CleanupAsync() => await Connection.CleanupAsync();
}

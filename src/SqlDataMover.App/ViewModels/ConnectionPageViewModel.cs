using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlDataMover.Core.Abstractions;
using SqlDataMover.Core.Models;

namespace SqlDataMover.App.ViewModels;

/// <summary>Шаг 1: тип СУБД и строки подключения. Создаёт и подключает провайдеры.</summary>
public partial class ConnectionPageViewModel : ViewModelBase
{
    public ObservableCollection<ProviderDescriptor> Providers { get; } = new(DbProviderFactory.SupportedProviders);

    [ObservableProperty] public partial ProviderDescriptor? SelectedProvider { get; set; }
    [ObservableProperty] public partial string SourceConnectionString { get; set; } = "";
    [ObservableProperty] public partial string TargetConnectionString { get; set; } = "";
    [ObservableProperty] public partial string? SourceStatus { get; set; }
    [ObservableProperty] public partial string? TargetStatus { get; set; }
    [ObservableProperty] public partial string? StatusMessage { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsReady { get; set; }

    public IDbProvider? Source { get; private set; }
    public IDbProvider? Target { get; private set; }

    /// <summary>Список таблиц источника после успешного подключения.</summary>
    public event Action<IReadOnlyList<DbTable>>? TablesLoaded;

    public ConnectionPageViewModel()
    {
        SelectedProvider = Providers.FirstOrDefault();
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        IsReady = false;
        SourceStatus = null;
        TargetStatus = null;
        StatusMessage = "Подключение к серверам...";

        await CleanupAsync();

        try
        {
            var key = SelectedProvider?.Key ?? throw new InvalidOperationException("Выберите тип СУБД.");
            Source = DbProviderFactory.Create(key, SourceConnectionString);
            Target = DbProviderFactory.Create(key, TargetConnectionString);

            await Source.ConnectAsync();
            SourceStatus = "✓ подключено";
            await Target.ConnectAsync();
            TargetStatus = "✓ подключено";

            var tables = await Source.GetTablesAsync();
            StatusMessage = null;
            TablesLoaded?.Invoke(tables);
            IsReady = true;
        }
        catch (Exception ex)
        {
            SourceStatus = null;
            TargetStatus = null;
            StatusMessage = $"Ошибка подключения: {ex.Message}";
            await CleanupAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void ResetStatus()
    {
        IsReady = false;
        SourceStatus = null;
        TargetStatus = null;
        StatusMessage = null;
    }

    public async Task CleanupAsync()
    {
        if (Source is not null) { await Source.DisposeAsync(); Source = null; }
        if (Target is not null) { await Target.DisposeAsync(); Target = null; }
    }
}

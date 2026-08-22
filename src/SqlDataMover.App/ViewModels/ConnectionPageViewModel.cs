using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlDataMover.App.Models;
using SqlDataMover.Core.Abstractions;
using SqlDataMover.Core.Models;

namespace SqlDataMover.App.ViewModels;

/// <summary>Шаг 1: тип СУБД и строки подключения. Создаёт и подключает провайдеры.</summary>
public partial class ConnectionPageViewModel : ViewModelBase
{
    public ObservableCollection<ProviderDescriptor> Providers { get; } =
        new(DbProviderFactory.SupportedProviders);

    /// <summary>История успешных подключений (свежие сверху) для выбора в ComboBox.</summary>
    public ObservableCollection<ConnectionHistoryEntry> SourceHistory { get; } = new();
    public ObservableCollection<ConnectionHistoryEntry> TargetHistory { get; } = new();

    [ObservableProperty]
    public partial ProviderDescriptor? SelectedProvider { get; set; }

    [ObservableProperty]
    public partial string SourceConnectionString { get; set; } = "";

    [ObservableProperty]
    public partial string TargetConnectionString { get; set; } = "";

    [ObservableProperty]
    public partial ConnectionHistoryEntry? SourceSelectedEntry { get; set; }

    [ObservableProperty]
    public partial ConnectionHistoryEntry? TargetSelectedEntry { get; set; }

    [ObservableProperty]
    public partial string? SourceStatus { get; set; }

    [ObservableProperty]
    public partial string? TargetStatus { get; set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsReady { get; set; }

    public IDbProvider? Source { get; private set; }
    public IDbProvider? Target { get; private set; }

    /// <summary>Список таблиц источника после успешного подключения.</summary>
    public event Action<IReadOnlyList<DbTable>>? TablesLoaded;

    public ConnectionPageViewModel()
    {
        SelectedProvider = Providers.FirstOrDefault();

        var settings = AppSettingsStore.Load();
        foreach (var connectionString in settings.SourceConnectionStrings)
            SourceHistory.Add(new ConnectionHistoryEntry(connectionString));
        foreach (var connectionString in settings.TargetConnectionStrings)
            TargetHistory.Add(new ConnectionHistoryEntry(connectionString));
    }

    partial void OnSourceSelectedEntryChanged(ConnectionHistoryEntry? value)
    {
        if (value is not null)
            SourceConnectionString = value.ConnectionString;
    }

    partial void OnTargetSelectedEntryChanged(ConnectionHistoryEntry? value)
    {
        if (value is not null)
            TargetConnectionString = value.ConnectionString;
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        IsReady = false;
        SourceStatus = null;
        TargetStatus = null;
        StatusMessage = "Подключение к серверам...";

        await CleanupAsync();

        try
        {
            var key =
                SelectedProvider?.Key ?? throw new InvalidOperationException("Выберите тип СУБД.");
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

            AddToHistory(SourceHistory, SourceConnectionString);
            AddToHistory(TargetHistory, TargetConnectionString);
            SaveSettings();
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

    /// <summary>Добавляет строку подключения в историю: свежие сверху, без дубликатов, не более MaxHistory.</summary>
    private static void AddToHistory(
        ObservableCollection<ConnectionHistoryEntry> history,
        string connectionString
    )
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        var existing = history.FirstOrDefault(e => e.ConnectionString == connectionString);
        if (existing is not null)
            history.Remove(existing);

        history.Insert(0, new ConnectionHistoryEntry(connectionString));

        while (history.Count > AppSettings.MaxHistory)
            history.RemoveAt(history.Count - 1);
    }

    private void SaveSettings() =>
        AppSettingsStore.Save(
            new AppSettings
            {
                SourceConnectionStrings = SourceHistory.Select(e => e.ConnectionString).ToList(),
                TargetConnectionStrings = TargetHistory.Select(e => e.ConnectionString).ToList(),
            }
        );

    public async Task CleanupAsync()
    {
        if (Source is not null)
        {
            await Source.DisposeAsync();
            Source = null;
        }
        if (Target is not null)
        {
            await Target.DisposeAsync();
            Target = null;
        }
    }
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlDataMover.App.Localization;
using SqlDataMover.App.Models;
using SqlDataMover.Core;
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
        // Обходим в обратном порядке: свежие записи в файле идут первыми, а AddToHistory
        // кладёт новые сверху и перезаписывает дубликаты по «сервер / БД» — в итоге
        // сохраняется порядок «свежие сверху» и список остаётся уникальным.
        foreach (var connectionString in settings.SourceConnectionStrings.AsEnumerable().Reverse())
            AddToHistory(SourceHistory, connectionString);
        foreach (var connectionString in settings.TargetConnectionStrings.AsEnumerable().Reverse())
            AddToHistory(TargetHistory, connectionString);
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
        StatusMessage = AppStrings.Current.Connecting;

        await CleanupAsync();

        try
        {
            var key =
                SelectedProvider?.Key
                ?? throw new InvalidOperationException(AppStrings.Current.SelectProviderError);
            Source = DbProviderFactory.Create(key, SourceConnectionString);
            Target = DbProviderFactory.Create(key, TargetConnectionString);

            await Source.ConnectAsync();
            SourceStatus = AppStrings.Current.Connected;
            await Target.ConnectAsync();
            TargetStatus = AppStrings.Current.Connected;

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
            StatusMessage = AppStrings.Current.FormatConnectionError(ex.Message);
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

    /// <summary>
    /// Добавляет строку подключения в историю: свежие сверху, не более MaxHistory.
    /// Запись с тем же отображаемым именем «сервер / БД» перезаписывается новой,
    /// чтобы в списке не было неразличимых дубликатов.
    /// </summary>
    private static void AddToHistory(
        ObservableCollection<ConnectionHistoryEntry> history,
        string connectionString
    )
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        var displayName = ConnectionStringFormatter.FormatDisplay(connectionString);
        var existing = history.FirstOrDefault(e => e.DisplayName == displayName);
        if (existing is not null)
            history.Remove(existing);

        history.Insert(0, new ConnectionHistoryEntry(connectionString));

        while (history.Count > AppSettings.MaxHistory)
            history.RemoveAt(history.Count - 1);
    }

    private void SaveSettings()
    {
        // Обновляем только историю подключений: остальные настройки (язык, выбранные
        // таблицы) должны пережить сохранение, поэтому не заменяем объект целиком.
        var settings = AppSettingsStore.Load();
        settings.SourceConnectionStrings = SourceHistory.Select(e => e.ConnectionString).ToList();
        settings.TargetConnectionStrings = TargetHistory.Select(e => e.ConnectionString).ToList();
        AppSettingsStore.Save(settings);
    }

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

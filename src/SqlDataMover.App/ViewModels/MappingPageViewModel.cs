using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlDataMover.Core.Abstractions;
using SqlDataMover.Core.Copy;
using SqlDataMover.Core.Models;

namespace SqlDataMover.App.ViewModels;

/// <summary>Строка списка на шаге 3: таблица и выбранное уникальное поле.</summary>
public partial class TableMappingViewModel : ViewModelBase
{
    public DbObjectName Table { get; }
    public string Display => Table.ToString();
    public IReadOnlyList<string> Columns { get; }

    [ObservableProperty] public partial string SelectedColumn { get; set; }

    public TableMappingViewModel(DbObjectName table, IReadOnlyList<string> columns)
    {
        Table = table;
        Columns = columns;
        SelectedColumn = columns.FirstOrDefault() ?? "";
    }
}

/// <summary>Шаг 3: выбор уникального поля для каждой таблицы и запуск копирования.</summary>
public partial class MappingPageViewModel : ViewModelBase
{
    private IDbProvider? _source;
    private IDbProvider? _target;
    private CancellationTokenSource? _cts;

    public ObservableCollection<TableMappingViewModel> Tables { get; } = [];
    public ObservableCollection<string> Log { get; } = [];

    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsCopying { get; set; }
    [ObservableProperty] public partial string CurrentTable { get; set; } = "";
    [ObservableProperty] public partial long ProcessedRows { get; set; }
    [ObservableProperty] public partial string? StatusText { get; set; }
    [ObservableProperty] public partial string? SummaryText { get; set; }
    [ObservableProperty] public partial bool CanCancel { get; set; }

    /// <summary>Событие для автопрокрутки журнала в представлении.</summary>
    public event EventHandler? LogAppended;

    public async Task InitializeAsync(IReadOnlyList<TableCopyConfig> configs, IDbProvider source, IDbProvider target)
    {
        _source = source;
        _target = target;
        IsBusy = true;
        StatusText = "Загрузка колонок таблиц...";

        try
        {
            Tables.Clear();

            foreach (var cfg in configs)
            {
                var details = await source.GetTableDetailsAsync(cfg.Table);
                var columns = details.Columns.Where(c => !c.IsComputed).Select(c => c.Name).ToList();
                var vm = new TableMappingViewModel(cfg.Table, columns);
                if (details.PrimaryKeyColumns.Count == 1)
                    vm.SelectedColumn = details.PrimaryKeyColumns[0];
                Tables.Add(vm);
            }

            StatusText = null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Reset()
    {
        Tables.Clear();
        Log.Clear();
        SummaryText = null;
        StatusText = null;
        CurrentTable = "";
        ProcessedRows = 0;
    }

    [RelayCommand]
    private async Task StartCopyAsync()
    {
        if (_source is null || _target is null || IsCopying) return;

        var configs = Tables
            .Select(t => new TableCopyConfig { Table = t.Table, UniqueColumn = t.SelectedColumn?.Trim() ?? "" })
            .ToList();

        if (configs.Any(c => string.IsNullOrEmpty(c.UniqueColumn)))
        {
            StatusText = "Укажите уникальное поле для каждой таблицы.";
            return;
        }

        Log.Clear();
        SummaryText = null;
        CurrentTable = "";
        ProcessedRows = 0;
        _cts = new CancellationTokenSource();
        IsCopying = true;
        CanCancel = true;

        try
        {
            var progress = new Progress<CopyProgress>(OnCopyProgress);
            var engine = new DataCopyEngine(_source, _target, new CopySettings { BatchSize = 500 }, progress);
            var result = await engine.CopyAsync(configs, _cts.Token);

            var totalInserted = result.Tables.Sum(t => t.Inserted);
            var totalUpdated = result.Tables.Sum(t => t.Updated);
            var failed = result.Tables.Count(t => !t.Success);

            SummaryText = failed == 0
                ? $"Копирование завершено за {result.Elapsed.TotalSeconds:F1} с. Таблиц: {result.Tables.Count}. Вставлено строк: {totalInserted:N0}, обновлено: {totalUpdated:N0}."
                : $"Копирование завершено с ошибками ({failed} из {result.Tables.Count} таблиц). Вставлено строк: {totalInserted:N0}, обновлено: {totalUpdated:N0}. Подробности — в журнале.";
        }
        catch (OperationCanceledException)
        {
            SummaryText = "Копирование отменено пользователем.";
        }
        catch (Exception ex)
        {
            SummaryText = $"Ошибка копирования: {ex.Message}";
        }
        finally
        {
            IsCopying = false;
            CanCancel = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void CancelCopy() => _cts?.Cancel();

    partial void OnCanCancelChanged(bool value) => CancelCopyCommand.NotifyCanExecuteChanged();

    private void OnCopyProgress(CopyProgress progress)
    {
        CurrentTable = progress.Stage is CopyStage.Completed or CopyStage.Failed ? "" : progress.Table.ToString();
        ProcessedRows = CurrentTable.Length > 0 ? progress.ProcessedRows : 0;
        AddLog(progress.Message, progress.IsError);
    }

    private void AddLog(string message, bool isError = false)
    {
        var prefix = isError ? "✗ " : "";
        Log.Add($"{prefix}[{DateTime.Now:HH:mm:ss}] {message}");
        if (Log.Count > 2000)
            Log.RemoveAt(0);
        LogAppended?.Invoke(this, EventArgs.Empty);
    }
}

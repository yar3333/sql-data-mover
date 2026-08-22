using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlDataMover.Core.Abstractions;
using SqlDataMover.Core.Copy;
using SqlDataMover.Core.Models;

namespace SqlDataMover.App.ViewModels;

/// <summary>Строка статистики предпросмотра по одной таблице.</summary>
public sealed class PreviewTableRow
{
    public required DbObjectName Table { get; init; }
    public string Display => Table.ToString();
    public long RowsRead { get; init; }
    public long Inserted { get; init; }
    public long Updated { get; init; }
    public string? Error { get; init; }
    public bool HasError => Error is not null;
}

/// <summary>Шаг 4: предварительный прогон (без записи) и запуск копирования.</summary>
public partial class PreviewPageViewModel : ViewModelBase
{
    private IDbProvider? _source;
    private IDbProvider? _target;
    private IReadOnlyList<TableCopyConfig>? _configs;
    private CancellationTokenSource? _cts;

    public ObservableCollection<PreviewTableRow> Tables { get; } = [];
    public ObservableCollection<string> Log { get; } = [];

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsCopying { get; set; }

    [ObservableProperty]
    public partial bool CanCancel { get; set; }

    [ObservableProperty]
    public partial bool HasPreview { get; set; }

    [ObservableProperty]
    public partial bool CanStartCopy { get; set; } = true;

    [ObservableProperty]
    public partial string CurrentTable { get; set; } = "";

    [ObservableProperty]
    public partial long ProcessedRows { get; set; }

    [ObservableProperty]
    public partial string? StatusText { get; set; }

    /// <summary>Итог предварительного прогона.</summary>
    [ObservableProperty]
    public partial string? PreviewSummary { get; set; }

    /// <summary>Итог реального копирования.</summary>
    [ObservableProperty]
    public partial string? SummaryText { get; set; }

    /// <summary>Событие для автопрокрутки журнала в представлении.</summary>
    public event EventHandler? LogAppended;

    /// <summary>Выполняет предварительный прогон (dry run) и заполняет статистику по таблицам.</summary>
    public async Task RunPreviewAsync(
        IReadOnlyList<TableCopyConfig> configs,
        IDbProvider source,
        IDbProvider target
    )
    {
        _source = source;
        _target = target;
        _configs = configs;

        Tables.Clear();
        Log.Clear();
        PreviewSummary = null;
        SummaryText = null;
        StatusText = "Выполняется предварительный прогон (без записи)...";
        CurrentTable = "";
        ProcessedRows = 0;
        HasPreview = false;
        _cts = new CancellationTokenSource();
        IsBusy = true;
        CanCancel = true;

        try
        {
            var progress = new Progress<CopyProgress>(OnCopyProgress);
            var engine = new DataCopyEngine(
                _source,
                _target,
                new CopySettings { BatchSize = 500, DryRun = true },
                progress
            );
            var result = await engine.CopyAsync(configs, _cts.Token);

            foreach (var t in result.Tables)
            {
                Tables.Add(
                    new PreviewTableRow
                    {
                        Table = t.Table,
                        RowsRead = t.RowsRead,
                        Inserted = t.Inserted,
                        Updated = t.Updated,
                        Error = t.Error,
                    }
                );
            }

            var failed = result.Tables.Count(t => !t.Success);
            var totalInserted = result.Tables.Sum(t => t.Inserted);
            var totalUpdated = result.Tables.Sum(t => t.Updated);
            PreviewSummary =
                failed == 0
                    ? $"Предпросмотр завершён: таблиц {result.Tables.Count}, будет вставлено {totalInserted:N0}, будет обновлено {totalUpdated:N0}."
                    : $"Предпросмотр завершён с ошибками ({failed} из {result.Tables.Count} таблиц). Будет вставлено {totalInserted:N0}, обновлено {totalUpdated:N0}. Подробности — в журнале и в столбце «Статус».";
            HasPreview = true;
        }
        catch (OperationCanceledException)
        {
            PreviewSummary = "Предпросмотр отменён пользователем.";
        }
        catch (Exception ex)
        {
            PreviewSummary = $"Ошибка предпросмотра: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            CanCancel = false;
            StatusText = null;
            _cts?.Dispose();
            _cts = null;
        }
    }

    public void Reset()
    {
        Tables.Clear();
        Log.Clear();
        PreviewSummary = null;
        SummaryText = null;
        StatusText = null;
        CurrentTable = "";
        ProcessedRows = 0;
        HasPreview = false;
    }

    [RelayCommand(CanExecute = nameof(CanStartCopy))]
    private async Task StartCopyAsync()
    {
        if (_source is null || _target is null || _configs is null || _configs.Count == 0)
            return;

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
            var engine = new DataCopyEngine(
                _source,
                _target,
                new CopySettings { BatchSize = 500 },
                progress
            );
            var result = await engine.CopyAsync(_configs, _cts.Token);

            var totalInserted = result.Tables.Sum(t => t.Inserted);
            var totalUpdated = result.Tables.Sum(t => t.Updated);
            var failed = result.Tables.Count(t => !t.Success);

            SummaryText =
                failed == 0
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

    partial void OnIsBusyChanged(bool value) => UpdateCanStartCopy();

    partial void OnIsCopyingChanged(bool value) => UpdateCanStartCopy();

    private void UpdateCanStartCopy() => CanStartCopy = !IsBusy && !IsCopying;

    partial void OnCanStartCopyChanged(bool value) => StartCopyCommand.NotifyCanExecuteChanged();

    private void OnCopyProgress(CopyProgress progress)
    {
        CurrentTable = progress.Stage is CopyStage.Completed or CopyStage.Failed
            ? ""
            : progress.Table.ToString();
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

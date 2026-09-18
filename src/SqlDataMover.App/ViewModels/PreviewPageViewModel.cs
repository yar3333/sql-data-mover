using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlDataMover.App.Localization;
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
    public long Deleted { get; init; }
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

    /// <summary>Число строк по каждой таблице из последнего предпросмотра (в порядке копирования).</summary>
    private IReadOnlyList<(DbObjectName Table, long Rows)> _previewRows = [];

    /// <summary>Префиксные суммы строк: prefix[i] — строки таблиц до индекса i.</summary>
    private long[] _prefixRows = [];

    private long _totalRows;
    private int _currentTableIndex = -1;

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

    /// <summary>
    /// Разрешить временное задвоение уникальных значений: уникальные индексы приёмника
    /// отключаются на время копирования таблицы и пересоздаются перед коммитом.
    /// </summary>
    [ObservableProperty]
    public partial bool AllowTemporaryUniqueDuplicates { get; set; }

    /// <summary>
    /// Удалять лишние записи в таблицах-получателях: строки, комбинация значений полей
    /// сопоставления которых отсутствует в источнике, удаляются до вставки/обновления.
    /// </summary>
    [ObservableProperty]
    public partial bool DeleteExtraRows { get; set; }

    [ObservableProperty]
    public partial string CurrentTable { get; set; } = "";

    [ObservableProperty]
    public partial long ProcessedRows { get; set; }

    /// <summary>Строка вида «Rows: N» рядом со счётчиком обработанных строк.</summary>
    public string ProcessedRowsText => $"{AppStrings.Current.RowsWord}: {ProcessedRows:N0}";

    partial void OnProcessedRowsChanged(long value) => OnPropertyChanged(nameof(ProcessedRowsText));

    /// <summary>Общий прогресс копирования в процентах (знаменатель — строки из предпросмотра).</summary>
    [ObservableProperty]
    public partial double ProgressPercent { get; set; }

    /// <summary>Текст прогресса вида «45% · 4 500 / 10 000 строк».</summary>
    [ObservableProperty]
    public partial string? ProgressText { get; set; }

    /// <summary>Полоса неопределённая, пока идёт сам предпросмотр или нет данных для расчёта процентов.</summary>
    public bool IsProgressIndeterminate => IsBusy || _previewRows.Count == 0;

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
        StatusText = AppStrings.Current.DryRunStatus;
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
                new CopySettings
                {
                    BatchSize = 500,
                    DryRun = true,
                    DeleteExtraRows = DeleteExtraRows,
                },
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
                        Deleted = t.Deleted,
                        Error = t.Error,
                    }
                );
            }

            // Число строк по таблицам из предпросмотра — знаменатель для честного процента при копировании.
            _previewRows = result.Tables.Select(t => (t.Table, t.RowsRead)).ToList();
            _prefixRows = new long[_previewRows.Count + 1];
            for (var i = 0; i < _previewRows.Count; i++)
                _prefixRows[i + 1] = _prefixRows[i] + _previewRows[i].Rows;
            _totalRows = _prefixRows[^1];
            OnPropertyChanged(nameof(IsProgressIndeterminate));

            var failed = result.Tables.Count(t => !t.Success);
            var totalInserted = result.Tables.Sum(t => t.Inserted);
            var totalUpdated = result.Tables.Sum(t => t.Updated);
            var totalDeleted = result.Tables.Sum(t => t.Deleted);
            PreviewSummary =
                failed == 0
                    ? DeleteExtraRows
                        ? AppStrings.Current.FormatPreviewSummaryWithDeletes(
                            result.Tables.Count,
                            totalInserted,
                            totalUpdated,
                            totalDeleted
                        )
                        : AppStrings.Current.FormatPreviewSummary(
                            result.Tables.Count,
                            totalInserted,
                            totalUpdated
                        )
                    : AppStrings.Current.FormatPreviewSummaryWithErrors(
                        failed,
                        result.Tables.Count,
                        totalInserted,
                        totalUpdated
                    );
            HasPreview = true;
        }
        catch (OperationCanceledException)
        {
            PreviewSummary = AppStrings.Current.PreviewCancelled;
        }
        catch (Exception ex)
        {
            PreviewSummary = AppStrings.Current.FormatPreviewError(ex.Message);
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
        ProgressPercent = 0;
        ProgressText = null;
        HasPreview = false;
        _previewRows = [];
        _prefixRows = [];
        _totalRows = 0;
        _currentTableIndex = -1;
        OnPropertyChanged(nameof(IsProgressIndeterminate));
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
        ProgressPercent = 0;
        ProgressText = null;
        _currentTableIndex = -1;
        _cts = new CancellationTokenSource();
        IsCopying = true;
        CanCancel = true;

        try
        {
            var progress = new Progress<CopyProgress>(OnCopyProgress);
            var engine = new DataCopyEngine(
                _source,
                _target,
                new CopySettings
                {
                    BatchSize = 500,
                    AllowTemporaryUniqueDuplicates = AllowTemporaryUniqueDuplicates,
                    DeleteExtraRows = DeleteExtraRows,
                },
                progress
            );
            var result = await engine.CopyAsync(_configs, _cts.Token);

            var totalInserted = result.Tables.Sum(t => t.Inserted);
            var totalUpdated = result.Tables.Sum(t => t.Updated);
            var totalDeleted = result.Tables.Sum(t => t.Deleted);
            var failed = result.Tables.Count(t => !t.Success);

            if (failed == 0)
                ProgressPercent = 100;

            SummaryText =
                failed == 0
                    ? DeleteExtraRows
                        ? AppStrings.Current.FormatCopySummaryWithDeletes(
                            result.Elapsed.TotalSeconds,
                            result.Tables.Count,
                            totalInserted,
                            totalUpdated,
                            totalDeleted
                        )
                        : AppStrings.Current.FormatCopySummary(
                            result.Elapsed.TotalSeconds,
                            result.Tables.Count,
                            totalInserted,
                            totalUpdated
                        )
                    : AppStrings.Current.FormatCopySummaryWithErrors(
                        failed,
                        result.Tables.Count,
                        totalInserted,
                        totalUpdated
                    );
        }
        catch (OperationCanceledException)
        {
            SummaryText = AppStrings.Current.CopyCancelled;
        }
        catch (Exception ex)
        {
            SummaryText = AppStrings.Current.FormatCopyError(ex.Message);
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

    partial void OnIsBusyChanged(bool value)
    {
        UpdateCanStartCopy();
        OnPropertyChanged(nameof(IsProgressIndeterminate));
    }

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

        UpdateProgressPercent(progress);
    }

    /// <summary>
    /// Честный процент по данным предпросмотра: сумма строк завершённых таблиц (префикс)
    /// плюс обработанные строки текущей, делённая на общее число строк из dry run.
    /// </summary>
    private void UpdateProgressPercent(CopyProgress progress)
    {
        if (_previewRows.Count == 0 || _totalRows == 0)
            return;

        var index = FindTableIndex(progress.Table);
        if (index < 0)
            return;

        if (index != _currentTableIndex)
            _currentTableIndex = index;

        var currentRows = _previewRows[_currentTableIndex].Rows;
        var done = _prefixRows[_currentTableIndex] + Math.Min(progress.ProcessedRows, currentRows);
        ProgressPercent = Math.Clamp(done * 100.0 / _totalRows, 0.0, 100.0);
        ProgressText = AppStrings.Current.FormatProgress(ProgressPercent, done, _totalRows);
    }

    private int FindTableIndex(DbObjectName table)
    {
        for (var i = 0; i < _previewRows.Count; i++)
            if (_previewRows[i].Table == table)
                return i;
        return -1;
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

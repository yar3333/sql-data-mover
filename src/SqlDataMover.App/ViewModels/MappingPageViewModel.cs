using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SqlDataMover.App.Localization;
using SqlDataMover.Core.Abstractions;
using SqlDataMover.Core.Copy;
using SqlDataMover.Core.Models;

namespace SqlDataMover.App.ViewModels;

/// <summary>Строка списка на шаге 3: таблица и выбранные поля сопоставления.</summary>
public partial class TableMappingViewModel : ViewModelBase
{
    public DbObjectName Table { get; }
    public string Display => Table.ToString();
    public ObservableCollection<ColumnChoiceViewModel> Columns { get; } = [];

    public TableMappingViewModel(
        DbObjectName table,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> selectedColumns
    )
    {
        Table = table;
        foreach (var column in columns)
            Columns.Add(
                new ColumnChoiceViewModel(
                    column,
                    selectedColumns.Contains(column, StringComparer.OrdinalIgnoreCase)
                )
            );
    }

    /// <summary>Выбранные поля: комбинация их значений — ключ сопоставления строк.</summary>
    public IReadOnlyList<string> GetMatchColumns() =>
        Columns.Where(c => c.IsSelected).Select(c => c.Name).ToList();
}

/// <summary>Колонка таблицы с чекбоксом выбора поля сопоставления.</summary>
public partial class ColumnChoiceViewModel : ViewModelBase
{
    public string Name { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public ColumnChoiceViewModel(string name, bool isSelected = false)
    {
        Name = name;
        IsSelected = isSelected;
    }
}

/// <summary>Шаг 3: выбор полей сопоставления для каждой таблицы.</summary>
public partial class MappingPageViewModel : ViewModelBase
{
    private IDbProvider? _source;

    public ObservableCollection<TableMappingViewModel> Tables { get; } = [];

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? StatusText { get; set; }

    /// <summary>Для каждой ли таблицы выбрано хотя бы одно поле сопоставления.</summary>
    [ObservableProperty]
    public partial bool AllHaveMatchColumns { get; set; }

    public async Task InitializeAsync(IReadOnlyList<TableCopyConfig> configs, IDbProvider source)
    {
        _source = source;
        IsBusy = true;
        StatusText = AppStrings.Current.LoadingTableColumns;

        try
        {
            Tables.Clear();

            foreach (var cfg in configs)
            {
                var details = await source.GetTableDetailsAsync(cfg.Table);
                var columns = details
                    .Columns.Where(c => !c.IsComputed)
                    .Select(c => c.Name)
                    .ToList();
                // По умолчанию — все колонки первичного ключа (их комбинация гарантированно
                // уникальна), иначе первая колонка.
                var defaults =
                    details.PrimaryKeyColumns.Count > 0
                        ? details.PrimaryKeyColumns
                        : columns.Take(1).ToList();
                var vm = new TableMappingViewModel(cfg.Table, columns, defaults);
                foreach (var column in vm.Columns)
                    column.PropertyChanged += OnColumnPropertyChanged;
                Tables.Add(vm);
            }

            UpdateAllHaveMatchColumns();
            StatusText = null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Конфигурации копирования по текущему выбору полей сопоставления.</summary>
    public IReadOnlyList<TableCopyConfig> GetConfigs() =>
        Tables
            .Select(t => new TableCopyConfig
            {
                Table = t.Table,
                MatchColumns = t.GetMatchColumns(),
            })
            .ToList();

    public void Reset()
    {
        Tables.Clear();
        StatusText = null;
        AllHaveMatchColumns = false;
    }

    private void OnColumnPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e
    )
    {
        if (e.PropertyName == nameof(ColumnChoiceViewModel.IsSelected))
            UpdateAllHaveMatchColumns();
    }

    private void UpdateAllHaveMatchColumns() =>
        AllHaveMatchColumns = Tables.Count > 0 && Tables.All(t => t.GetMatchColumns().Count > 0);
}

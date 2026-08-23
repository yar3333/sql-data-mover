using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SqlDataMover.App.Localization;
using SqlDataMover.Core.Copy;
using SqlDataMover.Core.Models;

namespace SqlDataMover.App.ViewModels;

/// <summary>Шаг 2: дерево таблиц (схема → таблицы) с чекбоксами и фильтром.</summary>
public partial class TablesPageViewModel : ViewModelBase
{
    private readonly List<SchemaNodeViewModel> _allSchemas = [];

    public ObservableCollection<SchemaNodeViewModel> Schemas { get; } = [];

    [ObservableProperty]
    public partial string FilterText { get; set; } = "";

    [ObservableProperty]
    public partial int SelectedCount { get; set; }

    [ObservableProperty]
    public partial int TotalCount { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>Строка «Selected: N» для шапки списка таблиц.</summary>
    public string SelectedCountText => $"{AppStrings.Current.Selected}: {SelectedCount}";

    /// <summary>Строка «Total: N» для шапки списка таблиц.</summary>
    public string TotalCountText => $"{AppStrings.Current.Total}: {TotalCount}";

    /// <summary>
    /// Загружает дерево таблиц. Таблицы, чьи имена ("schema.table") есть в
    /// <paramref name="preSelected"/>, отмечаются галочкой (выбор по умолчанию).
    /// </summary>
    public void LoadTables(
        IReadOnlyList<DbTable> tables,
        IReadOnlyCollection<string>? preSelected = null
    )
    {
        _allSchemas.Clear();
        var selected = preSelected is null
            ? null
            : new HashSet<string>(preSelected, StringComparer.OrdinalIgnoreCase);

        foreach (
            var group in tables
                .GroupBy(t => t.Name.Schema)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
        )
        {
            var schema = new SchemaNodeViewModel(group.Key);
            foreach (var table in group.OrderBy(t => t.Name.Name, StringComparer.OrdinalIgnoreCase))
            {
                var node = new TableNodeViewModel(table) { Parent = schema };
                node.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(TableNodeViewModel.IsChecked))
                        UpdateCounts();
                };
                schema.AllTables.Add(node);
                if (selected is not null && selected.Contains(table.Name.ToString()))
                    node.IsChecked = true;
            }
            schema.Recalculate();
            _allSchemas.Add(schema);
        }

        ApplyFilter();
        UpdateCounts();
    }

    public void ClearTables()
    {
        _allSchemas.Clear();
        Schemas.Clear();
        FilterText = "";
        SelectedCount = 0;
        TotalCount = 0;
    }

    public IReadOnlyList<TableCopyConfig> GetSelectedTables() =>
        _allSchemas
            .SelectMany(s => s.AllTables)
            .Where(t => t.IsChecked)
            .Select(t => new TableCopyConfig { Table = t.Table.Name, MatchColumns = [] })
            .ToList();

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        Schemas.Clear();
        var filter = FilterText?.Trim() ?? "";

        foreach (var schema in _allSchemas)
        {
            schema.ApplyFilter(filter);
            if (schema.VisibleTables.Count > 0)
                Schemas.Add(schema);
        }
    }

    private void UpdateCounts()
    {
        SelectedCount = _allSchemas.SelectMany(s => s.AllTables).Count(t => t.IsChecked);
        TotalCount = _allSchemas.SelectMany(s => s.AllTables).Count();
        OnPropertyChanged(nameof(SelectedCountText));
        OnPropertyChanged(nameof(TotalCountText));
    }
}

/// <summary>Узел схемы в дереве таблиц. Чекбокс с трёмя состояниями.</summary>
public partial class SchemaNodeViewModel : ViewModelBase
{
    public string Name { get; }
    public ObservableCollection<TableNodeViewModel> AllTables { get; } = [];
    public ObservableCollection<TableNodeViewModel> VisibleTables { get; } = [];

    [ObservableProperty]
    public partial bool? IsChecked { get; set; }

    private bool _suppress;

    public SchemaNodeViewModel(string name) => Name = name;

    public void ApplyFilter(string filter)
    {
        VisibleTables.Clear();

        if (string.IsNullOrEmpty(filter))
        {
            foreach (var t in AllTables)
                VisibleTables.Add(t);
        }
        else
        {
            foreach (var t in AllTables)
                if (
                    t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                )
                    VisibleTables.Add(t);
        }
    }

    public void Recalculate()
    {
        _suppress = true;
        if (AllTables.Count == 0)
            IsChecked = false;
        else if (AllTables.All(t => t.IsChecked))
            IsChecked = true;
        else if (AllTables.All(t => !t.IsChecked))
            IsChecked = false;
        else
            IsChecked = null;
        _suppress = false;
    }

    partial void OnIsCheckedChanged(bool? value)
    {
        if (_suppress || value is not bool b)
            return;

        _suppress = true;
        foreach (var t in AllTables)
            t.IsChecked = b;
        _suppress = false;
    }
}

/// <summary>Узел таблицы в дереве.</summary>
public partial class TableNodeViewModel : ViewModelBase
{
    public DbTable Table { get; }
    public string Name => Table.Name.Name;
    public string Display =>
        Table.RowCount > 0
            ? $"{Name}  ({AppStrings.Current.FormatRowCount(Table.RowCount)})"
            : Name;
    public SchemaNodeViewModel? Parent { get; set; }

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    public TableNodeViewModel(DbTable table) => Table = table;

    partial void OnIsCheckedChanged(bool value) => Parent?.Recalculate();
}

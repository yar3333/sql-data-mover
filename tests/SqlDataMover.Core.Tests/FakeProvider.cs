using System.Runtime.CompilerServices;
using SqlDataMover.Core;
using SqlDataMover.Core.Abstractions;
using SqlDataMover.Core.Models;

namespace SqlDataMover.Core.Tests;

/// <summary>In-memory провайдер для тестирования движка без реальной СУБД.</summary>
internal sealed class FakeProvider : IDbProvider
{
    private readonly Dictionary<DbObjectName, FakeTable> _tables = [];

    public string Name => "Fake";
    public bool IsConnected => true;

    public FakeProvider(params FakeTable[] tables)
    {
        foreach (var t in tables)
            _tables[t.Meta.Name] = t;
    }

    public FakeTable GetTable(DbObjectName name) => _tables[name];

    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<DbTable>> GetTablesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DbTable>>(_tables.Values.Select(t => t.Meta).ToList());

    public Task<DbTable> GetTableDetailsAsync(DbObjectName table, CancellationToken ct = default) =>
        _tables.TryGetValue(table, out var t)
            ? Task.FromResult(t.Meta)
            : throw new InvalidOperationException($"Таблица {table} не найдена");

    public async IAsyncEnumerable<object?[]> ReadRowsAsync(
        DbObjectName table,
        IReadOnlyList<string> columns,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        var ft = _tables[table];
        foreach (var row in ft.Rows)
        {
            ct.ThrowIfCancellationRequested();
            yield return columns.Select(c => row[ft.ColumnIndex(c)]).ToArray();
        }
    }

    public Task<Dictionary<object, object>> LoadMatchMapAsync(
        DbObjectName table,
        string uniqueColumn,
        string mappedColumn,
        CancellationToken ct = default
    )
    {
        var ft = _tables[table];
        var ui = ft.ColumnIndex(uniqueColumn);
        var mi = ft.ColumnIndex(mappedColumn);
        var map = new Dictionary<object, object>(ObjectKeyComparer.Instance);

        foreach (var row in ft.Rows)
            if (row[ui] is not null)
                map[row[ui]!] = row[mi]!;

        return Task.FromResult(map);
    }

    public Task<IDbWriteTransaction> BeginTransactionAsync(CancellationToken ct = default) =>
        Task.FromResult<IDbWriteTransaction>(new FakeTransaction());

    public Task<IReadOnlyList<object?>> InsertRowsAsync(
        DbTable table,
        IReadOnlyList<string> columns,
        IReadOnlyList<object?[]> rows,
        IDbWriteTransaction? transaction = null,
        CancellationToken ct = default
    )
    {
        var ft = _tables[table.Name];
        var ids = new List<object?>(rows.Count);

        foreach (var row in rows)
        {
            var values = ft.NewRow(columns, row);
            ft.Rows.Add(values);
            if (ft.Meta.IdentityColumn is { } identity)
                ids.Add(values[ft.ColumnIndex(identity)]);
        }

        return Task.FromResult<IReadOnlyList<object?>>(ids);
    }

    public Task<int> UpdateRowsAsync(
        DbTable table,
        string uniqueColumn,
        IReadOnlyList<string> allColumns,
        IReadOnlyList<string> updateColumns,
        IReadOnlyList<object?[]> rows,
        IDbWriteTransaction? transaction = null,
        CancellationToken ct = default
    )
    {
        var ft = _tables[table.Name];
        var uniqueIndex = ft.ColumnIndex(uniqueColumn);
        var keyIndex = IndexIn(allColumns, uniqueColumn);
        var updateIndices = updateColumns
            .Select(c => (Column: c, Index: IndexIn(allColumns, c)))
            .ToArray();
        int updated = 0;

        foreach (var row in rows)
        {
            var key = row[keyIndex];
            var target = ft.Rows.FirstOrDefault(r =>
                r[uniqueIndex] is not null && ObjectKeyComparer.Instance.Equals(r[uniqueIndex], key)
            );
            if (target is null)
                continue;

            foreach (var (column, index) in updateIndices)
                target[ft.ColumnIndex(column)] = row[index];
            updated++;
        }

        return Task.FromResult(updated);
    }

    public Task<int> ExecuteUpdatesAsync(
        DbTable table,
        string setColumn,
        string whereColumn,
        IReadOnlyList<(object? SetValue, object? WhereValue)> pairs,
        IDbWriteTransaction? transaction = null,
        CancellationToken ct = default
    )
    {
        var ft = _tables[table.Name];
        var si = ft.ColumnIndex(setColumn);
        var wi = ft.ColumnIndex(whereColumn);
        int updated = 0;

        foreach (var (setValue, whereValue) in pairs)
        {
            var target = ft.Rows.FirstOrDefault(r =>
                r[wi] is not null && ObjectKeyComparer.Instance.Equals(r[wi], whereValue)
            );
            if (target is null)
                continue;
            target[si] = setValue;
            updated++;
        }

        return Task.FromResult(updated);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static int IndexIn(IReadOnlyList<string> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++)
            if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }
}

internal sealed class FakeTransaction : IDbWriteTransaction
{
    public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Таблица в памяти. Строки — массивы значений, выровненные по колонкам Meta.Columns.</summary>
internal sealed class FakeTable
{
    public required DbTable Meta { get; init; }
    public List<object?[]> Rows { get; } = [];

    public int ColumnIndex(string name)
    {
        for (var i = 0; i < Meta.Columns.Count; i++)
            if (string.Equals(Meta.Columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>Создаёт строку из значений по списку колонок и присваивает следующий identity.</summary>
    public object?[] NewRow(IReadOnlyList<string> columns, IReadOnlyList<object?> values)
    {
        var row = new object?[Meta.Columns.Count];
        for (var i = 0; i < columns.Count; i++)
            row[ColumnIndex(columns[i])] = values[i];

        if (Meta.IdentityColumn is { } identity)
            row[ColumnIndex(identity)] = NextIdentity();

        return row;
    }

    private int NextIdentity()
    {
        var idx = ColumnIndex(Meta.IdentityColumn!);
        var max =
            Rows.Count > 0 ? Rows.Max(r => r[idx] is null ? 0L : Convert.ToInt64(r[idx])) : 0L;
        return (int)(max + 1);
    }
}

/// <summary>Хелперы построения метаданных для тестов.</summary>
internal static class TestTables
{
    public static DbColumn Col(string name, bool identity = false, bool pk = false) =>
        new()
        {
            Name = name,
            DataType = "int",
            IsIdentity = identity,
            IsPrimaryKey = pk,
        };

    public static DbTable Table(
        string schema,
        string name,
        DbColumn[] columns,
        params DbForeignKey[] fks
    ) =>
        new()
        {
            Name = new DbObjectName(schema, name),
            Columns = columns,
            PrimaryKeyColumns = columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList(),
            IdentityColumn = columns.FirstOrDefault(c => c.IsIdentity)?.Name,
            ForeignKeys = fks,
        };

    public static DbForeignKey Fk(
        string name,
        string childSchema,
        string childTable,
        string childCol,
        string parentSchema,
        string parentTable,
        string parentCol
    ) =>
        new()
        {
            Name = name,
            ReferencingTable = new DbObjectName(childSchema, childTable),
            ReferencedTable = new DbObjectName(parentSchema, parentTable),
            ColumnPairs = [new ForeignKeyColumnPair(childCol, parentCol)],
        };
}

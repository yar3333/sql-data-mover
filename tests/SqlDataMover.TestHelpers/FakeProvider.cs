using System.Runtime.CompilerServices;
using SqlDataMover.Core;
using SqlDataMover.Core.Abstractions;
using SqlDataMover.Core.Models;

namespace SqlDataMover.TestHelpers;

/// <summary>In-memory провайдер для тестирования движка и GUI без реальной СУБД.</summary>
public sealed class FakeProvider : IDbProvider
{
    private readonly Dictionary<DbObjectName, FakeTable> _tables = [];
    private readonly bool _failOnConnect;

    /// <summary>Уникальные колонки (по одной на «индекс»), для которых временно отключена проверка уникальности.</summary>
    private readonly HashSet<(DbObjectName Table, string Column)> _disabledUnique = [];

    public string Name => "Fake";
    public bool IsConnected => true;

    public FakeProvider(params FakeTable[] tables)
        : this(false, tables) { }

    /// <summary>
    /// При <paramref name="failOnConnect"/> == true <see cref="ConnectAsync"/> бросает
    /// исключение — имитация недоступного сервера для тестов обработки ошибок.
    /// </summary>
    public FakeProvider(bool failOnConnect, params FakeTable[] tables)
    {
        _failOnConnect = failOnConnect;
        foreach (var t in tables)
            _tables[t.Meta.Name] = t;
    }

    public FakeTable GetTable(DbObjectName name) => _tables[name];

    public Task ConnectAsync(CancellationToken ct = default) =>
        _failOnConnect
            ? throw new InvalidOperationException("Server unavailable (simulated)")
            : Task.CompletedTask;

    public Task<IReadOnlyList<DbTable>> GetTablesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DbTable>>(_tables.Values.Select(t => t.Meta).ToList());

    public Task<DbTable> GetTableDetailsAsync(DbObjectName table, CancellationToken ct = default) =>
        _tables.TryGetValue(table, out var t)
            ? Task.FromResult(t.Meta)
            : throw new InvalidOperationException($"Table {table} not found");

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
        IReadOnlyList<string> matchColumns,
        string mappedColumn,
        IDbWriteTransaction? transaction = null,
        CancellationToken ct = default
    )
    {
        var ft = _tables[table];
        var matchIndices = matchColumns.Select(c => ft.ColumnIndex(c)).ToArray();
        var mi = ft.ColumnIndex(mappedColumn);
        var map = new Dictionary<object, object>();

        foreach (var row in ft.Rows)
        {
            var keyValues = new object?[matchIndices.Length];
            var isNull = false;
            for (var i = 0; i < matchIndices.Length; i++)
            {
                if (row[matchIndices[i]] is null)
                {
                    isNull = true;
                    break;
                }
                keyValues[i] = row[matchIndices[i]];
            }

            if (isNull)
                continue;

            map[new CompositeKey(keyValues)] = row[mi]!;
        }

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
        var insertedThisBatch = new List<object?[]>(rows.Count);

        foreach (var row in rows)
        {
            CheckUniqueConstraints(table.Name, ft, columns, row, insertedThisBatch);

            var values = ft.NewRow(columns, row);
            ft.Rows.Add(values);
            insertedThisBatch.Add(values);
            if (ft.Meta.IdentityColumn is { } identity)
                ids.Add(values[ft.ColumnIndex(identity)]);
        }

        return Task.FromResult<IReadOnlyList<object?>>(ids);
    }

    /// <summary>
    /// Имитация уникального ограничения: если для уникальной колонки не включён режим
    /// временного отключения (<see cref="SetUniqueIndexEnabledAsync"/>), повторное значение
    /// среди уже существующих строк (или среди строк этого же пакета) считается нарушением.
    /// </summary>
    private void CheckUniqueConstraints(
        DbObjectName table,
        FakeTable ft,
        IReadOnlyList<string> columns,
        object?[] row,
        IReadOnlyList<object?[]> insertedThisBatch
    )
    {
        foreach (var uniqueColumn in ft.UniqueColumns)
        {
            if (_disabledUnique.Contains((table, uniqueColumn)))
                continue;

            var columnIndex = IndexIn(columns, uniqueColumn);
            if (columnIndex < 0)
                continue;

            var value = row[columnIndex];
            if (value is null)
                continue;

            var tableIndex = ft.ColumnIndex(uniqueColumn);
            var exists = ft.Rows.Any(r =>
                r[tableIndex] is not null
                && ObjectKeyComparer.Instance.Equals(r[tableIndex], value)
            );
            exists |= insertedThisBatch.Any(r =>
                r[tableIndex] is not null && ObjectKeyComparer.Instance.Equals(r[tableIndex], value)
            );
            if (exists)
                throw new InvalidOperationException(
                    $"UNIQUE constraint violation: value '{value}' already exists in {table}.{uniqueColumn}"
                );
        }
    }

    public Task<int> UpdateRowsAsync(
        DbTable table,
        IReadOnlyList<string> matchColumns,
        IReadOnlyList<string> allColumns,
        IReadOnlyList<string> updateColumns,
        IReadOnlyList<object?[]> rows,
        IDbWriteTransaction? transaction = null,
        CancellationToken ct = default
    )
    {
        var ft = _tables[table.Name];
        var matchIndicesInRow = matchColumns.Select(c => IndexIn(allColumns, c)).ToArray();
        var matchIndicesInTable = matchColumns.Select(c => ft.ColumnIndex(c)).ToArray();
        var updateIndices = updateColumns
            .Select(c => (Column: c, Index: IndexIn(allColumns, c)))
            .ToArray();
        int updated = 0;

        foreach (var row in rows)
        {
            var target = ft.Rows.FirstOrDefault(r =>
                matchIndicesInTable
                    .Select(
                        (idx, i) =>
                            r[idx] is not null
                            && ObjectKeyComparer.Instance.Equals(r[idx], row[matchIndicesInRow[i]])
                    )
                    .All(b => b)
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

    public Task SetIdentityInsertAsync(
        DbObjectName table,
        bool enabled,
        IDbWriteTransaction? transaction = null,
        CancellationToken ct = default
    ) =>
        // В памяти ограничений на вставку identity нет — флаг только отмечается.
        Task.FromResult(_tables[table].IdentityInsertEnabled = enabled);

    public Task<long> GetIdentityCurrentAsync(DbObjectName table, CancellationToken ct = default)
    {
        var ft = _tables[table];
        return Task.FromResult(
            ft.Meta.IdentityColumn is null ? 0 : ft.GetIdentityCurrent()
        );
    }

    public Task<IReadOnlyList<string>> GetUniqueIndexesAsync(
        DbObjectName table,
        CancellationToken ct = default
    ) =>
        Task.FromResult<IReadOnlyList<string>>(
            _tables[table].UniqueColumns.Select(c => "UQ_" + c).ToList()
        );

    public Task SetUniqueIndexEnabledAsync(
        DbObjectName table,
        string indexName,
        bool enabled,
        IDbWriteTransaction? transaction = null,
        CancellationToken ct = default
    )
    {
        const string prefix = "UQ_";
        if (!indexName.StartsWith(prefix, StringComparison.Ordinal))
            throw new ArgumentException($"Unknown unique index: {indexName}", nameof(indexName));

        var column = indexName[prefix.Length..];
        var key = (table, column);
        if (enabled)
            _disabledUnique.Remove(key);
        else
            _disabledUnique.Add(key);
        return Task.CompletedTask;
    }

    public Task ReseedIdentityAsync(
        DbObjectName table,
        long newValue,
        IDbWriteTransaction? transaction = null,
        CancellationToken ct = default
    )
    {
        _tables[table].ReseedIdentity(newValue);
        return Task.CompletedTask;
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

public sealed class FakeTransaction : IDbWriteTransaction
{
    public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Таблица в памяти. Строки — массивы значений, выровненные по колонкам Meta.Columns.</summary>
public sealed class FakeTable
{
    private long _identityCurrent;

    public required DbTable Meta { get; init; }
    public List<object?[]> Rows { get; } = [];

    /// <summary>Отмечает, что движок включил режим IDENTITY_INSERT (для проверки в тестах).</summary>
    public bool IdentityInsertEnabled { get; set; }

    /// <summary>Уникальные колонки таблицы (по одной на «уникальный индекс» UQ_&lt;колонка&gt;).</summary>
    public List<string> UniqueColumns { get; } = [];

    public int ColumnIndex(string name)
    {
        for (var i = 0; i < Meta.Columns.Count; i++)
            if (string.Equals(Meta.Columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>Аналог IDENT_CURRENT: последнее назначенное (или переданное явно) значение identity.</summary>
    public long GetIdentityCurrent()
    {
        var idx = ColumnIndex(Meta.IdentityColumn!);
        var max =
            Rows.Count > 0 ? Rows.Max(r => r[idx] is null ? 0L : Convert.ToInt64(r[idx])) : 0L;
        return Math.Max(max, _identityCurrent);
    }

    /// <summary>Аналог DBCC CHECKIDENT (RESEED): задаёт текущее значение счётчика identity.</summary>
    public void ReseedIdentity(long value) => _identityCurrent = value;

    /// <summary>
    /// Создаёт строку из значений по списку колонок. Значение identity, переданное явно
    /// (режим IDENTITY_INSERT), сохраняется и подстраивает счётчик; иначе присваивается следующий identity.
    /// </summary>
    public object?[] NewRow(IReadOnlyList<string> columns, IReadOnlyList<object?> values)
    {
        var row = new object?[Meta.Columns.Count];
        for (var i = 0; i < columns.Count; i++)
            row[ColumnIndex(columns[i])] = values[i];

        if (Meta.IdentityColumn is { } identity)
        {
            var idx = ColumnIndex(identity);
            if (row[idx] is not null)
            {
                var value = Convert.ToInt64(row[idx]);
                if (value > _identityCurrent)
                    _identityCurrent = value;
            }
            else
            {
                var next = GetIdentityCurrent() + 1;
                // В тестах identity обычно int; long сохраняем только для значений за пределами int.
                row[idx] = next > int.MaxValue ? (object)next : (int)next;
                _identityCurrent = Convert.ToInt64(row[idx]);
            }
        }

        return row;
    }
}

/// <summary>Хелперы построения метаданных для тестов.</summary>
public static class TestTables
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

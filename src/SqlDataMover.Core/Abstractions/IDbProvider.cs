using SqlDataMover.Core.Models;

namespace SqlDataMover.Core.Abstractions;

/// <summary>
/// Адаптер СУБД. Каждая поддерживаемая СУБД реализует этот интерфейс и регистрируется в
/// <see cref="DbProviderFactory"/>. Движок копирования работает только через этот интерфейс.
/// </summary>
public interface IDbProvider : IAsyncDisposable
{
    string Name { get; }

    bool IsConnected { get; }

    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>Список всех пользовательских таблиц с приблизительным количеством строк.</summary>
    Task<IReadOnlyList<DbTable>> GetTablesAsync(CancellationToken ct = default);

    /// <summary>Полные метаданные таблицы: колонки, первичный ключ, identity-колонка, внешние ключи.</summary>
    Task<DbTable> GetTableDetailsAsync(DbObjectName table, CancellationToken ct = default);

    /// <summary>Потоковое чтение строк таблицы; массивы значений выровнены по списку <paramref name="columns"/>.</summary>
    IAsyncEnumerable<object?[]> ReadRowsAsync(
        DbObjectName table,
        IReadOnlyList<string> columns,
        CancellationToken ct = default
    );

    /// <summary>
    /// Словарь «комбинация значений полей сопоставления → значение колонки сопоставления» для
    /// существующих строк целевой таблицы. Ключи — <see cref="CompositeKey"/>, компоненты-строки
    /// сравниваются без учёта регистра. Строки с NULL в любом поле сопоставления в словарь не попадают.
    /// </summary>
    Task<Dictionary<object, object>> LoadMatchMapAsync(
        DbObjectName table,
        IReadOnlyList<string> matchColumns,
        string mappedColumn,
        CancellationToken ct = default
    );

    Task<IDbWriteTransaction> BeginTransactionAsync(CancellationToken ct = default);

    /// <summary>
    /// Пакетная вставка строк (массивы выровнены по <paramref name="columns"/>, identity-колонка в них не входит).
    /// Возвращает значения identity для вставленных строк в том же порядке; пустой список, если identity нет.
    /// </summary>
    Task<IReadOnlyList<object?>> InsertRowsAsync(
        DbTable table,
        IReadOnlyList<string> columns,
        IReadOnlyList<object?[]> rows,
        IDbWriteTransaction? transaction = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Обновление строк по комбинации полей сопоставления (WHERE по всем полям через AND).
    /// <paramref name="allColumns"/> — полный список колонок, по которым выровнены массивы строк;
    /// обновляются только колонки из <paramref name="updateColumns"/>.
    /// </summary>
    Task<int> UpdateRowsAsync(
        DbTable table,
        IReadOnlyList<string> matchColumns,
        IReadOnlyList<string> allColumns,
        IReadOnlyList<string> updateColumns,
        IReadOnlyList<object?[]> rows,
        IDbWriteTransaction? transaction = null,
        CancellationToken ct = default
    );

    /// <summary>Пакетное обновление одного столбца по ключу (используется для самоссылающихся внешних ключей).</summary>
    Task<int> ExecuteUpdatesAsync(
        DbTable table,
        string setColumn,
        string whereColumn,
        IReadOnlyList<(object? SetValue, object? WhereValue)> pairs,
        IDbWriteTransaction? transaction = null,
        CancellationToken ct = default
    );
}

/// <summary>Транзакция записи. При отмене (Dispose без Commit) изменения откатываются.</summary>
public interface IDbWriteTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct = default);
}

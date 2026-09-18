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
    /// Вызывается в контексте транзакции записи, поэтому команда должна использовать
    /// <paramref name="transaction"/>.
    /// </summary>
    Task<Dictionary<object, object>> LoadMatchMapAsync(
        DbObjectName table,
        IReadOnlyList<string> matchColumns,
        string mappedColumn,
        IDbWriteTransaction? transaction = null,
        CancellationToken ct = default
    );

    Task<IDbWriteTransaction> BeginTransactionAsync(CancellationToken ct = default);

    /// <summary>
    /// Пакетная вставка строк (массивы выровнены по <paramref name="columns"/>). Обычно identity-колонка
    /// в список не входит и значение назначает СУБД; если она входит (режим <see cref="SetIdentityInsertAsync"/>),
    /// значения вставляются как есть. Возвращает значения identity для вставленных строк в том же порядке;
    /// пустой список, если identity нет.
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

    /// <summary>
    /// Удаление строк по комбинации полей сопоставления: для каждой строки из
    /// <paramref name="keys"/> удаляются строки, у которых значения всех
    /// <paramref name="whereColumns"/> равны значениям ключа (AND). Ключи не содержат NULL
    /// (строки с NULL в полях сопоставления в <see cref="LoadMatchMapAsync"/> не попадают).
    /// Ключи передаются пакетом; реализация сама разбивает их на запросы.
    /// </summary>
    Task<int> DeleteRowsAsync(
        DbObjectName table,
        IReadOnlyList<string> whereColumns,
        IReadOnlyList<object?[]> keys,
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

    /// <summary>
    /// Включает или выключает режим вставки явных значений identity (SET IDENTITY_INSERT) для таблицы.
    /// Используется, когда identity-колонка приёмника выбрана полем сопоставления: значения identity
    /// копируются из источника как есть. Должен вызываться в контексте транзакции записи.
    /// </summary>
    Task SetIdentityInsertAsync(
        DbObjectName table,
        bool enabled,
        IDbWriteTransaction? transaction = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Текущее значение счётчика identity (IDENT_CURRENT) таблицы — для выравнивания счётчика приёмника.
    /// </summary>
    Task<long> GetIdentityCurrentAsync(DbObjectName table, CancellationToken ct = default);

    /// <summary>
    /// Имена уникальных индексов (включая индексы уникальных ограничений) таблицы, кроме первичного
    /// ключа. Используется для временного отключения проверки уникальности на время копирования
    /// (<see cref="SetUniqueIndexEnabledAsync"/>).
    /// </summary>
    Task<IReadOnlyList<string>> GetUniqueIndexesAsync(
        DbObjectName table,
        CancellationToken ct = default
    );

    /// <summary>
    /// Включает или выключает уникальный индекс (в SQL Server — ALTER INDEX ... DISABLE / REBUILD).
    /// Вызывается в контексте транзакции записи.
    /// </summary>
    Task SetUniqueIndexEnabledAsync(
        DbObjectName table,
        string indexName,
        bool enabled,
        IDbWriteTransaction? transaction = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Устанавливает счётчик identity таблицы (DBCC CHECKIDENT RESEED), чтобы приёмник продолжал
    /// нумерацию источника. Вызывается в контексте транзакции записи.
    /// </summary>
    Task ReseedIdentityAsync(
        DbObjectName table,
        long newValue,
        IDbWriteTransaction? transaction = null,
        CancellationToken ct = default
    );
}

/// <summary>Транзакция записи. При отмене (Dispose без Commit) изменения откатываются.</summary>
public interface IDbWriteTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct = default);
}

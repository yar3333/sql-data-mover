using System.Diagnostics;
using SqlDataMover.Core.Abstractions;
using SqlDataMover.Core.Localization;
using SqlDataMover.Core.Models;

namespace SqlDataMover.Core.Copy;

/// <summary>
/// Движок копирования данных.
/// Сортирует таблицы по внешним ключам, копирует строки с переназначением identity-значений,
/// строит в памяти соответствие «оригинальный ID → новый ID» и подставляет новые ID
/// во внешние ключи зависимых таблиц.
/// </summary>
public sealed class DataCopyEngine
{
    private readonly IDbProvider _source;
    private readonly IDbProvider _target;
    private readonly CopySettings _settings;
    private readonly IProgress<CopyProgress>? _progress;
    private readonly IdMappingStore _mappings = new();

    public DataCopyEngine(
        IDbProvider source,
        IDbProvider target,
        CopySettings? settings = null,
        IProgress<CopyProgress>? progress = null
    )
    {
        _source = source;
        _target = target;
        _settings = settings ?? new CopySettings();
        _progress = progress;
    }

    public async Task<CopyResult> CopyAsync(
        IReadOnlyList<TableCopyConfig> configs,
        CancellationToken ct = default
    )
    {
        if (configs.Count == 0)
            return new CopyResult { Tables = [] };

        var stopwatch = Stopwatch.StartNew();
        var resultByTable = new Dictionary<DbObjectName, TableCopyResult>(configs.Count);
        foreach (var cfg in configs)
            resultByTable[cfg.Table] = new TableCopyResult { Table = cfg.Table };

        var byTable = configs.ToDictionary(c => c.Table);
        var selected = byTable.Keys.ToHashSet();
        var dryRun = _settings.DryRun;

        Report(
            new CopyProgress(
                new DbObjectName(string.Empty, string.Empty),
                CopyStage.Preparing,
                0,
                CoreStrings.LoadingMetadata
            )
        );

        var sourceDetails = new Dictionary<DbObjectName, DbTable>();
        var targetDetails = new Dictionary<DbObjectName, DbTable>();
        foreach (var cfg in configs)
        {
            ct.ThrowIfCancellationRequested();
            sourceDetails[cfg.Table] = await _source.GetTableDetailsAsync(cfg.Table, ct);
            targetDetails[cfg.Table] = await _target.GetTableDetailsAsync(cfg.Table, ct);
        }

        var referencedColumnsByTable = ComputeReferencedColumns(selected, sourceDetails);
        var order = TableDependencySorter.Sort(sourceDetails.Values);

        // Опция «удалять лишние записи в таблицах-получателях»: строки приёмника, которых нет
        // в источнике по полям сопоставления, удаляются до вставки/обновления — устаревшие
        // строки освобождают значения identity и уникальных полей, и вставка не конфликтует.
        // Удаление идёт в обратном топологическом порядке (дети раньше родителей), иначе
        // внешний ключ не позволит удалить родителя, на которого ещё ссылаются дети.
        if (_settings.DeleteExtraRows)
            await DeleteExtraRowsPassAsync(
                order,
                byTable,
                resultByTable,
                sourceDetails,
                targetDetails,
                dryRun,
                ct
            );

        foreach (var table in order)
        {
            ct.ThrowIfCancellationRequested();
            var cfg = byTable[table];
            var result = resultByTable[table];

            try
            {
                Report(
                    new CopyProgress(
                        table,
                        CopyStage.Preparing,
                        0,
                        dryRun
                            ? CoreStrings.FormatPreviewTable(table.ToString())
                            : CoreStrings.FormatCopyingTable(table.ToString())
                    )
                );

                // Опция «разрешить временное задвоение уникальных значений»: уникальные индексы
                // приёмника (кроме PK) на время копирования отключаются и пересоздаются перед
                // коммитом. Транзиентный конфликт (строка со значением вставлена раньше, чем
                // обновление освободило его у существующей строки) перестаёт быть ошибкой;
                // реальные дубликаты уронят REBUILD, и транзакция откатится.
                var uniqueIndexes =
                    !dryRun && _settings.AllowTemporaryUniqueDuplicates
                        ? await _target.GetUniqueIndexesAsync(table, ct)
                        : [];

                await using IDbWriteTransaction? tx = dryRun
                    ? null
                    : await _target.BeginTransactionAsync(ct);
                foreach (var index in uniqueIndexes)
                    await _target.SetUniqueIndexEnabledAsync(table, index, false, tx, ct);

                await CopyTableAsync(
                    cfg,
                    sourceDetails[table],
                    targetDetails[table],
                    selected,
                    referencedColumnsByTable,
                    tx,
                    result,
                    ct
                );
                if (!dryRun)
                {
                    foreach (var index in uniqueIndexes)
                        await _target.SetUniqueIndexEnabledAsync(table, index, true, tx, ct);
                    await tx!.CommitAsync(ct);
                }

                Report(
                    new CopyProgress(
                        table,
                        CopyStage.Completed,
                        result.RowsRead,
                        dryRun
                            ? CoreStrings.FormatPreviewSummary(
                                result.RowsRead,
                                result.Inserted,
                                result.Updated
                            )
                            : CoreStrings.FormatTableSummary(
                                result.RowsRead,
                                result.Inserted,
                                result.Updated,
                                result.Mapped,
                                result.Deleted
                            )
                    )
                );
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                result.Error = CoreStrings.CancelledByUser;
                Report(
                    new CopyProgress(
                        table,
                        CopyStage.Failed,
                        result.RowsRead,
                        CoreStrings.FormatTableCancelled(table.ToString()),
                        IsError: true
                    )
                );
                throw;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                Report(
                    new CopyProgress(
                        table,
                        CopyStage.Failed,
                        result.RowsRead,
                        CoreStrings.FormatTableError(table.ToString(), ex.Message),
                        IsError: true
                    )
                );
                if (!_settings.ContinueOnTableError)
                    throw;
            }
        }

        stopwatch.Stop();
        // Результаты — в топологическом порядке (родители раньше детей), как раньше.
        return new CopyResult
        {
            Tables = order.Select(t => resultByTable[t]).ToList(),
            Elapsed = stopwatch.Elapsed,
        };
    }

    private async Task DeleteExtraRowsPassAsync(
        IReadOnlyList<DbObjectName> order,
        IReadOnlyDictionary<DbObjectName, TableCopyConfig> byTable,
        IReadOnlyDictionary<DbObjectName, TableCopyResult> results,
        IReadOnlyDictionary<DbObjectName, DbTable> sourceDetails,
        IReadOnlyDictionary<DbObjectName, DbTable> targetDetails,
        bool dryRun,
        CancellationToken ct
    )
    {
        // Дети раньше родителей: обратный топологический порядок, иначе внешний ключ не позволит
        // удалить строки родительской таблицы, на которые ещё ссылаются строки детей.
        foreach (var table in order.Reverse())
        {
            ct.ThrowIfCancellationRequested();
            var cfg = byTable[table];
            var result = results[table];

            try
            {
                Report(
                    new CopyProgress(
                        table,
                        CopyStage.Copying,
                        0,
                        dryRun
                            ? CoreStrings.FormatPreviewDeletingTable(table.ToString())
                            : CoreStrings.FormatDeletingTable(table.ToString())
                    )
                );

                await using IDbWriteTransaction? tx = dryRun
                    ? null
                    : await _target.BeginTransactionAsync(ct);

                var deleted = await DeleteExtraRowsAsync(
                    cfg,
                    sourceDetails[table],
                    targetDetails[table],
                    tx,
                    ct
                );
                result.Deleted = deleted;

                if (!dryRun)
                    await tx!.CommitAsync(ct);

                Report(
                    new CopyProgress(
                        table,
                        CopyStage.Copying,
                        deleted,
                        dryRun
                            ? CoreStrings.FormatExtraRowsToDelete(table.ToString(), deleted)
                            : CoreStrings.FormatExtraRowsDeleted(table.ToString(), deleted)
                    )
                );
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                Report(
                    new CopyProgress(
                        table,
                        CopyStage.Failed,
                        0,
                        CoreStrings.FormatTableError(table.ToString(), ex.Message),
                        IsError: true
                    )
                );
                if (!_settings.ContinueOnTableError)
                    throw;
            }
        }
    }

    private async Task<long> DeleteExtraRowsAsync(
        TableCopyConfig cfg,
        DbTable src,
        DbTable tgt,
        IDbWriteTransaction? tx,
        CancellationToken ct
    )
    {
        if (cfg.MatchColumns.Count == 0)
            throw new InvalidOperationException(CoreStrings.NoMatchColumns);

        foreach (var column in cfg.MatchColumns)
        {
            EnsureColumnExists(src, column);
            EnsureColumnExists(tgt, column);
        }

        // Все ключи источника по полям сопоставления. Строки с NULL в любом поле исключаются:
        // такие строки всегда вставляются, а строки приёмника с NULL-ключами не сопоставляемы
        // и не удаляются (они могли появиться не из этой синхронизации).
        var sourceKeys = new HashSet<CompositeKey>();
        await foreach (var raw in _source.ReadRowsAsync(src.Name, cfg.MatchColumns, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (raw.All(v => v is not null))
                sourceKeys.Add(new CompositeKey(raw));
        }

        // Целевые ключи из словаря сопоставления; значение колонки сопоставления для определения
        // «лишних» не нужно, но метод требует её (она же используется при копировании).
        var identity = tgt.IdentityColumn is not null
            ? tgt.GetColumn(tgt.IdentityColumn)!.Name
            : null;
        var mappedColumn =
            identity ?? tgt.PrimaryKeyColumns.FirstOrDefault() ?? cfg.MatchColumns[0];
        var matchMap = await _target.LoadMatchMapAsync(
            src.Name,
            cfg.MatchColumns,
            mappedColumn,
            tx,
            ct
        );

        var extra = matchMap.Keys
            .OfType<CompositeKey>()
            .Where(k => !sourceKeys.Contains(k))
            .ToList();
        if (extra.Count == 0)
            return 0;

        if (tx is not null)
            await _target.DeleteRowsAsync(
                tgt.Name,
                cfg.MatchColumns,
                extra.Select(k => k.Values.ToArray()).ToList(),
                tx,
                ct
            );

        return extra.Count;
    }

    private async Task CopyTableAsync(
        TableCopyConfig cfg,
        DbTable src,
        DbTable tgt,
        IReadOnlySet<DbObjectName> selected,
        IReadOnlyDictionary<DbObjectName, HashSet<string>> referencedColumnsByTable,
        IDbWriteTransaction? tx,
        TableCopyResult result,
        CancellationToken ct
    )
    {
        var dryRun = _settings.DryRun;

        if (cfg.MatchColumns.Count == 0)
            throw new InvalidOperationException(CoreStrings.NoMatchColumns);

        foreach (var column in cfg.MatchColumns)
        {
            EnsureColumnExists(src, column);
            EnsureColumnExists(tgt, column);
        }

        var identity = tgt.IdentityColumn is not null
            ? tgt.GetColumn(tgt.IdentityColumn)!.Name
            : null;

        // Пользователь выбрал identity-колонку приёмника полем сопоставления → значения identity
        // копируются из источника как есть (для приёмника включается SET IDENTITY_INSERT).
        var preserveIdentity =
            identity is not null
            && cfg.MatchColumns.Contains(identity, StringComparer.OrdinalIgnoreCase);

        // Колонки, которые копируем: общие для источника и приёмника, без вычисляемых; без identity
        // приёмника, если только identity не выбран полем сопоставления (тогда он вставляется как есть).
        var insertColumns = src
            .Columns.Where(c => !c.IsComputed)
            .Where(c =>
            {
                var target = tgt.GetColumn(c.Name);
                return target is { IsComputed: false } && (preserveIdentity || !target.IsIdentity);
            })
            .Select(c => c.Name)
            .ToList();

        if (insertColumns.Count == 0)
            throw new InvalidOperationException(CoreStrings.NoCommonColumns);

        // Оригинальное identity-значение читаем тоже — оно нужно для сопоставления ID
        // (а при preserveIdentity вставляется как есть).
        var readColumns = insertColumns.ToList();
        var identityIndex = -1;
        if (identity is not null)
        {
            identityIndex = readColumns.FindIndex(c =>
                string.Equals(c, identity, StringComparison.OrdinalIgnoreCase)
            );
            if (identityIndex < 0)
            {
                identityIndex = readColumns.Count;
                readColumns.Add(identity);
            }
        }

        var matchIndices = cfg.MatchColumns.Select(c => IndexOf(readColumns, c)).ToArray();
        if (matchIndices.Any(i => i < 0))
            throw new InvalidOperationException(
                CoreStrings.FormatMatchColumnNotInCopySet(cfg.MatchColumns)
            );

        // Обновляются все колонки, кроме полей сопоставления, первичного ключа и identity.
        var updateColumns = insertColumns
            .Where(c => !cfg.MatchColumns.Contains(c, StringComparer.OrdinalIgnoreCase))
            .Where(c => !tgt.PrimaryKeyColumns.Contains(c, StringComparer.OrdinalIgnoreCase))
            .ToList();

        // Колонка, значение которой попадает в сопоставление ID: identity, иначе первичный ключ, иначе первое поле сопоставления.
        var mappedColumn =
            identity ?? tgt.PrimaryKeyColumns.FirstOrDefault() ?? cfg.MatchColumns[0];
        var mappedColumnIsIdentity =
            identity is not null
            && string.Equals(mappedColumn, identity, StringComparison.OrdinalIgnoreCase);
        var mappedIndex = mappedColumnIsIdentity ? -1 : IndexOf(readColumns, mappedColumn);
        if (mappedIndex < 0 && !mappedColumnIsIdentity)
            throw new InvalidOperationException(
                CoreStrings.FormatMappingColumnNotFound(mappedColumn)
            );

        // Колонки этой таблицы, на которые ссылаются внешние ключи других выбранных таблиц.
        var referencedCols = new List<(string Column, int Index)>();
        if (referencedColumnsByTable.TryGetValue(src.Name, out var refSet))
        {
            foreach (var column in refSet)
            {
                var index = string.Equals(column, identity, StringComparison.OrdinalIgnoreCase)
                    ? identityIndex
                    : IndexOf(readColumns, column);
                if (index >= 0)
                    referencedCols.Add((column, index));
            }
        }

        // Подстановки внешних ключей (кроме самоссылающихся): индекс колонки в строке, родитель, колонка родителя.
        var substitutions =
            new List<(int ColumnIndex, DbObjectName ParentTable, string ParentColumn)>();
        foreach (var fk in src.ForeignKeys.Where(f => !f.IsSelfReferencing))
        {
            if (!selected.Contains(fk.ReferencedTable))
                continue;
            foreach (var pair in fk.ColumnPairs)
            {
                var index = IndexOf(readColumns, pair.ChildColumn);
                if (index >= 0)
                    substitutions.Add((index, fk.ReferencedTable, pair.ParentColumn));
            }
        }

        // Самоссылающиеся FK обрабатываются двухфазно: (индекс колонки, родительская колонка).
        var selfRef = new List<(int ColumnIndex, string ParentColumn)>();
        foreach (var fk in src.ForeignKeys.Where(f => f.IsSelfReferencing))
        {
            foreach (var pair in fk.ColumnPairs)
            {
                var index = IndexOf(readColumns, pair.ChildColumn);
                if (index >= 0)
                    selfRef.Add((index, pair.ParentColumn));
            }
        }

        Report(
            new CopyProgress(
                src.Name,
                CopyStage.LoadingTargetMap,
                0,
                CoreStrings.FormatLoadingMatchMap(cfg.MatchColumns)
            )
        );

        var matchMap = await _target.LoadMatchMapAsync(
            src.Name,
            cfg.MatchColumns,
            mappedColumn,
            tx,
            ct
        );

        // Фаза 2 для самоссылающихся внешних ключей: (новый ID строки, колонка FK, родительская колонка, исходное значение).
        var phase2 =
            new List<(
                object NewMappedId,
                string FkColumn,
                string ParentColumn,
                object? OrigValue
            )>();
        var batchSize = Math.Clamp(
            _settings.BatchSize,
            1,
            Math.Max(1, 1500 / Math.Max(1, insertColumns.Count))
        );

        // В режиме предпросмотра identity не назначается СУБД: имитируем новые ID отрицательными
        // числами (не пересекаются с реальными значениями), чтобы корректно разрешать FK дочерних таблиц.
        long dryRunNextId = -1;

        var pendingInserts = new List<PendingRow>(batchSize);
        var pendingUpdates = new List<PendingRow>(batchSize);
        long rowsRead = 0;
        long lastReport = 0;

        async Task FlushInsertsAsync()
        {
            if (pendingInserts.Count == 0)
                return;

            if (dryRun)
            {
                foreach (var pending in pendingInserts)
                {
                    var newMappedId = preserveIdentity
                        ? pending.Values[identityIndex]
                        : mappedColumnIsIdentity
                            ? dryRunNextId--
                            : pending.Values[mappedIndex];
                    if (newMappedId is null)
                        throw new InvalidOperationException(
                            CoreStrings.FormatNoNewId(src.Name.ToString())
                        );

                    RecordMappings(
                        src.Name,
                        pending.Values,
                        newMappedId,
                        referencedCols,
                        identityIndex,
                        result
                    );
                    result.Inserted++;
                }

                pendingInserts.Clear();
                return;
            }

            // При копировании identity как есть включаем вставку явных значений на время вставки.
            if (preserveIdentity)
                await _target.SetIdentityInsertAsync(tgt.Name, true, tx, ct);
            try
            {
                var newIds = await _target.InsertRowsAsync(
                    tgt,
                    insertColumns,
                    pendingInserts.Select(p => p.Values).ToList(),
                    tx,
                    ct
                );

                for (var i = 0; i < pendingInserts.Count; i++)
                {
                    var pending = pendingInserts[i];
                    var newMappedId = preserveIdentity
                        ? pending.Values[identityIndex]
                        : mappedColumnIsIdentity
                            ? newIds[i]
                            : pending.Values[mappedIndex];
                    if (newMappedId is null)
                        throw new InvalidOperationException(
                            CoreStrings.FormatNoNewId(src.Name.ToString())
                        );

                    RecordMappings(
                        src.Name,
                        pending.Values,
                        newMappedId,
                        referencedCols,
                        identityIndex,
                        result
                    );
                    foreach (var (fkColumn, parentColumn, origValue) in pending.SelfRefEntries)
                        phase2.Add((newMappedId, fkColumn, parentColumn, origValue));
                    result.Inserted++;
                }
            }
            finally
            {
                if (preserveIdentity)
                    await _target.SetIdentityInsertAsync(tgt.Name, false, tx, ct);
            }

            pendingInserts.Clear();
        }

        async Task FlushUpdatesAsync()
        {
            if (pendingUpdates.Count == 0)
                return;

            if (!dryRun)
            {
                await _target.UpdateRowsAsync(
                    tgt,
                    cfg.MatchColumns,
                    readColumns,
                    updateColumns,
                    pendingUpdates.Select(p => p.Values).ToList(),
                    tx,
                    ct
                );
            }

            for (var i = 0; i < pendingUpdates.Count; i++)
            {
                var pending = pendingUpdates[i];
                var key = new CompositeKey(matchIndices.Select(idx => pending.Values[idx]));
                if (!matchMap.TryGetValue(key, out var newMappedId))
                    throw new InvalidOperationException(
                        CoreStrings.FormatTargetIdNotFound(cfg.MatchColumns, key.ToString())
                    );

                RecordMappings(
                    src.Name,
                    pending.Values,
                    newMappedId,
                    referencedCols,
                    identityIndex,
                    result
                );
                if (!dryRun)
                {
                    foreach (var (fkColumn, parentColumn, origValue) in pending.SelfRefEntries)
                        phase2.Add((newMappedId, fkColumn, parentColumn, origValue));
                }
                result.Updated++;
            }

            pendingUpdates.Clear();
        }

        await foreach (var raw in _source.ReadRowsAsync(src.Name, readColumns, ct))
        {
            ct.ThrowIfCancellationRequested();
            rowsRead++;
            result.RowsRead++;

            // Подстановка новых ID во внешние ключи (родители уже обработаны).
            foreach (var (columnIndex, parentTable, parentColumn) in substitutions)
            {
                var value = raw[columnIndex];
                if (value is null)
                    continue;

                if (!_mappings.TryGet(parentTable, parentColumn, value, out var mapped))
                    throw new InvalidOperationException(
                        CoreStrings.FormatNoIdMapping(
                            src.Name.ToString(),
                            readColumns[columnIndex],
                            parentTable.ToString(),
                            parentColumn,
                            value.ToString() ?? ""
                        )
                    );

                raw[columnIndex] = mapped;
            }

            // Самоссылающиеся FK: подставляем, если родитель уже обработан, иначе NULL + запись для фазы 2.
            var selfRefEntries =
                new List<(string FkColumn, string ParentColumn, object? OrigValue)>();
            foreach (var (columnIndex, parentColumn) in selfRef)
            {
                var value = raw[columnIndex];
                if (value is null)
                    continue;

                selfRefEntries.Add((readColumns[columnIndex], parentColumn, value));
                if (_mappings.TryGet(src.Name, parentColumn, value, out var mapped))
                    raw[columnIndex] = mapped;
                else
                    raw[columnIndex] = null;
            }

            var keyValues = matchIndices.Select(idx => raw[idx]).ToArray();
            var isMatch =
                keyValues.All(v => v is not null)
                && matchMap.ContainsKey(new CompositeKey(keyValues));
            var pending = isMatch ? pendingUpdates : pendingInserts;
            pending.Add(new PendingRow(raw, selfRefEntries));

            if (pendingInserts.Count >= batchSize)
                await FlushInsertsAsync();
            if (pendingUpdates.Count >= batchSize)
                await FlushUpdatesAsync();

            if (rowsRead - lastReport >= 5000)
            {
                Report(
                    new CopyProgress(
                        src.Name,
                        CopyStage.Copying,
                        rowsRead,
                        CoreStrings.FormatRowsProcessed(rowsRead)
                    )
                );
                lastReport = rowsRead;
            }
        }

        await FlushInsertsAsync();
        await FlushUpdatesAsync();

        // Фаза 2: устанавливаем самоссылающиеся внешние ключи по завершённому сопоставлению.
        if (dryRun)
            return;

        foreach (var group in phase2.GroupBy(p => p.FkColumn))
        {
            var pairs = new List<(object? SetValue, object? WhereValue)>();
            foreach (var (newMappedId, _, parentColumn, origValue) in group)
            {
                if (origValue is null)
                    continue;
                if (_mappings.TryGet(src.Name, parentColumn, origValue, out var mapped))
                    pairs.Add((mapped, newMappedId));
            }

            if (pairs.Count > 0)
                await _target.ExecuteUpdatesAsync(tgt, group.Key, mappedColumn, pairs, tx, ct);
        }

        // После копирования выравниваем счётчик identity приёмника со счётчиком источника,
        // чтобы последующие вставки в приёмнике продолжали нумерацию источника.
        if (preserveIdentity)
        {
            var sourceIdentity = await _source.GetIdentityCurrentAsync(src.Name, ct);
            await _target.ReseedIdentityAsync(src.Name, sourceIdentity, tx, ct);
        }
    }

    private static Dictionary<DbObjectName, HashSet<string>> ComputeReferencedColumns(
        IReadOnlySet<DbObjectName> selected,
        IReadOnlyDictionary<DbObjectName, DbTable> details
    )
    {
        var map = new Dictionary<DbObjectName, HashSet<string>>();

        foreach (var table in details.Values)
        {
            foreach (var fk in table.ForeignKeys)
            {
                if (!selected.Contains(fk.ReferencedTable))
                    continue;

                if (!map.TryGetValue(fk.ReferencedTable, out var columns))
                {
                    columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    map[fk.ReferencedTable] = columns;
                }

                foreach (var pair in fk.ColumnPairs)
                    columns.Add(pair.ParentColumn);
            }
        }

        return map;
    }

    private void RecordMappings(
        DbObjectName table,
        object?[] row,
        object newMappedId,
        IReadOnlyList<(string Column, int Index)> referencedCols,
        int identityIndex,
        TableCopyResult result
    )
    {
        foreach (var (column, index) in referencedCols)
        {
            var original = row[index];
            if (original is null)
                continue;

            var mapped = index == identityIndex ? newMappedId : original;
            _mappings.Add(table, column, original, mapped);
            result.Mapped++;
        }
    }

    private void Report(CopyProgress progress) => _progress?.Report(progress);

    private static void EnsureColumnExists(DbTable table, string column)
    {
        if (table.GetColumn(column) is null)
            throw new InvalidOperationException(
                CoreStrings.FormatColumnNotFound(column, table.Name.ToString())
            );
    }

    private static int IndexOf(IReadOnlyList<string> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++)
            if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private sealed record PendingRow(
        object?[] Values,
        List<(string FkColumn, string ParentColumn, object? OrigValue)> SelfRefEntries
    );
}

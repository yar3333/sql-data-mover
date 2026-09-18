using System.Data;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlDataMover.Core.Abstractions;
using SqlDataMover.Core.Localization;
using SqlDataMover.Core.Models;

namespace SqlDataMover.Core.Providers;

/// <summary>Реализация <see cref="IDbProvider"/> для MS SQL Server.</summary>
public sealed class SqlServerProvider : IDbProvider
{
    private readonly string _connectionString;
    private readonly SqlConnection _connection;

    public string Name => "MS SQL Server";

    public bool IsConnected => _connection.State == ConnectionState.Open;

    public SqlServerProvider(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        _connectionString = NormalizeConnectionString(connectionString);
        _connection = new SqlConnection(_connectionString);
    }

    /// <summary>Дополняет строку подключения значениями по умолчанию, если они не заданы явно.</summary>
    internal static string NormalizeConnectionString(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        // Для локальных серверов без настроенного TLS не отключаемся с ошибкой: пробуем шифрование, если возможно.
        if (!connectionString.Contains("Encrypt", StringComparison.OrdinalIgnoreCase))
            builder.Encrypt = SqlConnectionEncryptOption.Optional;
        if (
            !connectionString.Contains("TrustServerCertificate", StringComparison.OrdinalIgnoreCase)
        )
            builder.TrustServerCertificate = true;
        // По умолчанию используется Windows-аутентификация: Trusted_Connection считается
        // true, если в строке не заданы ни integrated security, ни SQL-логин (User ID/Password).
        var hasAuth =
            builder.ShouldSerialize("Integrated Security")
            || !string.IsNullOrEmpty(builder.UserID)
            || !string.IsNullOrEmpty(builder.Password);
        if (!hasAuth)
            builder.IntegratedSecurity = true;

        return builder.ConnectionString;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (!IsConnected)
            await _connection.OpenAsync(ct);
    }

    public async Task<IReadOnlyList<DbTable>> GetTablesAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT s.name AS SchemaName, t.name AS TableName, SUM(p.rows) AS [RowCount]
            FROM sys.tables t
            JOIN sys.schemas s ON t.schema_id = s.schema_id
            JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
            GROUP BY s.name, t.name
            ORDER BY s.name, t.name
            """;

        var tables = new List<DbTable>();
        await using var cmd = new SqlCommand(sql, _connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            tables.Add(
                new DbTable
                {
                    Name = new DbObjectName(reader.GetString(0), reader.GetString(1)),
                    RowCount = reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                }
            );
        }

        return tables;
    }

    public async Task<DbTable> GetTableDetailsAsync(
        DbObjectName table,
        CancellationToken ct = default
    )
    {
        const string columnsSql = """
            SELECT c.column_id, c.name AS ColumnName, ty.name AS DataType,
                   c.is_nullable, c.is_identity, c.is_computed,
                   CASE WHEN pk.column_id IS NULL THEN 0 ELSE 1 END AS IsPrimaryKey
            FROM sys.columns c
            JOIN sys.types ty ON c.user_type_id = ty.user_type_id
            LEFT JOIN (
                SELECT ic.object_id, ic.column_id
                FROM sys.index_columns ic
                JOIN sys.key_constraints kc
                     ON kc.parent_object_id = ic.object_id
                    AND kc.unique_index_id = ic.index_id
                    AND kc.type = 'PK'
            ) pk ON pk.object_id = c.object_id AND pk.column_id = c.column_id
            WHERE c.object_id = OBJECT_ID(@name)
            ORDER BY c.column_id
            """;

        var columns = new List<DbColumn>();
        string? identity = null;
        var primaryKey = new List<string>();

        await using (var cmd = new SqlCommand(columnsSql, _connection))
        {
            cmd.Parameters.AddWithValue("@name", table.ToString());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var column = new DbColumn
                {
                    Ordinal = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    DataType = reader.GetString(2),
                    IsNullable = reader.GetBoolean(3),
                    IsIdentity = reader.GetBoolean(4),
                    IsComputed = reader.GetBoolean(5),
                    IsPrimaryKey = reader.GetInt32(6) == 1,
                };

                if (column.IsIdentity)
                    identity = column.Name;
                if (column.IsPrimaryKey)
                    primaryKey.Add(column.Name);
                columns.Add(column);
            }
        }

        if (columns.Count == 0)
            throw new InvalidOperationException(CoreStrings.FormatTableNotFound(table.ToString()));

        var foreignKeys = await LoadForeignKeysAsync(table, ct);

        return new DbTable
        {
            Name = table,
            Columns = columns,
            PrimaryKeyColumns = primaryKey,
            IdentityColumn = identity,
            ForeignKeys = foreignKeys,
        };
    }

    private async Task<IReadOnlyList<DbForeignKey>> LoadForeignKeysAsync(
        DbObjectName table,
        CancellationToken ct
    )
    {
        const string sql = """
            SELECT fk.name AS FKName,
                   OBJECT_SCHEMA_NAME(fk.referenced_object_id) AS ParentSchema,
                   OBJECT_NAME(fk.referenced_object_id) AS ParentTable,
                   pc.name AS ChildColumn,
                   rc.name AS ParentColumn
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.columns pc ON pc.object_id = fk.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN sys.columns rc ON rc.object_id = fk.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            WHERE fk.parent_object_id = OBJECT_ID(@name)
            ORDER BY fk.name, fkc.constraint_column_id
            """;

        var raw =
            new List<(
                string FkName,
                string ParentSchema,
                string ParentTable,
                string ChildColumn,
                string ParentColumn
            )>();

        await using (var cmd = new SqlCommand(sql, _connection))
        {
            cmd.Parameters.AddWithValue("@name", table.ToString());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                raw.Add(
                    (
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4)
                    )
                );
            }
        }

        var result = new List<DbForeignKey>();
        foreach (var group in raw.GroupBy(r => r.FkName))
        {
            result.Add(
                new DbForeignKey
                {
                    Name = group.Key,
                    ReferencingTable = table,
                    ReferencedTable = new DbObjectName(
                        group.First().ParentSchema,
                        group.First().ParentTable
                    ),
                    ColumnPairs = group
                        .Select(r => new ForeignKeyColumnPair(r.ChildColumn, r.ParentColumn))
                        .ToList(),
                }
            );
        }

        return result;
    }

    public async IAsyncEnumerable<object?[]> ReadRowsAsync(
        DbObjectName table,
        IReadOnlyList<string> columns,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        var sql = $"SELECT {string.Join(", ", columns.Select(Quote))} FROM {QuoteTable(table)}";
        await using var cmd = new SqlCommand(sql, _connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var buffer = new object[reader.FieldCount];

        while (await reader.ReadAsync(ct))
        {
            reader.GetValues(buffer);
            var row = new object?[buffer.Length];
            for (var i = 0; i < buffer.Length; i++)
                row[i] = buffer[i] is DBNull ? null : buffer[i];
            yield return row;
        }
    }

    public async Task<Dictionary<object, object>> LoadMatchMapAsync(
        DbObjectName table,
        IReadOnlyList<string> matchColumns,
        string mappedColumn,
        IDbWriteTransaction? transaction = null,
        CancellationToken ct = default
    )
    {
        var map = new Dictionary<object, object>();
        var where = string.Join(" AND ", matchColumns.Select(c => $"{Quote(c)} IS NOT NULL"));
        var select = string.Join(", ", matchColumns.Select(Quote).Append(Quote(mappedColumn)));
        var sql = $"SELECT {select} FROM {QuoteTable(table)} WHERE {where}";

        await using var cmd = new SqlCommand(sql, _connection)
        {
            Transaction = GetSqlTransaction(transaction),
        };
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var keyValues = new object?[matchColumns.Count];
            var isNull = false;
            for (var i = 0; i < matchColumns.Count; i++)
            {
                var value = reader.GetValue(i);
                if (value is DBNull)
                {
                    isNull = true;
                    break;
                }
                keyValues[i] = value;
            }

            if (isNull)
                continue;

            var mapped = reader.GetValue(matchColumns.Count);
            map[new CompositeKey(keyValues)] = mapped is DBNull ? null! : mapped;
        }

        return map;
    }

    public Task<IDbWriteTransaction> BeginTransactionAsync(CancellationToken ct = default)
    {
        var tx = _connection.BeginTransaction();
        return Task.FromResult<IDbWriteTransaction>(new SqlWriteTransaction(tx));
    }

    public async Task<IReadOnlyList<object?>> InsertRowsAsync(
        DbTable table,
        IReadOnlyList<string> columns,
        IReadOnlyList<object?[]> rows,
        IDbWriteTransaction? transaction = null,
        CancellationToken ct = default
    )
    {
        if (rows.Count == 0)
            return [];

        var hasIdentity = table.IdentityColumn is not null;
        var sql = new StringBuilder();
        sql.Append("INSERT INTO ").Append(QuoteTable(table.Name)).Append(" (");
        sql.Append(string.Join(", ", columns.Select(Quote)));
        sql.Append(')');
        if (hasIdentity)
            sql.Append(" OUTPUT INSERTED.").Append(Quote(table.IdentityColumn!));
        sql.Append(" VALUES ");

        await using var cmd = new SqlCommand
        {
            Connection = _connection,
            Transaction = GetSqlTransaction(transaction),
        };

        var parameterIndex = 0;
        for (var r = 0; r < rows.Count; r++)
        {
            if (r > 0)
                sql.Append(", ");
            sql.Append('(');
            for (var c = 0; c < columns.Count; c++)
            {
                if (c > 0)
                    sql.Append(", ");
                var name = $"@p{parameterIndex}";
                sql.Append(name);
                cmd.Parameters.AddWithValue(name, rows[r][c] ?? DBNull.Value);
                parameterIndex++;
            }
            sql.Append(')');
        }

        cmd.CommandText = sql.ToString();

        if (!hasIdentity)
        {
            await cmd.ExecuteNonQueryAsync(ct);
            return Enumerable.Repeat<object?>(null, rows.Count).ToArray();
        }

        var newIds = new List<object?>(rows.Count);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var value = reader.GetValue(0);
            newIds.Add(value is DBNull ? null : value);
        }

        return newIds;
    }

    public async Task<int> UpdateRowsAsync(
        DbTable table,
        IReadOnlyList<string> matchColumns,
        IReadOnlyList<string> allColumns,
        IReadOnlyList<string> updateColumns,
        IReadOnlyList<object?[]> rows,
        IDbWriteTransaction? transaction = null,
        CancellationToken ct = default
    )
    {
        if (rows.Count == 0 || updateColumns.Count == 0)
            return 0;

        var matchIndices = matchColumns.Select(c => IndexOf(allColumns, c)).ToArray();
        if (matchIndices.Any(i => i < 0))
            throw new InvalidOperationException(CoreStrings.MatchColumnMissing);

        var updateIndices = updateColumns.Select(c => IndexOf(allColumns, c)).ToArray();
        if (updateIndices.Any(i => i < 0))
            throw new InvalidOperationException(CoreStrings.UpdateColumnMissing);

        var sql = new StringBuilder();
        await using var cmd = new SqlCommand
        {
            Connection = _connection,
            Transaction = GetSqlTransaction(transaction),
        };

        var parameterIndex = 0;
        for (var r = 0; r < rows.Count; r++)
        {
            sql.Append("UPDATE ").Append(QuoteTable(table.Name)).Append(" SET ");
            for (var c = 0; c < updateColumns.Count; c++)
            {
                if (c > 0)
                    sql.Append(", ");
                var name = $"@p{parameterIndex}";
                sql.Append(Quote(updateColumns[c])).Append(" = ").Append(name);
                cmd.Parameters.AddWithValue(name, rows[r][updateIndices[c]] ?? DBNull.Value);
                parameterIndex++;
            }
            sql.Append(" WHERE ");
            for (var i = 0; i < matchColumns.Count; i++)
            {
                if (i > 0)
                    sql.Append(" AND ");
                var name = $"@p{parameterIndex}";
                sql.Append(Quote(matchColumns[i])).Append(" = ").Append(name);
                cmd.Parameters.AddWithValue(name, rows[r][matchIndices[i]] ?? DBNull.Value);
                parameterIndex++;
            }
            if (r < rows.Count - 1)
                sql.Append("; ");
        }

        cmd.CommandText = sql.ToString();
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> DeleteRowsAsync(
        DbObjectName table,
        IReadOnlyList<string> whereColumns,
        IReadOnlyList<object?[]> keys,
        IDbWriteTransaction? transaction = null,
        CancellationToken ct = default
    )
    {
        if (keys.Count == 0 || whereColumns.Count == 0)
            return 0;

        var deleted = 0;
        // Ограничение SQL Server: не более 2100 параметров на запрос; берём запас до 1500.
        var batchSize = Math.Max(1, 1500 / whereColumns.Count);
        await using var cmd = new SqlCommand
        {
            Connection = _connection,
            Transaction = GetSqlTransaction(transaction),
        };

        for (var offset = 0; offset < keys.Count; offset += batchSize)
        {
            var batch = keys.Skip(offset).Take(batchSize).ToList();
            cmd.Parameters.Clear();

            var sql = new StringBuilder("DELETE FROM ").Append(QuoteTable(table)).Append(" WHERE ");
            var parameterIndex = 0;
            for (var r = 0; r < batch.Count; r++)
            {
                if (r > 0)
                    sql.Append(" OR ");
                sql.Append('(');
                for (var c = 0; c < whereColumns.Count; c++)
                {
                    if (c > 0)
                        sql.Append(" AND ");
                    var name = $"@p{parameterIndex}";
                    sql.Append(Quote(whereColumns[c])).Append(" = ").Append(name);
                    cmd.Parameters.AddWithValue(name, batch[r][c] ?? DBNull.Value);
                    parameterIndex++;
                }
                sql.Append(')');
            }

            cmd.CommandText = sql.ToString();
            deleted += await cmd.ExecuteNonQueryAsync(ct);
        }

        return deleted;
    }

    public async Task<int> ExecuteUpdatesAsync(
        DbTable table,
        string setColumn,
        string whereColumn,
        IReadOnlyList<(object? SetValue, object? WhereValue)> pairs,
        IDbWriteTransaction? transaction = null,
        CancellationToken ct = default
    )
    {
        if (pairs.Count == 0)
            return 0;

        var sql = new StringBuilder();
        await using var cmd = new SqlCommand
        {
            Connection = _connection,
            Transaction = GetSqlTransaction(transaction),
        };

        var parameterIndex = 0;
        for (var i = 0; i < pairs.Count; i++)
        {
            var setValueName = $"@p{parameterIndex}";
            sql.Append("UPDATE ")
                .Append(QuoteTable(table.Name))
                .Append(" SET ")
                .Append(Quote(setColumn))
                .Append(" = ")
                .Append(setValueName);
            cmd.Parameters.AddWithValue(setValueName, pairs[i].SetValue ?? DBNull.Value);
            parameterIndex++;

            var whereValueName = $"@p{parameterIndex}";
            sql.Append(" WHERE ").Append(Quote(whereColumn)).Append(" = ").Append(whereValueName);
            cmd.Parameters.AddWithValue(whereValueName, pairs[i].WhereValue ?? DBNull.Value);
            parameterIndex++;
            if (i < pairs.Count - 1)
                sql.Append("; ");
        }

        cmd.CommandText = sql.ToString();
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetIdentityInsertAsync(
        DbObjectName table,
        bool enabled,
        IDbWriteTransaction? transaction = null,
        CancellationToken ct = default
    )
    {
        await using var cmd = new SqlCommand
        {
            Connection = _connection,
            Transaction = GetSqlTransaction(transaction),
            CommandText = $"SET IDENTITY_INSERT {QuoteTable(table)} {(enabled ? "ON" : "OFF")}",
        };
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> GetIdentityCurrentAsync(
        DbObjectName table,
        CancellationToken ct = default
    )
    {
        await using var cmd = new SqlCommand
        {
            Connection = _connection,
            CommandText = "SELECT IDENT_CURRENT(@table)",
        };
        cmd.Parameters.AddWithValue("@table", table.ToString());
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }

    public async Task<IReadOnlyList<string>> GetUniqueIndexesAsync(
        DbObjectName table,
        CancellationToken ct = default
    )
    {
        const string sql = """
            SELECT i.name
            FROM sys.indexes i
            WHERE i.object_id = OBJECT_ID(@name)
              AND i.is_unique = 1
              AND i.is_primary_key = 0
              AND i.name IS NOT NULL
            ORDER BY i.name
            """;

        var names = new List<string>();
        await using var cmd = new SqlCommand(sql, _connection);
        cmd.Parameters.AddWithValue("@name", table.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            names.Add(reader.GetString(0));

        return names;
    }

    public async Task SetUniqueIndexEnabledAsync(
        DbObjectName table,
        string indexName,
        bool enabled,
        IDbWriteTransaction? transaction = null,
        CancellationToken ct = default
    )
    {
        // DISABLE снимает проверку уникальности на время копирования; REBUILD пересоздаёт
        // индекс и перепроверяет данные (транзиентных дублей уже нет, реальные — уронят транзакцию).
        await using var cmd = new SqlCommand
        {
            Connection = _connection,
            Transaction = GetSqlTransaction(transaction),
            CommandText = enabled
                ? $"ALTER INDEX {Quote(indexName)} ON {QuoteTable(table)} REBUILD"
                : $"ALTER INDEX {Quote(indexName)} ON {QuoteTable(table)} DISABLE",
        };
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ReseedIdentityAsync(
        DbObjectName table,
        long newValue,
        IDbWriteTransaction? transaction = null,
        CancellationToken ct = default
    )
    {
        await using var cmd = new SqlCommand
        {
            Connection = _connection,
            Transaction = GetSqlTransaction(transaction),
        };
        cmd.Parameters.AddWithValue("@table", table.ToString());
        cmd.Parameters.AddWithValue("@seed", newValue);
        cmd.CommandText = "DBCC CHECKIDENT (@table, RESEED, @seed) WITH NO_INFOMSGS";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public ValueTask DisposeAsync()
    {
        _connection.Dispose();
        return ValueTask.CompletedTask;
    }

    private static SqlTransaction? GetSqlTransaction(IDbWriteTransaction? transaction) =>
        (transaction as SqlWriteTransaction)?.Transaction;

    private static int IndexOf(IReadOnlyList<string> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++)
            if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    internal static string Quote(string identifier) => "[" + identifier.Replace("]", "]]") + "]";

    private static string QuoteTable(DbObjectName name) =>
        $"[{name.Schema.Replace("]", "]]")}].[{name.Name.Replace("]", "]]")}]";

    private sealed class SqlWriteTransaction : IDbWriteTransaction
    {
        private SqlTransaction? _tx;

        public SqlTransaction Transaction =>
            _tx ?? throw new ObjectDisposedException(nameof(SqlWriteTransaction));

        public SqlWriteTransaction(SqlTransaction tx) => _tx = tx;

        public Task CommitAsync(CancellationToken ct = default)
        {
            _tx?.Commit();
            _tx = null;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (_tx is { } tx)
            {
                _tx = null;
                try
                {
                    tx.Rollback();
                }
                catch (SqlException)
                { /* уже закоммичено или соединение закрыто */
                }
            }
            return ValueTask.CompletedTask;
        }
    }
}

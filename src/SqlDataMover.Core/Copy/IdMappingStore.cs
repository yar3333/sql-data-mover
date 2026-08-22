using SqlDataMover.Core.Models;

namespace SqlDataMover.Core.Copy;

/// <summary>
/// Хранит соответствие «оригинальный ID → новый ID» для колонок, на которые ссылаются внешние ключи.
/// Ключ: таблица → колонка → словарь значений.
/// </summary>
public sealed class IdMappingStore
{
    private readonly Dictionary<DbObjectName, Dictionary<string, Dictionary<object, object>>> _byTable = [];

    public void Add(DbObjectName table, string column, object original, object mapped)
    {
        if (!_byTable.TryGetValue(table, out var byColumn))
        {
            byColumn = new Dictionary<string, Dictionary<object, object>>(StringComparer.OrdinalIgnoreCase);
            _byTable[table] = byColumn;
        }

        if (!byColumn.TryGetValue(column, out var map))
        {
            map = new Dictionary<object, object>(ObjectKeyComparer.Instance);
            byColumn[column] = map;
        }

        map[original] = mapped;
    }

    public bool TryGet(DbObjectName table, string column, object? value, out object? mapped)
    {
        mapped = null;
        if (value is null) return false;

        return _byTable.TryGetValue(table, out var byColumn)
            && byColumn.TryGetValue(column, out var map)
            && map.TryGetValue(value, out mapped);
    }
}

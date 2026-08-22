using SqlDataMover.Core.Models;

namespace SqlDataMover.Core.Copy;

public static class TableDependencySorter
{
    /// <summary>
    /// Топологическая сортировка таблиц по внешним ключам: родители раньше детей.
    /// Самоссылающиеся внешние ключи игнорируются (обрабатываются движком отдельно);
    /// ссылки на таблицы вне набора тоже игнорируются (предполагается, что данные там уже есть).
    /// </summary>
    /// <exception cref="InvalidOperationException">При наличии цикла зависимостей.</exception>
    public static IReadOnlyList<DbObjectName> Sort(IReadOnlyCollection<DbTable> tables)
    {
        var selected = tables.Select(t => t.Name).ToHashSet();
        var dependents = new Dictionary<DbObjectName, HashSet<DbObjectName>>();
        var indegree = tables.ToDictionary(t => t.Name, _ => 0);

        foreach (var table in tables)
        {
            foreach (var fk in table.ForeignKeys)
            {
                if (fk.IsSelfReferencing || !selected.Contains(fk.ReferencedTable))
                    continue;

                if (!dependents.TryGetValue(fk.ReferencedTable, out var children))
                {
                    children = [];
                    dependents[fk.ReferencedTable] = children;
                }

                if (children.Add(table.Name))
                    indegree[table.Name]++;
            }
        }

        var queue = new Queue<DbObjectName>(
            tables.Where(t => indegree[t.Name] == 0)
                  .Select(t => t.Name)
                  .OrderBy(n => n.ToString(), StringComparer.OrdinalIgnoreCase));

        var order = new List<DbObjectName>(tables.Count);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            order.Add(node);

            if (!dependents.TryGetValue(node, out var children))
                continue;

            foreach (var child in children)
                if (--indegree[child] == 0)
                    queue.Enqueue(child);
        }

        if (order.Count != selected.Count)
        {
            var cycle = string.Join(", ", selected.Except(order).Select(n => n.ToString()));
            throw new InvalidOperationException($"Обнаружена циклическая зависимость между таблицами: {cycle}");
        }

        return order;
    }
}

namespace SqlDataMover.Core.Models;

/// <summary>Пара колонок внешнего ключа: колонка дочерней таблицы → колонка родительской таблицы.</summary>
public sealed record ForeignKeyColumnPair(string ChildColumn, string ParentColumn);

/// <summary>Внешний ключ таблицы.</summary>
public sealed class DbForeignKey
{
    public required string Name { get; init; }
    public required DbObjectName ReferencingTable { get; init; }
    public required DbObjectName ReferencedTable { get; init; }
    public List<ForeignKeyColumnPair> ColumnPairs { get; init; } = [];

    public bool IsSelfReferencing => ReferencingTable == ReferencedTable;
}

namespace SqlDataMover.Core.Models;

/// <summary>Метаданные таблицы.</summary>
public sealed class DbTable
{
    public required DbObjectName Name { get; init; }
    public IReadOnlyList<DbColumn> Columns { get; init; } = [];
    public IReadOnlyList<string> PrimaryKeyColumns { get; init; } = [];
    public string? IdentityColumn { get; init; }
    public IReadOnlyList<DbForeignKey> ForeignKeys { get; init; } = [];
    public long RowCount { get; init; }

    public DbColumn? GetColumn(string name) =>
        Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

namespace SqlDataMover.Core.Models;

/// <summary>Метаданные колонки таблицы.</summary>
public sealed class DbColumn
{
    public required string Name { get; init; }
    public required string DataType { get; init; }
    public bool IsNullable { get; init; }
    public bool IsIdentity { get; init; }
    public bool IsComputed { get; init; }
    public bool IsPrimaryKey { get; init; }
    public int Ordinal { get; init; }
}

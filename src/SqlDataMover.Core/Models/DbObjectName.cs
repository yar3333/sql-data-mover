namespace SqlDataMover.Core.Models;

/// <summary>Имя объекта базы данных: схема + имя.</summary>
public sealed record DbObjectName(string Schema, string Name)
{
    public override string ToString() => $"{Schema}.{Name}";
}

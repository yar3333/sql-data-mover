using SqlDataMover.Core.Models;

namespace SqlDataMover.Core.Copy;

/// <summary>Конфигурация копирования одной таблицы.</summary>
public sealed class TableCopyConfig
{
    public required DbObjectName Table { get; init; }

    /// <summary>
    /// Уникальное поле, по которому строки источника сопоставляются с уже существующими строками
    /// приёмника. Может не совпадать с первичным ключом.
    /// </summary>
    public required string UniqueColumn { get; init; }
}

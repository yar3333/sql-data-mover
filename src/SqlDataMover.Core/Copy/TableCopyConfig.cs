using SqlDataMover.Core.Models;

namespace SqlDataMover.Core.Copy;

/// <summary>Конфигурация копирования одной таблицы.</summary>
public sealed class TableCopyConfig
{
    public required DbObjectName Table { get; init; }

    /// <summary>
    /// Поля сопоставления: комбинация их значений образует ключ, по которому строки источника
    /// сопоставляются с уже существующими строками приёмника. Может не совпадать с первичным ключом.
    /// Строка считается совпавшей, только если все компоненты ключа не NULL и найдены в приёмнике.
    /// </summary>
    public required IReadOnlyList<string> MatchColumns { get; init; }
}

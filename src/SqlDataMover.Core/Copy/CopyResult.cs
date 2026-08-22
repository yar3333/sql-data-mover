using SqlDataMover.Core.Models;

namespace SqlDataMover.Core.Copy;

/// <summary>Результат копирования одной таблицы.</summary>
public sealed class TableCopyResult
{
    public required DbObjectName Table { get; init; }
    public long RowsRead { get; set; }
    public long Inserted { get; set; }
    public long Updated { get; set; }

    /// <summary>Количество записанных сопоставлений «оригинальный ID → новый ID».</summary>
    public long Mapped { get; set; }
    public string? Error { get; set; }
    public bool Success => Error is null;
}

/// <summary>Итоговый результат копирования.</summary>
public sealed class CopyResult
{
    public IReadOnlyList<TableCopyResult> Tables { get; init; } = [];
    public TimeSpan Elapsed { get; init; }
    public bool Success => Tables.All(t => t.Success);
}

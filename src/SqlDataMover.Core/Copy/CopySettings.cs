namespace SqlDataMover.Core.Copy;

public sealed class CopySettings
{
    /// <summary>Максимальный размер пакета строк (ограничивается 1500 параметрами SQL).</summary>
    public int BatchSize { get; init; } = 500;

    /// <summary>Продолжать копирование остальных таблиц при ошибке в одной таблице.</summary>
    public bool ContinueOnTableError { get; init; } = true;

    /// <summary>
    /// Режим предпросмотра: строки читаются и сопоставляются как при обычном копировании,
    /// но запись в приёмник не выполняется. Результат показывает, сколько строк в каждой
    /// таблице было бы вставлено и обновлено.
    /// </summary>
    public bool DryRun { get; init; }
}

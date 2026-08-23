namespace SqlDataMover.Core.Localization;

/// <summary>
/// User-facing messages of the copy engine and database providers.
/// English is the default; Russian is optional and is enabled by setting <see cref="Language"/>.
/// </summary>
public static class CoreStrings
{
    /// <summary>Current UI language: "en" (default) or "ru".</summary>
    public static string Language { get; set; } = "en";

    private static string T(string en, string ru) => Language == "ru" ? ru : en;

    /// <summary>Joins values with the quoting style of the current language.</summary>
    private static string JoinQuoted(IEnumerable<string> values) =>
        Language == "ru"
            ? string.Join("», «", values)
            : string.Join("\", \"", values);

    // ---- DataCopyEngine: progress ----

    public static string LoadingMetadata =>
        T("Loading table metadata...", "Загрузка метаданных таблиц...");

    public static string FormatPreviewTable(string table) =>
        string.Format(T("Previewing table {0}...", "Предпросмотр таблицы {0}..."), table);

    public static string FormatCopyingTable(string table) =>
        string.Format(T("Copying table {0}...", "Копирование таблицы {0}..."), table);

    public static string FormatPreviewSummary(long read, long insert, long update) =>
        string.Format(
            T(
                "Preview: {0:N0} rows read, {1:N0} to insert, {2:N0} to update",
                "Предпросмотр: строк {0:N0}, будет вставлено {1:N0}, будет обновлено {2:N0}"
            ),
            read,
            insert,
            update
        );

    public static string FormatTableSummary(long read, long insert, long update, long mapped) =>
        string.Format(
            T(
                "Done: {0:N0} rows read, {1:N0} inserted, {2:N0} updated, {3:N0} IDs mapped",
                "Готово: строк {0:N0}, вставлено {1:N0}, обновлено {2:N0}, сопоставлено ID {3:N0}"
            ),
            read,
            insert,
            update,
            mapped
        );

    public static string FormatRowsProcessed(long rows) =>
        string.Format(T("Rows processed: {0:N0}", "Обработано строк: {0:N0}"), rows);

    public static string FormatLoadingMatchMap(IEnumerable<string> columns) =>
        string.Format(
            T(
                "Loading match map for columns \"{0}\"...",
                "Загрузка соответствий по полям «{0}»..."
            ),
            JoinQuoted(columns)
        );

    public static string CancelledByUser => T("Cancelled by user", "Отменено пользователем");

    public static string FormatTableCancelled(string table) =>
        string.Format(T("Table {0}: copy cancelled", "Таблица {0}: копирование отменено"), table);

    public static string FormatTableError(string table, string error) =>
        string.Format(T("Table {0}: error — {1}", "Таблица {0}: ошибка — {1}"), table, error);

    // ---- DataCopyEngine: errors ----

    public static string NoMatchColumns =>
        T("No match columns specified for the table.", "Не указаны поля сопоставления для таблицы.");

    public static string NoCommonColumns =>
        T(
            "No common columns to copy (all columns are computed or target identity).",
            "Нет общих колонок для копирования (все колонки вычисляемые или identity приёмника)."
        );

    public static string FormatMatchColumnNotInCopySet(IEnumerable<string> columns) =>
        string.Format(
            T(
                "One of the match columns \"{0}\" is not among the columns being copied.",
                "Одно из полей сопоставления «{0}» не найдено среди копируемых колонок."
            ),
            JoinQuoted(columns)
        );

    public static string FormatMappingColumnNotFound(string column) =>
        string.Format(
            T(
                "Could not determine the column for ID mapping: \"{0}\".",
                "Не удалось определить колонку для сопоставления ID: «{0}»."
            ),
            column
        );

    public static string FormatNoIdMapping(
        string childTable,
        string childColumn,
        string parentTable,
        string parentColumn,
        string value
    ) =>
        string.Format(
            T(
                "No ID mapping for foreign key ({0}.{1} → {2}.{3}) with value {4}. Make sure the parent table is selected for copying.",
                "Нет сопоставления ID для внешнего ключа ({0}.{1} → {2}.{3}) со значением {4}. Проверьте, что родительская таблица выбрана для копирования."
            ),
            childTable,
            childColumn,
            parentTable,
            parentColumn,
            Language == "ru" ? $"«{value}»" : $"\"{value}\""
        );

    public static string FormatNoNewId(string table) =>
        string.Format(
            T(
                "No new ID value returned when inserting into table {0}.",
                "Не получено новое значение ID при вставке в таблицу {0}."
            ),
            table
        );

    public static string FormatTargetIdNotFound(IEnumerable<string> columns, string key) =>
        string.Format(
            T(
                "Could not determine the target ID for row with match columns \"{0}\" = \"{1}\".",
                "Не удалось определить целевой ID для строки с полями «{0}» = «{1}»."
            ),
            JoinQuoted(columns),
            key
        );

    public static string FormatColumnNotFound(string column, string table) =>
        string.Format(
            T(
                "Column \"{0}\" not found in table {1}.",
                "Колонка «{0}» не найдена в таблице {1}."
            ),
            column,
            table
        );

    // ---- TableDependencySorter ----

    public static string FormatCycleDetected(string tables) =>
        string.Format(
            T(
                "Circular dependency detected between tables: {0}",
                "Обнаружена циклическая зависимость между таблицами: {0}"
            ),
            tables
        );

    // ---- SqlServerProvider ----

    public static string FormatTableNotFound(string table) =>
        string.Format(
            T("Table {0} not found in the database.", "Таблица {0} не найдена в базе данных."),
            table
        );

    public static string MatchColumnMissing =>
        T(
            "One of the match columns is not among the columns being copied.",
            "Одно из полей сопоставления не найдено среди копируемых колонок."
        );

    public static string UpdateColumnMissing =>
        T("One of the update columns was not found.", "Одна из колонок обновления не найдена.");

    // ---- DbProviderFactory ----

    public static string FormatUnknownProvider(string key) =>
        string.Format(T("Unknown database type: {0}", "Неизвестный тип СУБД: {0}"), key);
}

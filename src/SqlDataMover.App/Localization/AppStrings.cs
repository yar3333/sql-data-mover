using System.ComponentModel;
using System.Globalization;
using SqlDataMover.Core.Localization;

namespace SqlDataMover.App.Localization;

/// <summary>A language offered in the language selector.</summary>
public sealed record LanguageOption(string Code, string DisplayName);

/// <summary>
/// All user-visible UI strings. English is the default; Russian is optional.
/// Views bind via <c>{Binding Key, Source={x:Static loc:AppStrings.Current}}</c>
/// and update live when <see cref="Language"/> changes.
/// </summary>
public sealed class AppStrings : INotifyPropertyChanged
{
    private static AppStrings? _current;

    /// <summary>The singleton instance used by views (via resource) and view models (via code).</summary>
    public static AppStrings Current => _current ??= new AppStrings();

    /// <summary>Available languages, shown in their own language (endonyms).</summary>
    public static IReadOnlyList<LanguageOption> Languages { get; } =
    [new("en", "English"), new("ru", "Русский")];

    public event PropertyChangedEventHandler? PropertyChanged;

    private string _language = "en";

    /// <summary>Current UI language code: "en" or "ru". Changing it also switches core messages and number formatting.</summary>
    public string Language
    {
        get => _language;
        set
        {
            var normalized = value == "ru" ? "ru" : "en";
            if (_language == normalized)
                return;
            _language = normalized;
            CoreStrings.Language = normalized;
            var culture = new CultureInfo(normalized == "ru" ? "ru-RU" : "en-US");
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
    }

    public AppStrings() => _current = this;

    /// <summary>
    /// Initial language for the first run: Russian only if the operating system UI is Russian,
    /// English otherwise.
    /// </summary>
    public static string DetectInitialLanguage() =>
        DetectInitialLanguage(CultureInfo.InstalledUICulture);

    public static string DetectInitialLanguage(CultureInfo osUiCulture) =>
        osUiCulture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase)
            ? "ru"
            : "en";

    private string T(string en, string ru) => _language == "ru" ? ru : en;

    // ---- Main window ----

    public string Subtitle =>
        T("Copy data between SQL servers", "Копирование данных между серверами");
    public string Step1Label => T("Connect", "Подключение");
    public string Step2Label => T("Select tables", "Выбор таблиц");
    public string Step3Label => T("Mapping", "Сопоставление");
    public string Step4Label => T("Preview", "Предпросмотр");
    public string Restart => T("Restart", "Заново");
    public string Back => T("← Back", "← Назад");
    public string Next => T("Next →", "Далее →");

    // ---- Step 1: connection ----

    public string Step1Title => T("Step 1. Connect", "Шаг 1. Подключение");
    public string Step1Subtitle =>
        T(
            "Enter the database type and the connection strings of the source and target servers (C# connection string format).",
            "Укажите тип СУБД и строки подключения источника и приёмника (формат connection string C#)."
        );
    public string DbTypeLabel => T("Database type", "Тип СУБД");
    public string SourceLabel => T("Source", "Источник (source)");
    public string TargetLabel => T("Target", "Приёмник (target)");
    public string ConnectButton => T("Connect", "Подключиться");
    public string HowItWorks =>
        T(
            "How it works: tables are copied in foreign-key dependency order. Auto-increment IDs are remapped; the old-to-new ID mapping is kept in memory and substituted into the foreign keys of dependent tables.",
            "Как это работает: таблицы копируются в порядке зависимостей по внешним ключам. Автоинкрементные ID переназначаются, соответствие старых и новых ID хранится в памяти и подставляется во внешние ключи зависимых таблиц."
        );
    public string Connecting => T("Connecting to servers...", "Подключение к серверам...");
    public string SelectProviderError => T("Select a database type.", "Выберите тип СУБД.");
    public string Connected => T("✓ Connected", "✓ подключено");

    public string FormatConnectionError(string message) =>
        string.Format(T("Connection error: {0}", "Ошибка подключения: {0}"), message);

    // ---- Step 2: tables ----

    public string Step2Title => T("Step 2. Select tables", "Шаг 2. Выбор таблиц");
    public string Step2Subtitle =>
        T(
            "Check the tables to copy: schema → tables.",
            "Отметьте таблицы для копирования: схема → таблицы."
        );
    public string FilterPlaceholder =>
        T("Filter by table or schema name...", "Фильтр по имени таблицы или схемы...");
    public string Selected => T("Selected", "Выбрано");
    public string Total => T("Total", "Всего");

    public string FormatRowCount(long rows) =>
        string.Format(T("{0:N0} rows", "{0:N0} строк"), rows);

    // ---- Step 3: mapping ----

    public string Step3Title => T("Step 3. Match columns", "Шаг 3. Поля сопоставления");
    public string Step3Subtitle =>
        T(
            "For each table, select one or more columns: their value combination matches source rows against existing target rows. Matched rows are updated, missing rows are inserted.",
            "Для каждой таблицы отметьте одно или несколько полей: комбинация их значений сопоставляет строки источника с уже существующими строками приёмника. Совпавшие строки обновляются, отсутствующие — вставляются."
        );
    public string TablesToCopy => T("Tables to copy", "Таблицы для копирования");
    public string SelectAtLeastOne =>
        T(
            "Select at least one column for every table to proceed to the preview.",
            "Выберите хотя бы одно поле для каждой таблицы, чтобы перейти к предпросмотру."
        );
    public string LoadingColumns => T("Loading columns...", "Загрузка колонок...");
    public string LoadingTableColumns =>
        T("Loading table columns...", "Загрузка колонок таблиц...");

    // ---- Step 4: preview and copy ----

    public string Step4Title => T("Step 4. Preview and copy", "Шаг 4. Предпросмотр и копирование");
    public string Step4Subtitle =>
        T(
            "A dry run (no writes) shows how many rows will be inserted and updated in each table. Start the copy after reviewing the statistics.",
            "Предварительный прогон (без записи) показывает, сколько строк в каждой таблице будет вставлено и обновлено. Запускайте копирование после просмотра статистики."
        );
    public string PreviewStats => T("Preview statistics", "Статистика предпросмотра");
    public string RunningDryRun => T("Running dry run...", "Выполняется предварительный прогон...");
    public string DryRunStatus =>
        T("Running dry run (no writes)...", "Выполняется предварительный прогон (без записи)...");
    public string TableHeader => T("Table", "Таблица");
    public string ReadHeader => T("Read", "Прочитано");
    public string InsertedHeader => T("Inserted", "Вставлено");
    public string UpdatedHeader => T("Updated", "Обновлено");
    public string DeletedHeader => T("Deleted", "Удалено");
    public string StatusHeader => T("Status", "Статус");
    public string NoStatsYet =>
        T(
            "Statistics will appear after the dry run.",
            "Статистика появится после предварительного прогона."
        );
    public string StartCopy => T("Start copy", "Запустить копирование");
    public string Cancel => T("Cancel", "Отмена");
    public string RowsWord => T("Rows", "Строк");
    public string LogTitle => T("Log", "Журнал");
    public string AllowTemporaryUniqueDuplicates =>
        T(
            "Allow temporary duplicate values in unique columns during copy",
            "Разрешить временное задвоение уникальных значений при копировании"
        );
    public string AllowTemporaryUniqueDuplicatesToolTip =>
        T(
            "Disables the target's unique indexes (except the primary key) for the duration of each table copy and rebuilds them before the transaction commits. Use when a unique field is temporarily duplicated during the copy (e.g. a new row is inserted with a value that a stale row still holds and will release on update). Requires ALTER permission on the target tables.",
            "Уникальные индексы приёмника (кроме первичного ключа) отключаются на время копирования таблицы и пересоздаются перед коммитом транзакции. Включайте, если во время копирования возникает временное задвоение уникального поля (например, новая строка вставляется со значением, которое ещё не освободила обновляемая устаревшая строка). Требует права ALTER на таблицы приёмника."
        );
    public string DeleteExtraRows =>
        T(
            "Delete extra rows in target tables (rows absent from the source)",
            "Удалять лишние записи в таблицах-получателях (отсутствующие в источнике)"
        );
    public string DeleteExtraRowsToolTip =>
        T(
            "Before copying, deletes rows in each target table whose match-column values are absent from the source. Frees identity and unique values occupied by stale rows, avoiding conflicts on insert. Deletion runs before the copy, in reverse dependency order (children first), so foreign keys are not violated.",
            "Перед копированием удаляет в каждой таблице-получателе строки, комбинация значений полей сопоставления которых отсутствует в источнике. Устаревшие строки освобождают значения identity и уникальных полей — вставка идёт без конфликтов. Удаление выполняется до копирования, в обратном порядке зависимостей (дети раньше родителей), чтобы не нарушить внешние ключи."
        );

    public string FormatPreviewSummary(int tableCount, long toInsert, long toUpdate) =>
        string.Format(
            T(
                "Preview complete: tables {0}, rows to insert: {1:N0}, rows to update: {2:N0}.",
                "Предпросмотр завершён: таблиц {0}, будет вставлено {1:N0}, будет обновлено {2:N0}."
            ),
            tableCount,
            toInsert,
            toUpdate
        );

    public string FormatPreviewSummaryWithDeletes(
        int tableCount,
        long toInsert,
        long toUpdate,
        long toDelete
    ) =>
        string.Format(
            T(
                "Preview complete: tables {0}, rows to insert: {1:N0}, rows to update: {2:N0}, rows to delete: {3:N0}.",
                "Предпросмотр завершён: таблиц {0}, будет вставлено {1:N0}, будет обновлено {2:N0}, будет удалено {3:N0}."
            ),
            tableCount,
            toInsert,
            toUpdate,
            toDelete
        );

    public string FormatPreviewSummaryWithErrors(
        int failed,
        int tableCount,
        long toInsert,
        long toUpdate
    ) =>
        string.Format(
            T(
                "Preview completed with errors ({0} of {1} tables). Rows to insert: {2:N0}, rows to update: {3:N0}. Details in the log and the Status column.",
                "Предпросмотр завершён с ошибками ({0} из {1} таблиц). Будет вставлено {2:N0}, обновлено {3:N0}. Подробности — в журнале и в столбце «Статус»."
            ),
            failed,
            tableCount,
            toInsert,
            toUpdate
        );

    public string PreviewCancelled =>
        T("Preview cancelled by the user.", "Предпросмотр отменён пользователем.");

    public string FormatPreviewError(string message) =>
        string.Format(T("Preview error: {0}", "Ошибка предпросмотра: {0}"), message);

    public string FormatCopySummary(double seconds, int tableCount, long inserted, long updated) =>
        string.Format(
            T(
                "Copy completed in {0:F1} s. Tables: {1}. Inserted rows: {2:N0}, updated: {3:N0}.",
                "Копирование завершено за {0:F1} с. Таблиц: {1}. Вставлено строк: {2:N0}, обновлено: {3:N0}."
            ),
            seconds,
            tableCount,
            inserted,
            updated
        );

    public string FormatCopySummaryWithErrors(
        int failed,
        int tableCount,
        long inserted,
        long updated
    ) =>
        string.Format(
            T(
                "Copy completed with errors ({0} of {1} tables). Inserted rows: {2:N0}, updated: {3:N0}. Details in the log.",
                "Копирование завершено с ошибками ({0} из {1} таблиц). Вставлено строк: {2:N0}, обновлено: {3:N0}. Подробности — в журнале."
            ),
            failed,
            tableCount,
            inserted,
            updated
        );

    public string FormatCopySummaryWithDeletes(
        double seconds,
        int tableCount,
        long inserted,
        long updated,
        long deleted
    ) =>
        string.Format(
            T(
                "Copy completed in {0:F1} s. Tables: {1}. Inserted rows: {2:N0}, updated: {3:N0}, deleted: {4:N0}.",
                "Копирование завершено за {0:F1} с. Таблиц: {1}. Вставлено строк: {2:N0}, обновлено: {3:N0}, удалено: {4:N0}."
            ),
            seconds,
            tableCount,
            inserted,
            updated,
            deleted
        );

    public string CopyCancelled =>
        T("Copy cancelled by the user.", "Копирование отменено пользователем.");

    public string FormatCopyError(string message) =>
        string.Format(T("Copy error: {0}", "Ошибка копирования: {0}"), message);

    public string FormatProgress(double percent, long done, long total) =>
        string.Format(
            T("{0:F0}% · {1:N0} / {2:N0} rows", "{0:F0}% · {1:N0} / {2:N0} строк"),
            percent,
            done,
            total
        );
}

using SqlDataMover.App.Localization;
using SqlDataMover.App.Models;
using SqlDataMover.App.ViewModels;
using SqlDataMover.Core.Abstractions;
using SqlDataMover.Core.Models;
using SqlDataMover.TestHelpers;

namespace SqlDataMover.App.Tests;

/// <summary>
/// Тесты логики мастера на уровне ViewModel: навигация, блокировка кнопок,
/// полный сценарий «подключение → выбор таблиц → сопоставление → предпросмотр».
/// Провайдеры — in-memory (FakeProvider), регистрируются в DbProviderFactory.
/// </summary>
[Collection("app")]
public class MainViewModelTests
{
    /// <summary>
    /// Каждый тест стартует с чистым файлом настроек: история подключений и выбор
    /// таблиц пишутся в общий (на коллекцию) файл, и тесты, проходящие мастер,
    /// не должны влиять на состояние, с которым начинается следующий тест.
    /// </summary>
    public MainViewModelTests() => AppSettingsStore.Save(new AppSettings());

    private static ProviderDescriptor FakeDescriptor(string key) =>
        DbProviderFactory.SupportedProviders.First(p => p.Key == key);

    /// <summary>
    /// Регистрирует СУБД с указанным ключом: по строке подключения "src" возвращается
    /// источник, всё остальное — приёмник. Каждый тест использует свой уникальный ключ,
    /// чтобы реестр глобальной фабрики не конфликтовал между тестами.
    /// </summary>
    private static void RegisterFake(string key, IDbProvider source, IDbProvider target) =>
        DbProviderFactory.Register(key, "Fake", cs => cs == "src" ? source : target);

    /// <summary>Мастер с fake-СУБД: регистрация провайдера и заполнение формы подключения.</summary>
    private static MainViewModel SetupWizard(string key, IDbProvider source, IDbProvider target)
    {
        RegisterFake(key, source, target);

        var vm = new MainViewModel();
        vm.Connection.SelectedProvider = FakeDescriptor(key);
        vm.Connection.SourceConnectionString = "src";
        vm.Connection.TargetConnectionString = "tgt";
        return vm;
    }

    [Fact]
    public async Task Full_wizard_flow_with_fake_provider()
    {
        var vm = SetupWizard(
            "fake-flow",
            new FakeProvider(
                WizardData.CustomerTable([
                    new object?[] { 10, "Alice" },
                    new object?[] { 20, "Bob" },
                ]),
                WizardData.OrderTable([new object?[] { 1, 10 }])
            ),
            new FakeProvider(WizardData.CustomerTable(), WizardData.OrderTable())
        );

        // Шаг 0: «Далее» заблокировано (нет подключения), «Назад» недоступно.
        Assert.Equal(0, vm.CurrentStep);
        Assert.False(vm.CanGoNext);
        Assert.False(vm.CanGoBack);

        await vm.Connection.ConnectCommand.ExecuteAsync(null);
        Assert.True(vm.Connection.IsReady);
        Assert.Equal("✓ Connected", vm.Connection.SourceStatus);
        Assert.Equal("✓ Connected", vm.Connection.TargetStatus);
        Assert.Equal(2, vm.Tables.TotalCount);
        Assert.True(vm.CanGoNext); // подключение есть — можно идти выбирать таблицы

        // Шаг 1: ни одна таблица не выбрана — «Далее» заблокировано.
        await vm.GoNextCommand.ExecuteAsync(null);
        Assert.Equal(1, vm.CurrentStep);
        Assert.False(vm.CanGoNext);

        // Выбираем обе таблицы — «Далее» разблокируется.
        foreach (var schema in vm.Tables.Schemas)
        foreach (var table in schema.AllTables)
            table.IsChecked = true;
        Assert.Equal(2, vm.Tables.SelectedCount);
        Assert.True(vm.CanGoNext);

        // Шаг 2: сопоставление, по умолчанию выбраны поля PK.
        await vm.GoNextCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.CurrentStep);
        Assert.Equal(2, vm.Mapping.Tables.Count);
        var mapping = vm.Mapping.Tables.Single(t => t.Table.Name == "Customers");
        Assert.Equal(new[] { "Id" }, mapping.GetMatchColumns()); // по умолчанию — PK

        // Шаг 3: предпросмотр (dry run), записей в приёмнике нет.
        await vm.GoNextCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.CurrentStep);
        Assert.True(vm.Preview.HasPreview);
        Assert.Contains("Preview complete", vm.Preview.PreviewSummary);

        var customers = vm.Preview.Tables.Single(t => t.Table.Name == "Customers");
        Assert.Equal(2, customers.RowsRead);
        Assert.Equal(2, customers.Inserted);
        Assert.Equal(0, customers.Updated);
        var orders = vm.Preview.Tables.Single(t => t.Table.Name == "Orders");
        Assert.Equal(1, orders.Inserted);
        Assert.Null(orders.Error);

        Assert.False(vm.CanGoNext); // на последнем шаге «Далее» не работает

        // «Назад» возвращает на шаг сопоставления.
        vm.GoBackCommand.Execute(null);
        Assert.Equal(2, vm.CurrentStep);

        // «Заново» сбрасывает всё в исходное состояние.
        await vm.RestartCommand.ExecuteAsync(null);
        Assert.Equal(0, vm.CurrentStep);
        Assert.Empty(vm.Tables.Schemas);
        Assert.Empty(vm.Mapping.Tables);
        Assert.Empty(vm.Preview.Tables);
        Assert.False(vm.CanGoNext);
    }

    [Fact]
    public async Task Preview_reports_update_when_row_already_exists_in_target()
    {
        var vm = SetupWizard(
            "fake-upd",
            new FakeProvider(WizardData.CustomerTable([new object?[] { 10, "Alice" }])),
            new FakeProvider(WizardData.CustomerTable([new object?[] { 10, "OLD" }]))
        );

        await vm.Connection.ConnectCommand.ExecuteAsync(null);
        vm.Tables.Schemas[0].AllTables[0].IsChecked = true;
        await vm.GoNextCommand.ExecuteAsync(null); // → выбор таблиц
        await vm.GoNextCommand.ExecuteAsync(null); // → сопоставление
        await vm.GoNextCommand.ExecuteAsync(null); // → предпросмотр

        var row = Assert.Single(vm.Preview.Tables);
        Assert.Equal(0, row.Inserted);
        Assert.Equal(1, row.Updated);
        Assert.Contains("rows to update: 1", vm.Preview.PreviewSummary);
    }

    [Fact]
    public async Task Connect_failure_shows_error_and_keeps_next_disabled()
    {
        DbProviderFactory.Register(
            "fake-fail",
            "Fake Fail",
            _ => new FakeProvider(failOnConnect: true)
        );

        var vm = new MainViewModel();
        vm.Connection.SelectedProvider = FakeDescriptor("fake-fail");
        vm.Connection.SourceConnectionString = "src";
        vm.Connection.TargetConnectionString = "tgt";
        await vm.Connection.ConnectCommand.ExecuteAsync(null);

        Assert.False(vm.Connection.IsReady);
        Assert.False(vm.Connection.IsBusy);
        Assert.Contains("Connection error", vm.Connection.StatusMessage);
        Assert.False(vm.CanGoNext);
    }

    [Fact]
    public async Task Selected_tables_are_saved_for_source_connection_string()
    {
        var vm = SetupWizard(
            "fake-save",
            new FakeProvider(WizardData.CustomerTable(), WizardData.OrderTable()),
            new FakeProvider(WizardData.CustomerTable(), WizardData.OrderTable())
        );

        await vm.Connection.ConnectCommand.ExecuteAsync(null);
        await vm.GoNextCommand.ExecuteAsync(null); // → шаг таблиц
        foreach (var schema in vm.Tables.Schemas)
        foreach (var table in schema.AllTables)
            table.IsChecked = true;
        Assert.Equal(2, vm.Tables.SelectedCount);

        // Переход со шага таблиц запоминает выбор за строкой подключения источника.
        await vm.GoNextCommand.ExecuteAsync(null); // → сопоставление
        Assert.Equal(2, vm.CurrentStep);

        var saved = AppSettingsStore.Load().SelectedTablesBySource["src"];
        Assert.Equal(["dbo.Customers", "dbo.Orders"], saved);
    }

    [Fact]
    public async Task Saved_tables_are_preselected_after_reconnect_to_same_source()
    {
        // Выбор из «прошлого запуска»: для источника "src" сохранены таблицы.
        AppSettingsStore.Save(
            new AppSettings { SelectedTablesBySource = new() { ["src"] = ["dbo.Customers"] } }
        );

        var vm = SetupWizard(
            "fake-restore",
            new FakeProvider(WizardData.CustomerTable(), WizardData.OrderTable()),
            new FakeProvider(WizardData.CustomerTable(), WizardData.OrderTable())
        );

        await vm.Connection.ConnectCommand.ExecuteAsync(null);
        await vm.GoNextCommand.ExecuteAsync(null); // → шаг таблиц
        Assert.Equal(1, vm.CurrentStep);

        var all = vm.Tables.Schemas.SelectMany(s => s.AllTables).ToList();
        Assert.True(all.Single(t => t.Name == "Customers").IsChecked);
        Assert.False(all.Single(t => t.Name == "Orders").IsChecked);
        Assert.Equal(1, vm.Tables.SelectedCount);
        // Схема частично выбрана — чекбокс в промежуточном состоянии.
        Assert.Null(vm.Tables.Schemas.Single().IsChecked);

        // Пользователь по-прежнему может поменять выбор.
        all.Single(t => t.Name == "Orders").IsChecked = true;
        Assert.Equal(2, vm.Tables.SelectedCount);
        Assert.True(vm.CanGoNext);
    }

    [Fact]
    public async Task Connect_preserves_language_and_saved_tables()
    {
        AppSettingsStore.Save(new AppSettings { Language = "ru" });

        var vm = SetupWizard(
            "fake-preserve",
            new FakeProvider(WizardData.CustomerTable()),
            new FakeProvider(WizardData.CustomerTable())
        );

        await vm.Connection.ConnectCommand.ExecuteAsync(null);

        var settings = AppSettingsStore.Load();
        Assert.Equal("ru", settings.Language);
        Assert.Contains("src", settings.SourceConnectionStrings);
        Assert.Contains("tgt", settings.TargetConnectionStrings);
    }

    [Fact]
    public async Task Match_columns_are_saved_when_leaving_mapping_step()
    {
        var vm = SetupWizard(
            "fake-map-save",
            new FakeProvider(WizardData.CustomerTable(), WizardData.OrderTable()),
            new FakeProvider(WizardData.CustomerTable(), WizardData.OrderTable())
        );

        await vm.Connection.ConnectCommand.ExecuteAsync(null);
        await vm.GoNextCommand.ExecuteAsync(null); // → шаг таблиц
        foreach (var schema in vm.Tables.Schemas)
        foreach (var table in schema.AllTables)
            table.IsChecked = true;
        await vm.GoNextCommand.ExecuteAsync(null); // → сопоставление
        Assert.Equal(2, vm.CurrentStep);

        // Меняем поля сопоставления Customers: вместо PK (Id) — Name.
        var customers = vm.Mapping.Tables.Single(t => t.Table.Name == "Customers");
        customers.Columns.Single(c => c.Name == "Id").IsSelected = false;
        customers.Columns.Single(c => c.Name == "Name").IsSelected = true;

        // Уход со шага сопоставления запоминает поля за источником и таблицей.
        await vm.GoNextCommand.ExecuteAsync(null); // → предпросмотр
        Assert.Equal(3, vm.CurrentStep);

        var saved = AppSettingsStore.Load().MatchColumnsBySource["src"];
        Assert.Equal(["Name"], saved["dbo.Customers"]);
        Assert.Equal(["Id"], saved["dbo.Orders"]); // дефолт Orders не менялся
    }

    [Fact]
    public async Task Saved_match_columns_are_restored_after_reconnect()
    {
        AppSettingsStore.Save(
            new AppSettings
            {
                MatchColumnsBySource = new() { ["src"] = new() { ["dbo.Customers"] = ["Name"] } },
            }
        );

        var vm = SetupWizard(
            "fake-map-restore",
            new FakeProvider(WizardData.CustomerTable(), WizardData.OrderTable()),
            new FakeProvider(WizardData.CustomerTable(), WizardData.OrderTable())
        );

        await vm.Connection.ConnectCommand.ExecuteAsync(null);
        await vm.GoNextCommand.ExecuteAsync(null); // → шаг таблиц
        vm.Tables.Schemas[0].AllTables[0].IsChecked = true; // Customers
        await vm.GoNextCommand.ExecuteAsync(null); // → сопоставление

        var customers = vm.Mapping.Tables.Single(t => t.Table.Name == "Customers");
        Assert.Equal(["Name"], customers.GetMatchColumns());
    }

    [Fact]
    public async Task Stale_saved_match_columns_fall_back_to_defaults()
    {
        // Колонки из сохранённой конфигурации больше нет в таблице (схема изменилась).
        AppSettingsStore.Save(
            new AppSettings
            {
                MatchColumnsBySource = new()
                {
                    ["src"] = new() { ["dbo.Customers"] = ["MissingCol"] },
                },
            }
        );

        var vm = SetupWizard(
            "fake-map-fallback",
            new FakeProvider(WizardData.CustomerTable(), WizardData.OrderTable()),
            new FakeProvider(WizardData.CustomerTable(), WizardData.OrderTable())
        );

        await vm.Connection.ConnectCommand.ExecuteAsync(null);
        await vm.GoNextCommand.ExecuteAsync(null); // → шаг таблиц
        vm.Tables.Schemas[0].AllTables[0].IsChecked = true; // Customers
        await vm.GoNextCommand.ExecuteAsync(null); // → сопоставление

        var customers = vm.Mapping.Tables.Single(t => t.Table.Name == "Customers");
        Assert.Equal(["Id"], customers.GetMatchColumns()); // фолбэк на PK
    }

    [Fact]
    public async Task Saving_match_columns_keeps_config_of_unselected_tables()
    {
        // В прошлый раз для Orders были настроены поля — таблица не выбрана сейчас,
        // но конфигурация не должна потеряться.
        AppSettingsStore.Save(
            new AppSettings
            {
                MatchColumnsBySource = new()
                {
                    ["src"] = new() { ["dbo.Orders"] = ["CustomerId"] },
                },
            }
        );

        var vm = SetupWizard(
            "fake-map-merge",
            new FakeProvider(WizardData.CustomerTable(), WizardData.OrderTable()),
            new FakeProvider(WizardData.CustomerTable(), WizardData.OrderTable())
        );

        await vm.Connection.ConnectCommand.ExecuteAsync(null);
        await vm.GoNextCommand.ExecuteAsync(null); // → шаг таблиц
        vm.Tables.Schemas[0].AllTables[0].IsChecked = true; // только Customers
        await vm.GoNextCommand.ExecuteAsync(null); // → сопоставление
        await vm.GoNextCommand.ExecuteAsync(null); // → предпросмотр

        var saved = AppSettingsStore.Load().MatchColumnsBySource["src"];
        Assert.Equal(["Id"], saved["dbo.Customers"]);
        Assert.Equal(["CustomerId"], saved["dbo.Orders"]);
    }

    [Fact]
    public async Task Back_and_restart_are_disabled_while_mapping_columns_load()
    {
        var source = new GatedProvider(
            new FakeProvider(WizardData.CustomerTable(), WizardData.OrderTable())
        );
        source.GateDetails();
        var vm = SetupWizard(
            "fake-back-load",
            source,
            new FakeProvider(WizardData.CustomerTable(), WizardData.OrderTable())
        );

        await vm.Connection.ConnectCommand.ExecuteAsync(null);
        await vm.GoNextCommand.ExecuteAsync(null); // → шаг таблиц
        foreach (var schema in vm.Tables.Schemas)
        foreach (var table in schema.AllTables)
            table.IsChecked = true;

        // «Далее» ушёл в фоновую загрузку колонок: назад и «Заново» блокируются.
        var nextTask = vm.GoNextCommand.ExecuteAsync(null);
        await Task.Yield();
        Assert.True(vm.Mapping.IsBusy);
        Assert.False(vm.CanGoNext);
        Assert.False(vm.CanGoBack);
        Assert.False(vm.CanRestart);

        source.OpenGates();
        await nextTask;
        Assert.Equal(2, vm.CurrentStep);
        Assert.False(vm.Mapping.IsBusy);
        Assert.True(vm.CanGoBack);
        Assert.True(vm.CanRestart);
    }

    [Fact]
    public async Task Back_during_mapping_load_does_not_jump_forward_again()
    {
        var source = new GatedProvider(
            new FakeProvider(WizardData.CustomerTable(), WizardData.OrderTable())
        );
        source.GateDetails();
        var vm = SetupWizard(
            "fake-back-race",
            source,
            new FakeProvider(WizardData.CustomerTable(), WizardData.OrderTable())
        );

        await vm.Connection.ConnectCommand.ExecuteAsync(null);
        await vm.GoNextCommand.ExecuteAsync(null); // → шаг таблиц
        foreach (var schema in vm.Tables.Schemas)
        foreach (var table in schema.AllTables)
            table.IsChecked = true;

        var nextTask = vm.GoNextCommand.ExecuteAsync(null);
        await Task.Yield();
        Assert.True(vm.Mapping.IsBusy);

        // Пока колонки грузятся, пользователь уходит на шаг подключения.
        vm.CurrentStep = 0;

        source.OpenGates();
        await nextTask;

        // Завершившаяся загрузка не должна перекинуть пользователя вперёд.
        Assert.Equal(0, vm.CurrentStep);
    }

    [Fact]
    public async Task Back_and_restart_are_disabled_during_real_copy()
    {
        var target = new GatedProvider(new FakeProvider(WizardData.CustomerTable()));
        target.GateTransaction();
        var vm = SetupWizard(
            "fake-back-copy",
            new FakeProvider(WizardData.CustomerTable([new object?[] { 10, "Alice" }])),
            target
        );

        await vm.Connection.ConnectCommand.ExecuteAsync(null);
        await vm.GoNextCommand.ExecuteAsync(null); // → шаг таблиц
        foreach (var schema in vm.Tables.Schemas)
        foreach (var table in schema.AllTables)
            table.IsChecked = true;
        await vm.GoNextCommand.ExecuteAsync(null); // → сопоставление
        await vm.GoNextCommand.ExecuteAsync(null); // → предпросмотр (dry run транзакций не открывает)
        Assert.True(vm.Preview.HasPreview);

        // Реальное копирование блокируется на открытии транзакции: покинуть страницу
        // и сбросить мастера в этот момент нельзя — копия пишет в БД.
        var copyTask = vm.Preview.StartCopyCommand.ExecuteAsync(null);
        await Task.Yield();
        Assert.True(vm.Preview.IsCopying);
        Assert.False(vm.CanGoBack);
        Assert.False(vm.CanRestart);

        target.OpenGates();
        await copyTask;
        Assert.False(vm.Preview.IsCopying);
        Assert.True(vm.CanGoBack);
        Assert.True(vm.CanRestart);
    }

    [Fact]
    public async Task Cancel_interrupts_dry_run_preview()
    {
        var target = new GatedProvider(
            new FakeProvider(WizardData.CustomerTable(), WizardData.OrderTable())
        );
        target.GateMatchMap();
        var vm = SetupWizard(
            "fake-cancel-preview",
            new FakeProvider(
                WizardData.CustomerTable([new object?[] { 10, "Alice" }]),
                WizardData.OrderTable([new object?[] { 1, 10 }])
            ),
            target
        );

        await vm.Connection.ConnectCommand.ExecuteAsync(null);
        await vm.GoNextCommand.ExecuteAsync(null); // → шаг таблиц
        foreach (var schema in vm.Tables.Schemas)
        foreach (var table in schema.AllTables)
            table.IsChecked = true;
        await vm.GoNextCommand.ExecuteAsync(null); // → сопоставление

        // Предпросмотр блокируется на загрузке словаря сопоставления приёмника.
        var previewTask = vm.GoNextCommand.ExecuteAsync(null);
        await Task.Yield();
        Assert.True(vm.Preview.IsBusy);
        Assert.True(vm.Preview.CanCancel);

        // Отмена прерывает dry run и показывает итог об отмене.
        vm.Preview.CancelCopyCommand.Execute(null);
        await previewTask;
        Assert.False(vm.Preview.IsBusy);
        Assert.Equal(AppStrings.Current.PreviewCancelled, vm.Preview.PreviewSummary);
    }

    [Fact]
    public async Task Cancel_interrupts_real_copy()
    {
        var target = new GatedProvider(new FakeProvider(WizardData.CustomerTable()));
        target.GateTransaction();
        var vm = SetupWizard(
            "fake-cancel-copy",
            new FakeProvider(WizardData.CustomerTable([new object?[] { 10, "Alice" }])),
            target
        );

        await vm.Connection.ConnectCommand.ExecuteAsync(null);
        await vm.GoNextCommand.ExecuteAsync(null); // → шаг таблиц
        foreach (var schema in vm.Tables.Schemas)
        foreach (var table in schema.AllTables)
            table.IsChecked = true;
        await vm.GoNextCommand.ExecuteAsync(null); // → сопоставление
        await vm.GoNextCommand.ExecuteAsync(null); // → предпросмотр (dry run, транзакций не открывает)
        Assert.True(vm.Preview.HasPreview);

        // Копирование блокируется на открытии транзакции; отмена прерывает его.
        var copyTask = vm.Preview.StartCopyCommand.ExecuteAsync(null);
        await Task.Yield();
        Assert.True(vm.Preview.IsCopying);
        Assert.True(vm.Preview.CanCancel);

        vm.Preview.CancelCopyCommand.Execute(null);
        await copyTask;
        Assert.False(vm.Preview.IsCopying);
        Assert.Equal(AppStrings.Current.CopyCancelled, vm.Preview.SummaryText);
    }
}

/// <summary>
/// Обёртка над FakeProvider с «воротами»: указанная операция блокируется до вызова
/// <see cref="OpenGates"/>. Позволяет проверять навигацию, пока в фоне идёт длительная
/// операция (загрузка колонок, реальное копирование).
/// </summary>
internal sealed class GatedProvider : IDbProvider
{
    private readonly FakeProvider _inner;
    private TaskCompletionSource? _detailsGate;
    private TaskCompletionSource? _transactionGate;
    private TaskCompletionSource? _matchMapGate;

    public GatedProvider(FakeProvider inner) => _inner = inner;

    /// <summary>Блокирует GetTableDetailsAsync до открытия ворот.</summary>
    public void GateDetails() =>
        _detailsGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Блокирует BeginTransactionAsync до открытия ворот (только реальное копирование).</summary>
    public void GateTransaction() =>
        _transactionGate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

    /// <summary>Блокирует LoadMatchMapAsync до открытия ворот (dry run и реальное копирование).</summary>
    public void GateMatchMap() =>
        _matchMapGate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

    public void OpenGates()
    {
        _detailsGate?.TrySetResult();
        _transactionGate?.TrySetResult();
        _matchMapGate?.TrySetResult();
    }

    public string Name => _inner.Name;
    public bool IsConnected => _inner.IsConnected;

    public Task ConnectAsync(CancellationToken ct = default) => _inner.ConnectAsync(ct);

    public Task<IReadOnlyList<DbTable>> GetTablesAsync(CancellationToken ct = default) =>
        _inner.GetTablesAsync(ct);

    public async Task<DbTable> GetTableDetailsAsync(
        DbObjectName table,
        CancellationToken ct = default
    )
    {
        if (_detailsGate is not null)
            await _detailsGate.Task.WaitAsync(ct);
        return await _inner.GetTableDetailsAsync(table, ct);
    }

    public IAsyncEnumerable<object?[]> ReadRowsAsync(
        DbObjectName table,
        IReadOnlyList<string> columns,
        CancellationToken ct = default
    ) => _inner.ReadRowsAsync(table, columns, ct);

    public async Task<Dictionary<object, object>> LoadMatchMapAsync(
        DbObjectName table,
        IReadOnlyList<string> matchColumns,
        string mappedColumn,
        IDbWriteTransaction? transaction = null,
        CancellationToken ct = default
    )
    {
        if (_matchMapGate is not null)
            await _matchMapGate.Task.WaitAsync(ct);
        return await _inner.LoadMatchMapAsync(table, matchColumns, mappedColumn, transaction, ct);
    }

    public async Task<IDbWriteTransaction> BeginTransactionAsync(CancellationToken ct = default)
    {
        if (_transactionGate is not null)
            await _transactionGate.Task.WaitAsync(ct);
        return await _inner.BeginTransactionAsync(ct);
    }

    public Task<IReadOnlyList<object?>> InsertRowsAsync(
        DbTable table,
        IReadOnlyList<string> columns,
        IReadOnlyList<object?[]> rows,
        IDbWriteTransaction? transaction = null,
        CancellationToken ct = default
    ) => _inner.InsertRowsAsync(table, columns, rows, transaction, ct);

    public Task<int> UpdateRowsAsync(
        DbTable table,
        IReadOnlyList<string> matchColumns,
        IReadOnlyList<string> allColumns,
        IReadOnlyList<string> updateColumns,
        IReadOnlyList<object?[]> rows,
        IDbWriteTransaction? transaction = null,
        CancellationToken ct = default
    ) =>
        _inner.UpdateRowsAsync(
            table,
            matchColumns,
            allColumns,
            updateColumns,
            rows,
            transaction,
            ct
        );

    public Task<int> ExecuteUpdatesAsync(
        DbTable table,
        string setColumn,
        string whereColumn,
        IReadOnlyList<(object? SetValue, object? WhereValue)> pairs,
        IDbWriteTransaction? transaction = null,
        CancellationToken ct = default
    ) => _inner.ExecuteUpdatesAsync(table, setColumn, whereColumn, pairs, transaction, ct);

    public Task SetIdentityInsertAsync(
        DbObjectName table,
        bool enabled,
        IDbWriteTransaction? transaction = null,
        CancellationToken ct = default
    ) => _inner.SetIdentityInsertAsync(table, enabled, transaction, ct);

    public Task<long> GetIdentityCurrentAsync(DbObjectName table, CancellationToken ct = default) =>
        _inner.GetIdentityCurrentAsync(table, ct);

    public Task ReseedIdentityAsync(
        DbObjectName table,
        long newValue,
        IDbWriteTransaction? transaction = null,
        CancellationToken ct = default
    ) => _inner.ReseedIdentityAsync(table, newValue, transaction, ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

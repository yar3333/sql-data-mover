using SqlDataMover.App.Models;
using SqlDataMover.App.ViewModels;
using SqlDataMover.Core.Abstractions;
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
    private static void RegisterFake(string key, FakeProvider source, FakeProvider target) =>
        DbProviderFactory.Register(key, "Fake", cs => cs == "src" ? source : target);

    /// <summary>Мастер с fake-СУБД: регистрация провайдера и заполнение формы подключения.</summary>
    private static MainViewModel SetupWizard(string key, FakeProvider source, FakeProvider target)
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
}

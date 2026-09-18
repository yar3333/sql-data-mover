using SqlDataMover.Core.Copy;
using SqlDataMover.Core.Localization;
using SqlDataMover.Core.Models;
using SqlDataMover.TestHelpers;

namespace SqlDataMover.Core.Tests;

public class DataCopyEngineTests
{
    private static readonly DbObjectName Customers = new("dbo", "Customers");
    private static readonly DbObjectName Orders = new("dbo", "Orders");
    private static readonly DbObjectName Employees = new("dbo", "Employees");

    private static FakeTable CustomerTable(params object?[][] rows)
    {
        var table = new FakeTable
        {
            Meta = TestTables.Table(
                "dbo",
                "Customers",
                [TestTables.Col("Id", identity: true, pk: true), TestTables.Col("Name")]
            ),
        };
        foreach (var row in rows)
            table.Rows.Add(row);
        return table;
    }

    private static FakeTable OrderTable(params object?[][] rows)
    {
        var table = new FakeTable
        {
            Meta = TestTables.Table(
                "dbo",
                "Orders",
                [TestTables.Col("Id", identity: true, pk: true), TestTables.Col("CustomerId")],
                TestTables.Fk(
                    "FK_Orders_Customers",
                    "dbo",
                    "Orders",
                    "CustomerId",
                    "dbo",
                    "Customers",
                    "Id"
                )
            ),
        };
        foreach (var row in rows)
            table.Rows.Add(row);
        return table;
    }

    /// <summary>Таблица Customers с уникальным полем Name (уникальный индекс UQ_Name).</summary>
    private static FakeTable UniqueCustomerTable(params object?[][] rows)
    {
        var table = new FakeTable
        {
            Meta = TestTables.Table(
                "dbo",
                "Customers",
                [TestTables.Col("Id", identity: true, pk: true), TestTables.Col("Name")]
            ),
        };
        table.UniqueColumns.Add("Name");
        foreach (var row in rows)
            table.Rows.Add(row);
        return table;
    }

    private static TableCopyConfig Config(DbObjectName table, params string[] matchColumns) =>
        new() { Table = table, MatchColumns = matchColumns };

    [Fact]
    public async Task Copy_remaps_identity_and_substitutes_foreign_keys()
    {
        // Источник: два клиента (Id 10, 20, имена A, B). Цель: уже есть клиент Id=1 ("PRE").
        // Сопоставление по Name (не identity) — identity клиентов переназначается приёмником.
        var source = new FakeProvider(
            CustomerTable([new object?[] { 10, "A" }, new object?[] { 20, "B" }]),
            OrderTable([new object?[] { 1, 10 }, new object?[] { 2, 20 }, new object?[] { 3, 10 }])
        );
        var target = new FakeProvider(CustomerTable([new object?[] { 1, "PRE" }]), OrderTable([]));

        var engine = new DataCopyEngine(source, target);
        var result = await engine.CopyAsync(
            [Config(Customers, "Name"), Config(Orders, "Id")],
            CancellationToken.None
        );

        Assert.True(
            result.Success,
            string.Join("; ", result.Tables.Where(t => !t.Success).Select(t => t.Error))
        );

        // Новые ID: 1 (существующий) + 2, 3 (вставленные).
        var targetCustomers = target.GetTable(Customers).Rows;
        Assert.Equal(3, targetCustomers.Count);
        Assert.Equal("PRE", targetCustomers[0][1]);
        Assert.Equal("A", targetCustomers[1][1]);
        Assert.Equal("B", targetCustomers[2][1]);

        // CustomerId 10, 20, 10 → 2, 3, 2.
        var targetOrders = target.GetTable(Orders).Rows;
        Assert.Equal(3, targetOrders.Count);
        Assert.Equal(2, targetOrders[0][1]);
        Assert.Equal(3, targetOrders[1][1]);
        Assert.Equal(2, targetOrders[2][1]);

        var customersResult = result.Tables.Single(t => t.Table == Customers);
        Assert.Equal(2, customersResult.Inserted);
        Assert.Equal(0, customersResult.Updated); // строк с именами A/B в источнике нет — существующая строка не затронута
    }

    [Fact]
    public async Task Copy_matches_by_unique_column_that_is_not_pk()
    {
        // Источник: колонки Id (identity), Code (поле сопоставления), Name.
        var srcCustomers = new FakeTable
        {
            Meta = TestTables.Table(
                "dbo",
                "Customers",
                [
                    TestTables.Col("Id", identity: true, pk: true),
                    TestTables.Col("Code"),
                    TestTables.Col("Name"),
                ]
            ),
            Rows = { new object?[] { 100, "X1", "new" }, new object?[] { 101, "X2", "B" } },
        };
        var srcOrders = OrderTable([new object?[] { 1, 100 }, new object?[] { 2, 101 }]);

        var tgtCustomers = new FakeTable
        {
            Meta = srcCustomers.Meta,
            Rows = { new object?[] { 5, "X1", "old" } },
        };
        var tgtOrders = OrderTable([]);

        var source = new FakeProvider(srcCustomers, srcOrders);
        var target = new FakeProvider(tgtCustomers, tgtOrders);

        var engine = new DataCopyEngine(source, target);
        var result = await engine.CopyAsync(
            [Config(Customers, "Code"), Config(Orders, "Id")],
            CancellationToken.None
        );

        Assert.True(
            result.Success,
            string.Join("; ", result.Tables.Where(t => !t.Success).Select(t => t.Error))
        );

        // X1 обновлён (Id остался 5), X2 вставлен (новый Id 6).
        var rows = target.GetTable(Customers).Rows;
        Assert.Equal(2, rows.Count);
        Assert.Equal(5, rows[0][0]);
        Assert.Equal("new", rows[0][2]);
        Assert.Equal(6, rows[1][0]);
        Assert.Equal("X2", rows[1][1]);

        // Заказы: 100 → 5, 101 → 6.
        var orders = target.GetTable(Orders).Rows;
        Assert.Equal(5, orders[0][1]);
        Assert.Equal(6, orders[1][1]);
    }

    [Fact]
    public async Task Copy_handles_self_referencing_fk_in_two_phases()
    {
        var srcEmployees = new FakeTable
        {
            Meta = TestTables.Table(
                "dbo",
                "Employees",
                [
                    TestTables.Col("Id", identity: true, pk: true),
                    TestTables.Col("Name"),
                    TestTables.Col("ManagerId"),
                ],
                TestTables.Fk(
                    "FK_Empl_Empl",
                    "dbo",
                    "Employees",
                    "ManagerId",
                    "dbo",
                    "Employees",
                    "Id"
                )
            ),
            Rows =
            {
                new object?[] { 1, "CEO", null },
                new object?[] { 2, "Dev1", 1 },
                new object?[] { 3, "Dev2", 1 },
            },
        };
        var tgtEmployees = new FakeTable { Meta = srcEmployees.Meta };

        var source = new FakeProvider(srcEmployees);
        var target = new FakeProvider(tgtEmployees);

        var engine = new DataCopyEngine(source, target);
        var result = await engine.CopyAsync([Config(Employees, "Id")], CancellationToken.None);

        Assert.True(result.Success);

        var rows = target.GetTable(Employees).Rows;
        Assert.Equal(3, rows.Count);
        Assert.Null(rows[0][2]);
        Assert.Equal(1, rows[1][2]);
        Assert.Equal(1, rows[2][2]);
    }

    [Fact]
    public async Task Copy_orders_tables_by_dependencies_even_if_input_is_reversed()
    {
        var source = new FakeProvider(
            CustomerTable([new object?[] { 10, "A" }]),
            OrderTable([new object?[] { 1, 10 }])
        );
        var target = new FakeProvider(CustomerTable([]), OrderTable([]));

        var engine = new DataCopyEngine(source, target);
        var result = await engine.CopyAsync(
            [Config(Orders, "Id"), Config(Customers, "Name")],
            CancellationToken.None
        );

        Assert.True(
            result.Success,
            string.Join("; ", result.Tables.Where(t => !t.Success).Select(t => t.Error))
        );

        var orders = target.GetTable(Orders).Rows;
        Assert.Equal(1, orders[0][1]); // CustomerId 10 → новый ID 1
    }

    [Fact]
    public async Task Copy_preserves_identity_values_when_identity_is_match_column()
    {
        // Поле сопоставления — identity (Id): значения вставляются как есть (SET IDENTITY_INSERT),
        // новые ID не генерируются, счётчик приёмника выравнивается по источнику.
        var source = new FakeProvider(
            CustomerTable([new object?[] { 10, "A" }, new object?[] { 20, "B" }]),
            OrderTable([new object?[] { 1, 10 }, new object?[] { 2, 20 }])
        );
        var target = new FakeProvider(CustomerTable([new object?[] { 1, "PRE" }]), OrderTable([]));

        var engine = new DataCopyEngine(source, target);
        var result = await engine.CopyAsync(
            [Config(Customers, "Id"), Config(Orders, "Id")],
            CancellationToken.None
        );

        Assert.True(
            result.Success,
            string.Join("; ", result.Tables.Where(t => !t.Success).Select(t => t.Error))
        );

        // Клиенты: существующая строка осталась, вставленные сохранили исходные Id 10, 20.
        var rows = target.GetTable(Customers).Rows;
        Assert.Equal(3, rows.Count);
        Assert.Equal(1, rows[0][0]);
        Assert.Equal(10, rows[1][0]);
        Assert.Equal(20, rows[2][0]);

        // FK дочерней таблицы разрешились на сохранённые Id (10 → 10, 20 → 20);
        // identity заказов тоже сохранён.
        var orders = target.GetTable(Orders).Rows;
        Assert.Equal(2, orders.Count);
        Assert.Equal(1, orders[0][0]);
        Assert.Equal(10, orders[0][1]);
        Assert.Equal(2, orders[1][0]);
        Assert.Equal(20, orders[1][1]);

        var customersResult = result.Tables.Single(t => t.Table == Customers);
        Assert.Equal(2, customersResult.Inserted);
        Assert.Equal(0, customersResult.Updated);

        // Счётчик identity приёмника выровнен по источнику (20): следующая автогенерация даст 21.
        Assert.Equal(20, await target.GetIdentityCurrentAsync(Customers));
        var next = target.GetTable(Customers).NewRow(["Name"], ["C"]);
        Assert.Equal(21L, Convert.ToInt64(next[0]));
    }

    [Fact]
    public async Task Copy_updates_existing_row_when_identity_is_match_column()
    {
        // Строка с Id=10 уже есть в приёмнике — по identity-ключу она обновится, Id сохранится.
        var source = new FakeProvider(CustomerTable([new object?[] { 10, "New" }]));
        var target = new FakeProvider(CustomerTable([new object?[] { 10, "Old" }]));

        var engine = new DataCopyEngine(source, target);
        var result = await engine.CopyAsync([Config(Customers, "Id")], CancellationToken.None);

        Assert.True(
            result.Success,
            string.Join("; ", result.Tables.Where(t => !t.Success).Select(t => t.Error))
        );

        var rows = target.GetTable(Customers).Rows;
        Assert.Single(rows);
        Assert.Equal(10, rows[0][0]);
        Assert.Equal("New", rows[0][1]);

        var customersResult = result.Tables.Single(t => t.Table == Customers);
        Assert.Equal(0, customersResult.Inserted);
        Assert.Equal(1, customersResult.Updated);
    }

    [Fact]
    public async Task DryRun_preserves_identity_values_in_preview()
    {
        // В предпросмотре identity тоже «сохраняется»: сопоставление идёт по исходным значениям
        // (не по фиктивным отрицательным), дочерняя таблица разрешается без ошибок.
        var source = new FakeProvider(
            CustomerTable([new object?[] { 10, "A" }]),
            OrderTable([new object?[] { 1, 10 }])
        );
        var target = new FakeProvider(CustomerTable([]), OrderTable([]));

        var engine = new DataCopyEngine(source, target, new CopySettings { DryRun = true });
        var result = await engine.CopyAsync(
            [Config(Customers, "Id"), Config(Orders, "Id")],
            CancellationToken.None
        );

        Assert.True(
            result.Success,
            string.Join("; ", result.Tables.Where(t => !t.Success).Select(t => t.Error))
        );

        Assert.Equal(1, result.Tables.Single(t => t.Table == Customers).Inserted);
        Assert.Equal(1, result.Tables.Single(t => t.Table == Orders).Inserted);
        Assert.Empty(target.GetTable(Customers).Rows);
        Assert.Empty(target.GetTable(Orders).Rows);
    }

    [Fact]
    public async Task Copy_keeps_fk_values_when_parent_table_is_not_selected()
    {
        var source = new FakeProvider(OrderTable([new object?[] { 1, 10 }]));
        var target = new FakeProvider(OrderTable([]));

        var engine = new DataCopyEngine(source, target);
        var result = await engine.CopyAsync([Config(Orders, "Id")], CancellationToken.None);

        Assert.True(result.Success);

        var orders = target.GetTable(Orders).Rows;
        Assert.Equal(10, orders[0][1]); // значение FK сохранено как есть
    }

    [Fact]
    public async Task Copy_reports_missing_mapping_as_table_error()
    {
        // Child ссылается на выбранного родителя, но данных родителя в источнике нет → нет сопоставления.
        var source = new FakeProvider(CustomerTable([]), OrderTable([new object?[] { 1, 999 }]));
        var target = new FakeProvider(CustomerTable([]), OrderTable([]));

        var engine = new DataCopyEngine(source, target);
        var result = await engine.CopyAsync(
            [Config(Customers, "Id"), Config(Orders, "Id")],
            CancellationToken.None
        );

        var ordersResult = result.Tables.Single(t => t.Table == Orders);
        Assert.False(ordersResult.Success);
        Assert.Contains("No ID mapping", ordersResult.Error);
        Assert.Empty(target.GetTable(Orders).Rows); // транзакция откачена
    }

    [Fact]
    public async Task Copy_matches_by_composite_match_columns()
    {
        // Ключ сопоставления — комбинация (Region, Code): одна строка совпадает с целью,
        // вторая отличается только Region (тот же Code), третья содержит NULL в ключе.
        var srcCustomers = new FakeTable
        {
            Meta = TestTables.Table(
                "dbo",
                "Customers",
                [
                    TestTables.Col("Id", identity: true, pk: true),
                    TestTables.Col("Region"),
                    TestTables.Col("Code"),
                    TestTables.Col("Name"),
                ]
            ),
            Rows =
            {
                new object?[] { 100, "EU", "X1", "new" }, // совпадает с (EU, X1) в цели → UPDATE
                new object?[] { 101, "US", "X1", "other" }, // нет в цели → INSERT
                new object?[] { 102, null, "Y", "noKey" }, // NULL в ключе → всегда INSERT
            },
        };
        var tgtCustomers = new FakeTable
        {
            Meta = srcCustomers.Meta,
            Rows = { new object?[] { 5, "EU", "X1", "old" } },
        };

        var source = new FakeProvider(srcCustomers);
        var target = new FakeProvider(tgtCustomers);

        var engine = new DataCopyEngine(source, target);
        var result = await engine.CopyAsync(
            [Config(Customers, "Region", "Code")],
            CancellationToken.None
        );

        Assert.True(
            result.Success,
            string.Join("; ", result.Tables.Where(t => !t.Success).Select(t => t.Error))
        );

        var rows = target.GetTable(Customers).Rows;
        Assert.Equal(3, rows.Count);

        // (EU, X1) обновлён: Id остался 5, Name = new.
        Assert.Equal(5, rows[0][0]);
        Assert.Equal("new", rows[0][3]);

        // (US, X1) вставлен с новым Id, несмотря на совпадающий Code.
        Assert.Equal(6, rows[1][0]);
        Assert.Equal("US", rows[1][1]);

        // (NULL, Y) вставлен: NULL в любом поле ключа исключает сопоставление.
        Assert.Equal(7, rows[2][0]);
        Assert.Null(rows[2][1]);

        var customersResult = result.Tables.Single(t => t.Table == Customers);
        Assert.Equal(2, customersResult.Inserted);
        Assert.Equal(1, customersResult.Updated);
    }

    [Fact]
    public async Task Copy_with_composite_key_substitutes_foreign_keys_for_updated_rows()
    {
        // Родитель сопоставляется по (Region, Code) и обновляется (Id сохраняется);
        // дочерняя таблица должна получить в FK новый/сохранённый ID из сопоставления.
        var srcCustomers = new FakeTable
        {
            Meta = TestTables.Table(
                "dbo",
                "Customers",
                [
                    TestTables.Col("Id", identity: true, pk: true),
                    TestTables.Col("Region"),
                    TestTables.Col("Code"),
                    TestTables.Col("Name"),
                ]
            ),
            Rows = { new object?[] { 100, "EU", "X1", "new" } },
        };
        var srcOrders = new FakeTable
        {
            Meta = TestTables.Table(
                "dbo",
                "Orders",
                [TestTables.Col("Id", identity: true, pk: true), TestTables.Col("CustomerId")],
                TestTables.Fk(
                    "FK_Orders_Customers",
                    "dbo",
                    "Orders",
                    "CustomerId",
                    "dbo",
                    "Customers",
                    "Id"
                )
            ),
            Rows = { new object?[] { 1, 100 } },
        };
        var tgtCustomers = new FakeTable
        {
            Meta = srcCustomers.Meta,
            Rows = { new object?[] { 5, "EU", "X1", "old" } },
        };
        var tgtOrders = new FakeTable { Meta = srcOrders.Meta };

        var source = new FakeProvider(srcCustomers, srcOrders);
        var target = new FakeProvider(tgtCustomers, tgtOrders);

        var engine = new DataCopyEngine(source, target);
        var result = await engine.CopyAsync(
            [Config(Customers, "Region", "Code"), Config(Orders, "Id")],
            CancellationToken.None
        );

        Assert.True(
            result.Success,
            string.Join("; ", result.Tables.Where(t => !t.Success).Select(t => t.Error))
        );

        var customers = target.GetTable(Customers).Rows;
        Assert.Single(customers);
        Assert.Equal(5, customers[0][0]); // Id сохранён при обновлении
        Assert.Equal("new", customers[0][3]);

        var orders = target.GetTable(Orders).Rows;
        Assert.Single(orders);
        Assert.Equal(5, orders[0][1]); // CustomerId 100 → 5

        var customersResult = result.Tables.Single(t => t.Table == Customers);
        Assert.Equal(1, customersResult.Updated);
        Assert.Equal(0, customersResult.Inserted);
        Assert.Equal(1, customersResult.Mapped);
    }

    [Fact]
    public async Task Copy_matches_composite_key_case_insensitively()
    {
        // Значения в цели записаны в другом регистре — комбинация всё равно считается ключом.
        var srcCustomers = new FakeTable
        {
            Meta = TestTables.Table(
                "dbo",
                "Customers",
                [
                    TestTables.Col("Id", identity: true, pk: true),
                    TestTables.Col("Region"),
                    TestTables.Col("Code"),
                    TestTables.Col("Name"),
                ]
            ),
            Rows = { new object?[] { 100, "EU", "X1", "new" } },
        };
        var tgtCustomers = new FakeTable
        {
            Meta = srcCustomers.Meta,
            Rows = { new object?[] { 5, "eu", "x1", "old" } },
        };

        var source = new FakeProvider(srcCustomers);
        var target = new FakeProvider(tgtCustomers);

        var engine = new DataCopyEngine(source, target);
        var result = await engine.CopyAsync(
            [Config(Customers, "Region", "Code")],
            CancellationToken.None
        );

        Assert.True(result.Success);

        var rows = target.GetTable(Customers).Rows;
        Assert.Single(rows);
        Assert.Equal(5, rows[0][0]); // обновление, а не вставка
        Assert.Equal("new", rows[0][3]);

        var customersResult = result.Tables.Single(t => t.Table == Customers);
        Assert.Equal(1, customersResult.Updated);
        Assert.Equal(0, customersResult.Inserted);
    }

    [Fact]
    public async Task Copy_matches_by_composite_key_without_identity()
    {
        // Нет identity: целевой ID сопоставления — первичный ключ.
        var srcCustomers = new FakeTable
        {
            Meta = TestTables.Table(
                "dbo",
                "Customers",
                [
                    TestTables.Col("Id", pk: true),
                    TestTables.Col("Region"),
                    TestTables.Col("Code"),
                    TestTables.Col("Name"),
                ]
            ),
            Rows =
            {
                new object?[] { 10, "EU", "X1", "new" },
                new object?[] { 20, "US", "X2", "other" },
            },
        };
        var tgtCustomers = new FakeTable
        {
            Meta = srcCustomers.Meta,
            Rows = { new object?[] { 1, "EU", "X1", "old" } },
        };

        var source = new FakeProvider(srcCustomers);
        var target = new FakeProvider(tgtCustomers);

        var engine = new DataCopyEngine(source, target);
        var result = await engine.CopyAsync(
            [Config(Customers, "Region", "Code")],
            CancellationToken.None
        );

        Assert.True(
            result.Success,
            string.Join("; ", result.Tables.Where(t => !t.Success).Select(t => t.Error))
        );

        var rows = target.GetTable(Customers).Rows;
        Assert.Equal(2, rows.Count);

        // (EU, X1) обновлён: Id остался 1.
        Assert.Equal(1, rows[0][0]);
        Assert.Equal("new", rows[0][3]);

        // (US, X2) вставлен: Id = значение PK источника (20), identity нет.
        Assert.Equal(20, rows[1][0]);
        Assert.Equal("US", rows[1][1]);
    }

    [Fact]
    public async Task Copy_matches_by_composite_key_without_pk_or_identity()
    {
        // Ни PK, ни identity: целевая колонка сопоставления ID — первое поле сопоставления.
        var srcCustomers = new FakeTable
        {
            Meta = TestTables.Table(
                "dbo",
                "Customers",
                [TestTables.Col("Region"), TestTables.Col("Code"), TestTables.Col("Name")]
            ),
            Rows = { new object?[] { "EU", "X1", "new" }, new object?[] { "US", "X2", "other" } },
        };
        var tgtCustomers = new FakeTable
        {
            Meta = srcCustomers.Meta,
            Rows = { new object?[] { "EU", "X1", "old" } },
        };

        var source = new FakeProvider(srcCustomers);
        var target = new FakeProvider(tgtCustomers);

        var engine = new DataCopyEngine(source, target);
        var result = await engine.CopyAsync(
            [Config(Customers, "Region", "Code")],
            CancellationToken.None
        );

        Assert.True(
            result.Success,
            string.Join("; ", result.Tables.Where(t => !t.Success).Select(t => t.Error))
        );

        var rows = target.GetTable(Customers).Rows;
        Assert.Equal(2, rows.Count);
        Assert.Equal("EU", rows[0][0]);
        Assert.Equal("new", rows[0][2]);
        Assert.Equal("US", rows[1][0]);
        Assert.Equal("other", rows[1][2]);
    }

    [Fact]
    public async Task Copy_reports_error_when_no_match_columns()
    {
        var source = new FakeProvider(CustomerTable([new object?[] { 10, "A" }]));
        var target = new FakeProvider(CustomerTable([]));

        var engine = new DataCopyEngine(source, target);
        var result = await engine.CopyAsync([Config(Customers)], CancellationToken.None);

        var customersResult = result.Tables.Single(t => t.Table == Customers);
        Assert.False(customersResult.Success);
        Assert.Contains("No match columns specified", customersResult.Error);
        Assert.Empty(target.GetTable(Customers).Rows);
    }

    [Fact]
    public async Task Copy_reports_error_when_match_column_does_not_exist()
    {
        var source = new FakeProvider(CustomerTable([new object?[] { 10, "A" }]));
        var target = new FakeProvider(CustomerTable([]));

        var engine = new DataCopyEngine(source, target);
        var result = await engine.CopyAsync([Config(Customers, "Nope")], CancellationToken.None);

        var customersResult = result.Tables.Single(t => t.Table == Customers);
        Assert.False(customersResult.Success);
        Assert.Contains("\"Nope\" not found", customersResult.Error);
        Assert.Empty(target.GetTable(Customers).Rows);
    }

    [Fact]
    public async Task Copy_reports_error_when_match_column_is_computed()
    {
        // Вычисляемая колонка есть в таблице, но не входит в копируемые колонки.
        var srcCustomers = new FakeTable
        {
            Meta = TestTables.Table(
                "dbo",
                "Customers",
                [
                    TestTables.Col("Id", identity: true, pk: true),
                    TestTables.Col("Name"),
                    new DbColumn
                    {
                        Name = "FullName",
                        DataType = "nvarchar(200)",
                        IsComputed = true,
                    },
                ]
            ),
            Rows = { new object?[] { 10, "A", "A" } },
        };
        var tgtCustomers = new FakeTable { Meta = srcCustomers.Meta };

        var source = new FakeProvider(srcCustomers);
        var target = new FakeProvider(tgtCustomers);

        var engine = new DataCopyEngine(source, target);
        var result = await engine.CopyAsync(
            [Config(Customers, "FullName")],
            CancellationToken.None
        );

        var customersResult = result.Tables.Single(t => t.Table == Customers);
        Assert.False(customersResult.Success);
        Assert.Contains("not among the columns being copied", customersResult.Error);
        Assert.Empty(target.GetTable(Customers).Rows);
    }

    [Fact]
    public async Task DryRun_reports_counts_without_modifying_target()
    {
        // Источник: 2 клиента (10, 20) и 3 заказа. Цель: клиент 10 уже есть — он обновится,
        // клиент 20 вставится; заказы все вставятся (в цели их нет).
        var source = new FakeProvider(
            CustomerTable([new object?[] { 10, "A" }, new object?[] { 20, "B" }]),
            OrderTable([new object?[] { 1, 10 }, new object?[] { 2, 20 }, new object?[] { 3, 10 }])
        );
        var target = new FakeProvider(CustomerTable([new object?[] { 10, "OLD" }]), OrderTable([]));

        var engine = new DataCopyEngine(source, target, new CopySettings { DryRun = true });
        var result = await engine.CopyAsync(
            [Config(Customers, "Id"), Config(Orders, "Id")],
            CancellationToken.None
        );

        Assert.True(
            result.Success,
            string.Join("; ", result.Tables.Where(t => !t.Success).Select(t => t.Error))
        );

        var customersResult = result.Tables.Single(t => t.Table == Customers);
        Assert.Equal(2, customersResult.RowsRead);
        Assert.Equal(1, customersResult.Inserted);
        Assert.Equal(1, customersResult.Updated);

        var ordersResult = result.Tables.Single(t => t.Table == Orders);
        Assert.Equal(3, ordersResult.RowsRead);
        Assert.Equal(3, ordersResult.Inserted);
        Assert.Equal(0, ordersResult.Updated);

        // Целевые таблицы не изменены: запись не выполнялась.
        Assert.Single(target.GetTable(Customers).Rows);
        Assert.Empty(target.GetTable(Orders).Rows);
    }

    [Fact]
    public async Task DryRun_counts_match_real_run()
    {
        // Одинаковые данные для двух независимых прогонов: предпросмотр и реальное копирование
        // должны дать одинаковые счётчики по каждой таблице.
        static (FakeProvider Source, FakeProvider Target) Build()
        {
            var source = new FakeProvider(
                CustomerTable([
                    new object?[] { 10, "A" },
                    new object?[] { 20, "B" },
                    new object?[] { 30, "C" },
                ]),
                OrderTable([
                    new object?[] { 1, 10 },
                    new object?[] { 2, 20 },
                    new object?[] { 3, 30 },
                    new object?[] { 4, 10 },
                ])
            );
            var target = new FakeProvider(
                CustomerTable([new object?[] { 10, "OLD" }, new object?[] { 30, "EXIST" }]),
                OrderTable([new object?[] { 1, 10 }])
            );
            return (source, target);
        }

        var configs = new[] { Config(Customers, "Id"), Config(Orders, "Id") };

        var (srcDry, tgtDry) = Build();
        var dryResult = await new DataCopyEngine(
            srcDry,
            tgtDry,
            new CopySettings { DryRun = true }
        ).CopyAsync(configs, CancellationToken.None);

        var (srcReal, tgtReal) = Build();
        var realResult = await new DataCopyEngine(srcReal, tgtReal).CopyAsync(
            configs,
            CancellationToken.None
        );

        Assert.True(
            dryResult.Success,
            string.Join("; ", dryResult.Tables.Where(t => !t.Success).Select(t => t.Error))
        );
        Assert.True(
            realResult.Success,
            string.Join("; ", realResult.Tables.Where(t => !t.Success).Select(t => t.Error))
        );

        foreach (var table in new[] { Customers, Orders })
        {
            var dry = dryResult.Tables.Single(t => t.Table == table);
            var real = realResult.Tables.Single(t => t.Table == table);
            Assert.Equal(real.RowsRead, dry.RowsRead);
            Assert.Equal(real.Inserted, dry.Inserted);
            Assert.Equal(real.Updated, dry.Updated);
            Assert.Equal(real.Mapped, dry.Mapped);
        }

        // Реальное копирование записало строки, предпросмотр — нет.
        Assert.Equal(3, tgtReal.GetTable(Customers).Rows.Count);
        Assert.Equal(4, tgtReal.GetTable(Orders).Rows.Count);
        Assert.Equal(2, tgtDry.GetTable(Customers).Rows.Count);
        Assert.Single(tgtDry.GetTable(Orders).Rows);
    }

    [Fact]
    public async Task DryRun_handles_self_referencing_fk_without_writes()
    {
        var srcEmployees = new FakeTable
        {
            Meta = TestTables.Table(
                "dbo",
                "Employees",
                [
                    TestTables.Col("Id", identity: true, pk: true),
                    TestTables.Col("Name"),
                    TestTables.Col("ManagerId"),
                ],
                TestTables.Fk(
                    "FK_Empl_Empl",
                    "dbo",
                    "Employees",
                    "ManagerId",
                    "dbo",
                    "Employees",
                    "Id"
                )
            ),
            Rows =
            {
                new object?[] { 1, "CEO", null },
                new object?[] { 2, "Dev1", 1 },
                new object?[] { 3, "Dev2", 1 },
            },
        };
        var tgtEmployees = new FakeTable { Meta = srcEmployees.Meta };

        var source = new FakeProvider(srcEmployees);
        var target = new FakeProvider(tgtEmployees);

        var engine = new DataCopyEngine(source, target, new CopySettings { DryRun = true });
        var result = await engine.CopyAsync([Config(Employees, "Id")], CancellationToken.None);

        Assert.True(
            result.Success,
            string.Join("; ", result.Tables.Where(t => !t.Success).Select(t => t.Error))
        );

        var employeesResult = result.Tables.Single(t => t.Table == Employees);
        Assert.Equal(3, employeesResult.RowsRead);
        Assert.Equal(3, employeesResult.Inserted);
        Assert.Equal(0, employeesResult.Updated);

        Assert.Empty(target.GetTable(Employees).Rows);
    }

    [Fact]
    public async Task DryRun_reports_missing_mapping_as_table_error()
    {
        // Та же ситуация, что и в реальном копировании: ссылка на выбранного родителя без данных
        // в источнике должна проявиться в предпросмотре как ошибка таблицы.
        var source = new FakeProvider(CustomerTable([]), OrderTable([new object?[] { 1, 999 }]));
        var target = new FakeProvider(CustomerTable([]), OrderTable([]));

        var engine = new DataCopyEngine(source, target, new CopySettings { DryRun = true });
        var result = await engine.CopyAsync(
            [Config(Customers, "Id"), Config(Orders, "Id")],
            CancellationToken.None
        );

        var ordersResult = result.Tables.Single(t => t.Table == Orders);
        Assert.False(ordersResult.Success);
        Assert.Contains("No ID mapping", ordersResult.Error);
        Assert.Empty(target.GetTable(Orders).Rows);
    }

    [Fact]
    public async Task Engine_errors_are_localized_when_russian_language_is_selected()
    {
        CoreStrings.Language = "ru";
        try
        {
            // Child ссылается на выбранного родителя, но данных родителя в источнике нет → нет сопоставления.
            var source = new FakeProvider(
                CustomerTable([]),
                OrderTable([new object?[] { 1, 999 }])
            );
            var target = new FakeProvider(CustomerTable([]), OrderTable([]));

            var engine = new DataCopyEngine(source, target);
            var result = await engine.CopyAsync(
                [Config(Customers, "Id"), Config(Orders, "Id")],
                CancellationToken.None
            );

            var ordersResult = result.Tables.Single(t => t.Table == Orders);
            Assert.False(ordersResult.Success);
            Assert.Contains("Нет сопоставления ID", ordersResult.Error);
        }
        finally
        {
            CoreStrings.Language = "en";
        }
    }

    [Fact]
    public async Task Unique_index_temporary_duplicate_fails_table_by_default()
    {
        // Уникальное поле Name. В приёмнике устаревшая строка (Id=1, Name="BBB"), источник:
        // строка (2, "BBB") вставляется раньше, чем обновление строки 1 освободит значение
        // "BBB" → вставка роняет уникальный индекс. Без опции таблица падает.
        var source = new FakeProvider(
            UniqueCustomerTable([new object?[] { 2, "BBB" }, new object?[] { 1, "AAA" }])
        );
        var target = new FakeProvider(UniqueCustomerTable([new object?[] { 1, "BBB" }]));

        var engine = new DataCopyEngine(source, target);
        var result = await engine.CopyAsync([Config(Customers, "Id")], CancellationToken.None);

        var customersResult = result.Tables.Single(t => t.Table == Customers);
        Assert.False(customersResult.Success);
        Assert.Contains("UNIQUE constraint violation", customersResult.Error);
        // Ничего не вставлено и не обновлено.
        Assert.Single(target.GetTable(Customers).Rows);
    }

    [Fact]
    public async Task AllowTemporaryUniqueDuplicates_lets_temporary_duplicate_pass_and_rebuilds_index()
    {
        // Та же ситуация, что и в тесте выше, но с включённой опцией: уникальный индекс приёмника
        // отключается на время копирования, транзиентное задвоение проходит, а после обновления
        // дубликатов в данных нет.
        var source = new FakeProvider(
            UniqueCustomerTable([new object?[] { 2, "BBB" }, new object?[] { 1, "AAA" }])
        );
        var target = new FakeProvider(UniqueCustomerTable([new object?[] { 1, "BBB" }]));

        var engine = new DataCopyEngine(
            source,
            target,
            new CopySettings { AllowTemporaryUniqueDuplicates = true }
        );
        var result = await engine.CopyAsync([Config(Customers, "Id")], CancellationToken.None);

        Assert.True(
            result.Success,
            string.Join("; ", result.Tables.Where(t => !t.Success).Select(t => t.Error))
        );

        var customersResult = result.Tables.Single(t => t.Table == Customers);
        Assert.Equal(1, customersResult.Inserted);
        Assert.Equal(1, customersResult.Updated);

        // Итоговые данные без дубликатов: строка 1 обновлена (BBB → AAA), строка 2 вставлена (BBB).
        var rows = target.GetTable(Customers).Rows;
        Assert.Equal(2, rows.Count);
        Assert.Equal("AAA", rows.Single(r => Equals(r[0], 1))[1]);
        Assert.Equal("BBB", rows.Single(r => Equals(r[0], 2))[1]);
        Assert.Single(rows, r => Equals(r[1], "BBB"));
    }
}

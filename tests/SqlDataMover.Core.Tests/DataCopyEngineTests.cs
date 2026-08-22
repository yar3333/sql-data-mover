using SqlDataMover.Core.Copy;
using SqlDataMover.Core.Models;

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

    private static TableCopyConfig Config(DbObjectName table, params string[] matchColumns) =>
        new() { Table = table, MatchColumns = matchColumns };

    [Fact]
    public async Task Copy_remaps_identity_and_substitutes_foreign_keys()
    {
        // Источник: два клиента (Id 10, 20). Цель: уже есть клиент Id=1.
        var source = new FakeProvider(
            CustomerTable([new object?[] { 10, "A" }, new object?[] { 20, "B" }]),
            OrderTable([new object?[] { 1, 10 }, new object?[] { 2, 20 }, new object?[] { 3, 10 }])
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
        Assert.Equal(0, customersResult.Updated); // строк с Id=1 в источнике нет — существующая строка не затронута
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
            [Config(Orders, "Id"), Config(Customers, "Id")],
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
        Assert.Contains("Нет сопоставления ID", ordersResult.Error);
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
        Assert.Contains("Не указаны поля сопоставления", customersResult.Error);
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
        Assert.Contains("«Nope» не найдена", customersResult.Error);
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
        Assert.Contains("не найдено среди копируемых колонок", customersResult.Error);
        Assert.Empty(target.GetTable(Customers).Rows);
    }
}

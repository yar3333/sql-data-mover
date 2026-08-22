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

    private static TableCopyConfig Config(DbObjectName table, string uniqueColumn) =>
        new() { Table = table, UniqueColumn = uniqueColumn };

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
        // Источник: колонки Id (identity), Code (уникальное поле), Name.
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
}

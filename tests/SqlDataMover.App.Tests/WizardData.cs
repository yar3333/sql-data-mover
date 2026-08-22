using SqlDataMover.TestHelpers;

namespace SqlDataMover.App.Tests;

/// <summary>Построение in-memory таблиц для сценариев мастера (source/target с одинаковыми схемами).</summary>
internal static class WizardData
{
    public static FakeTable CustomerTable(params object?[][] rows) =>
        Table(
            "dbo",
            "Customers",
            [TestTables.Col("Id", identity: true, pk: true), TestTables.Col("Name")],
            rows
        );

    public static FakeTable OrderTable(params object?[][] rows) =>
        Table(
            "dbo",
            "Orders",
            [TestTables.Col("Id", identity: true, pk: true), TestTables.Col("CustomerId")],
            rows,
            TestTables.Fk(
                "FK_Orders_Customers",
                "dbo",
                "Orders",
                "CustomerId",
                "dbo",
                "Customers",
                "Id"
            )
        );

    private static FakeTable Table(
        string schema,
        string name,
        SqlDataMover.Core.Models.DbColumn[] columns,
        object?[][] rows,
        params SqlDataMover.Core.Models.DbForeignKey[] fks
    )
    {
        var table = new FakeTable { Meta = TestTables.Table(schema, name, columns, fks) };
        foreach (var row in rows)
            table.Rows.Add(row);
        return table;
    }
}

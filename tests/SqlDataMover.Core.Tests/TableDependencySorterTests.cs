using SqlDataMover.Core.Copy;
using SqlDataMover.Core.Models;

namespace SqlDataMover.Core.Tests;

public class TableDependencySorterTests
{
    private static DbTable Table(string name, params DbForeignKey[] fks) =>
        TestTables.Table("dbo", name, [TestTables.Col("Id", identity: true, pk: true)], fks);

    private static int Position(IReadOnlyList<DbObjectName> order, string name) =>
        order.ToList().IndexOf(new DbObjectName("dbo", name));

    [Fact]
    public void Sort_orders_parents_before_children()
    {
        var a = Table("A");
        var b = Table("B", TestTables.Fk("FK_B_A", "dbo", "B", "AId", "dbo", "A", "Id"));
        var c = Table("C", TestTables.Fk("FK_C_B", "dbo", "C", "BId", "dbo", "B", "Id"));

        var order = TableDependencySorter.Sort([a, b, c]);

        Assert.Equal(3, order.Count);
        Assert.True(Position(order, "A") < Position(order, "B"));
        Assert.True(Position(order, "B") < Position(order, "C"));
    }

    [Fact]
    public void Sort_ignores_self_referencing_fk()
    {
        var employees = Table(
            "Employees",
            TestTables.Fk("FK_Empl_Empl", "dbo", "Employees", "ManagerId", "dbo", "Employees", "Id")
        );

        var order = TableDependencySorter.Sort([employees]);

        Assert.Single(order);
    }

    [Fact]
    public void Sort_ignores_fk_to_table_outside_set()
    {
        var child = Table(
            "Child",
            TestTables.Fk("FK_Child_Parent", "dbo", "Child", "ParentId", "dbo", "Parent", "Id")
        );

        var order = TableDependencySorter.Sort([child]);

        Assert.Single(order);
    }

    [Fact]
    public void Sort_handles_diamond_dependency()
    {
        var a = Table("A");
        var b = Table("B", TestTables.Fk("FK_B_A", "dbo", "B", "AId", "dbo", "A", "Id"));
        var c = Table("C", TestTables.Fk("FK_C_A", "dbo", "C", "AId", "dbo", "A", "Id"));
        var d = Table(
            "D",
            TestTables.Fk("FK_D_B", "dbo", "D", "BId", "dbo", "B", "Id"),
            TestTables.Fk("FK_D_C", "dbo", "D", "CId", "dbo", "C", "Id")
        );

        var order = TableDependencySorter.Sort([d, c, b, a]);

        Assert.Equal(4, order.Count);
        Assert.True(Position(order, "A") < Position(order, "B"));
        Assert.True(Position(order, "A") < Position(order, "C"));
        Assert.True(Position(order, "B") < Position(order, "D"));
        Assert.True(Position(order, "C") < Position(order, "D"));
    }

    [Fact]
    public void Sort_throws_on_cycle()
    {
        var a = Table("A", TestTables.Fk("FK_A_B", "dbo", "A", "BId", "dbo", "B", "Id"));
        var b = Table("B", TestTables.Fk("FK_B_A", "dbo", "B", "AId", "dbo", "A", "Id"));

        Assert.Throws<InvalidOperationException>(() => TableDependencySorter.Sort([a, b]));
    }
}

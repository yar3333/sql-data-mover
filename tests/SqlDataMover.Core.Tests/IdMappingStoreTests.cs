using SqlDataMover.Core.Copy;
using SqlDataMover.Core.Models;

namespace SqlDataMover.Core.Tests;

public class IdMappingStoreTests
{
    private static readonly DbObjectName Table = new("dbo", "Orders");

    [Fact]
    public void Add_then_get_roundtrip()
    {
        var store = new IdMappingStore();

        store.Add(Table, "CustomerId", 10, 5);

        Assert.True(store.TryGet(Table, "CustomerId", 10, out var mapped));
        Assert.Equal(5, mapped);
    }

    [Fact]
    public void String_keys_compared_case_insensitively()
    {
        var store = new IdMappingStore();

        store.Add(Table, "Code", "ABC", 42);

        Assert.True(store.TryGet(Table, "Code", "abc", out var mapped));
        Assert.Equal(42, mapped);
    }

    [Fact]
    public void Null_value_is_not_found()
    {
        var store = new IdMappingStore();

        Assert.False(store.TryGet(Table, "CustomerId", null, out _));
    }

    [Fact]
    public void Different_tables_are_isolated()
    {
        var store = new IdMappingStore();
        var other = new DbObjectName("dbo", "Invoices");

        store.Add(Table, "Id", 1, 2);

        Assert.False(store.TryGet(other, "Id", 1, out _));
    }

    [Fact]
    public void Different_columns_are_isolated()
    {
        var store = new IdMappingStore();

        store.Add(Table, "Id", 1, 2);

        Assert.False(store.TryGet(Table, "Code", 1, out _));
    }
}

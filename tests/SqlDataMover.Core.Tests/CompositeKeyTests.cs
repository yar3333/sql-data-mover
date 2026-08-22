using SqlDataMover.Core;

namespace SqlDataMover.Core.Tests;

public class CompositeKeyTests
{
    [Fact]
    public void Equal_keys_are_equal_and_have_equal_hash_codes()
    {
        var a = new CompositeKey(["EU", "X1"]);
        var b = new CompositeKey(["EU", "X1"]);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Component_order_matters()
    {
        var a = new CompositeKey(["EU", "X1"]);
        var b = new CompositeKey(["X1", "EU"]);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void String_components_are_compared_case_insensitively()
    {
        var a = new CompositeKey(["EU", "X1"]);
        var b = new CompositeKey(["eu", "x1"]);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Keys_with_different_length_are_not_equal()
    {
        var a = new CompositeKey(["EU"]);
        var b = new CompositeKey(["EU", "X1"]);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Null_components_are_supported()
    {
        var a = new CompositeKey(["EU", null]);
        var b = new CompositeKey(["EU", null]);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        var withValue = new CompositeKey(["EU", "X1"]);
        Assert.NotEqual(a, withValue);
    }

    [Fact]
    public void Null_in_different_position_is_not_equal()
    {
        var a = new CompositeKey(["EU", null]);
        var b = new CompositeKey([null, "EU"]);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Not_equal_to_other_objects_or_null()
    {
        var key = new CompositeKey(["EU", "X1"]);

        Assert.False(key.Equals("EU, X1"));
        Assert.False(key.Equals(null));
    }

    [Fact]
    public void Works_as_dictionary_key_with_default_comparer()
    {
        // Так ключи используются в LoadMatchMapAsync и движке: Dictionary<object, object>.
        var map = new Dictionary<object, object> { [new CompositeKey(["EU", "X1"])] = 5 };

        Assert.True(map.ContainsKey(new CompositeKey(["eu", "x1"])));
        Assert.False(map.ContainsKey(new CompositeKey(["EU", "X2"])));
    }

    [Fact]
    public void ToString_joins_components()
    {
        Assert.Equal("EU, X1", new CompositeKey(["EU", "X1"]).ToString());
        Assert.Equal("EU, NULL", new CompositeKey(["EU", null]).ToString());
    }
}

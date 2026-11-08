using XISOStudio.Models;
using Xunit;

namespace XISOStudio.Tests.Models;

/// <summary>Tests the defined members and stable numeric values of the <c>OutputFormat</c> enum.</summary>
public class OutputFormatTests
{
    [Fact]
    public void DefinedMembersHaveStableValues()
    {
        Assert.Equal(0, (int)OutputFormat.Xiso);
        Assert.Equal(1, (int)OutputFormat.Zar);
        Assert.Equal(2, (int)OutputFormat.Cso);
        Assert.Equal(3, (int)OutputFormat.Chd);
        Assert.Equal(4, (int)OutputFormat.Cci);
    }

    [Fact]
    public void DefinedMembersMatchExpectedNamesInOrder()
    {
        Assert.Equal(["Xiso", "Zar", "Cso", "Chd", "Cci"], Enum.GetNames<OutputFormat>());
    }

    [Fact]
    public void EnumContainsExactlyFiveValues()
    {
        var values = Enum.GetValues<OutputFormat>();

        Assert.Equal(5, values.Length);
        Assert.Equal([OutputFormat.Xiso, OutputFormat.Zar, OutputFormat.Cso, OutputFormat.Chd, OutputFormat.Cci],
            values);
    }

    [Theory]
    [InlineData(OutputFormat.Xiso, 0)]
    [InlineData(OutputFormat.Zar, 1)]
    [InlineData(OutputFormat.Cso, 2)]
    [InlineData(OutputFormat.Chd, 3)]
    [InlineData(OutputFormat.Cci, 4)]
    public void CastToIntRoundTrips(OutputFormat format, int expected)
    {
        Assert.Equal(expected, (int)format);
        Assert.Equal(format, (OutputFormat)expected);
    }

    [Fact]
    public void DefaultValueIsXiso()
    {
        Assert.Equal(OutputFormat.Xiso, default);
    }

    [Theory]
    [InlineData("Xiso", OutputFormat.Xiso)]
    [InlineData("Zar", OutputFormat.Zar)]
    [InlineData("Cso", OutputFormat.Cso)]
    [InlineData("Chd", OutputFormat.Chd)]
    [InlineData("Cci", OutputFormat.Cci)]
    public void ParseResolvesDefinedNames(string name, OutputFormat expected)
    {
        Assert.Equal(expected, Enum.Parse<OutputFormat>(name));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(99)]
    public void UndefinedValuesAreNotDefined(int value)
    {
        Assert.False(Enum.IsDefined((OutputFormat)value));
    }

    [Fact]
    public void UnderlyingTypeIsInt()
    {
        Assert.Equal(typeof(int), Enum.GetUnderlyingType(typeof(OutputFormat)));
    }
}
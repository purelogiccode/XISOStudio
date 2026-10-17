using System.Text;
using XISOStudio.Services;
using Xunit;

namespace XISOStudio.Tests.Services;

/// <summary>Tests exception detail formatting, including nested exceptions and indentation, in <c>ExceptionFormatter</c>.</summary>
public class ExceptionFormatterTests
{
    [Fact]
    public void AppendExceptionDetailsWithSingleExceptionFormatsCorrectly()
    {
        var sb = new StringBuilder();
        var exception = new InvalidOperationException("Test message");

        ExceptionFormatter.AppendExceptionDetails(sb, exception);

        var result = sb.ToString();
        Assert.Contains("Type: System.InvalidOperationException", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Message: Test message", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Source:", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("StackTrace:", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppendExceptionDetailsWithNestedExceptionsIncludesInnerException()
    {
        var sb = new StringBuilder();
        var inner = new ArgumentException("Inner error");
        var outer = new InvalidOperationException("Outer error", inner);

        ExceptionFormatter.AppendExceptionDetails(sb, outer);

        var result = sb.ToString();
        Assert.Contains("Type: System.InvalidOperationException", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Message: Outer error", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Inner Exception:", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Type: System.ArgumentException", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Message: Inner error", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppendExceptionDetailsWithLevelIncludesIndentation()
    {
        var sb = new StringBuilder();

        // Use reflection to set inner exception since constructor param is easier
        // Actually let's just use the normal nesting
        var outer = new InvalidOperationException("Outer", new ArgumentException("Inner"));

        ExceptionFormatter.AppendExceptionDetails(sb, outer, 1);

        var result = sb.ToString();
        // Outer should be indented with 2 spaces
        Assert.Contains("  Type: System.InvalidOperationException", result, StringComparison.OrdinalIgnoreCase);
        // Inner should be indented with 4 spaces
        Assert.Contains("    Type: System.ArgumentException", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppendExceptionDetailsThreeLevelNestingIndentsEachLevel()
    {
        var sb = new StringBuilder();
        var exception = new InvalidOperationException("L1",
            new ArgumentException("L2", new FormatException("L3")));

        ExceptionFormatter.AppendExceptionDetails(sb, exception);

        var result = sb.ToString();
        Assert.Contains("Type: System.InvalidOperationException", result, StringComparison.Ordinal);
        Assert.Contains("Inner Exception:", result, StringComparison.Ordinal);
        Assert.Contains("  Type: System.ArgumentException", result, StringComparison.Ordinal);
        Assert.Contains("  Inner Exception:", result, StringComparison.Ordinal);
        Assert.Contains("    Type: System.FormatException", result, StringComparison.Ordinal);
        Assert.Contains("    Message: L3", result, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendExceptionDetailsNestedChainEmitsOneInnerLabelPerLevel()
    {
        var sb = new StringBuilder();
        var exception = new InvalidOperationException("1", new ArgumentException("2", new FormatException("3")));

        ExceptionFormatter.AppendExceptionDetails(sb, exception);

        Assert.Equal(2, CountOccurrences(sb.ToString(), "Inner Exception:"));
    }

    [Fact]
    public void AppendExceptionDetailsWithLevelThreeIndentsBySixSpaces()
    {
        var sb = new StringBuilder();

        ExceptionFormatter.AppendExceptionDetails(sb, new Exception("X"), 3);

        var result = sb.ToString();
        Assert.Contains("      Type: System.Exception", result, StringComparison.Ordinal);
        Assert.Contains("      Message: X", result, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendExceptionDetailsZeroLevelMatchesDefaultLevel()
    {
        var explicitZero = new StringBuilder();
        var implicitLevel = new StringBuilder();
        var exception = new InvalidOperationException("X", new ArgumentException("Y"));

        // The explicit 0 (versus the default) is the contract under test: the two calls
        // must produce identical output, and diverge if the default ever changes.
        // ReSharper disable once RedundantArgumentDefaultValue
        ExceptionFormatter.AppendExceptionDetails(explicitZero, exception, 0);
        ExceptionFormatter.AppendExceptionDetails(implicitLevel, exception);

        Assert.Equal(implicitLevel.ToString(), explicitZero.ToString());
    }

    [Fact]
    public void AppendExceptionDetailsAggregateWithSeveralInnersFormatsIndexedLabels()
    {
        var sb = new StringBuilder();
        var aggregate = new AggregateException("Aggregate failure",
            new IOException("First"), new TimeoutException("Second"), new InvalidCastException("Third"));

        ExceptionFormatter.AppendExceptionDetails(sb, aggregate);

        var result = sb.ToString();
        Assert.Contains("Type: System.AggregateException", result, StringComparison.Ordinal);
        Assert.Contains("Inner Exception [0]:", result, StringComparison.Ordinal);
        Assert.Contains("Inner Exception [1]:", result, StringComparison.Ordinal);
        Assert.Contains("Inner Exception [2]:", result, StringComparison.Ordinal);
        Assert.Contains("  Type: System.IO.IOException", result, StringComparison.Ordinal);
        Assert.Contains("  Type: System.TimeoutException", result, StringComparison.Ordinal);
        Assert.Contains("  Type: System.InvalidCastException", result, StringComparison.Ordinal);
        Assert.Contains("  Message: Third", result, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendExceptionDetailsAggregateEmitsOneLabelPerInnerException()
    {
        var sb = new StringBuilder();
        var aggregate = new AggregateException("Aggregate",
            new InvalidOperationException("A"), new InvalidOperationException("B"),
            new InvalidOperationException("C"), new InvalidOperationException("D"));

        ExceptionFormatter.AppendExceptionDetails(sb, aggregate);

        Assert.Equal(4, CountOccurrences(sb.ToString(), "Inner Exception ["));
    }

    [Fact]
    public void AppendExceptionDetailsAggregateMessageIncludesInnerMessages()
    {
        var sb = new StringBuilder();
        var aggregate = new AggregateException("Aggregate",
            new InvalidOperationException("Alpha"), new InvalidOperationException("Beta"));

        ExceptionFormatter.AppendExceptionDetails(sb, aggregate);

        Assert.Contains("Message: Aggregate (Alpha) (Beta)", sb.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AppendExceptionDetailsAggregateInsideInnerExceptionIsIndented()
    {
        var sb = new StringBuilder();
        var aggregate = new AggregateException("InnerAggregate", new IOException("Leaf"));
        var outer = new InvalidOperationException("Outer", aggregate);

        ExceptionFormatter.AppendExceptionDetails(sb, outer);

        var result = sb.ToString();
        Assert.Contains("Inner Exception:", result, StringComparison.Ordinal);
        Assert.Contains("  Type: System.AggregateException", result, StringComparison.Ordinal);
        Assert.Contains("  Inner Exception [0]:", result, StringComparison.Ordinal);
        Assert.Contains("    Type: System.IO.IOException", result, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendExceptionDetailsAggregateInnerWithOwnInnerFormatsRecursively()
    {
        var sb = new StringBuilder();
        var nested = new Exception("Leaf", new FormatException("DeepLeaf"));
        var aggregate = new AggregateException("Aggregate", nested, new TimeoutException("Other"));

        ExceptionFormatter.AppendExceptionDetails(sb, aggregate);

        var result = sb.ToString();
        Assert.Contains("  Inner Exception:", result, StringComparison.Ordinal);
        Assert.Contains("    Type: System.FormatException", result, StringComparison.Ordinal);
        Assert.Contains("    Message: DeepLeaf", result, StringComparison.Ordinal);
        Assert.Contains("Inner Exception [1]:", result, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendExceptionDetailsNullSourceRendersEmptySourceLine()
    {
        var sb = new StringBuilder();
        var exception = new InvalidOperationException("No source");
        Assert.Null(exception.Source);

        ExceptionFormatter.AppendExceptionDetails(sb, exception);

        var lines = sb.ToString().Split(Environment.NewLine);
        Assert.Contains("Source: ", lines, StringComparer.Ordinal);
    }

    [Fact]
    public void AppendExceptionDetailsNullStackTraceRendersEmptyStackTraceLine()
    {
        var sb = new StringBuilder();
        var exception = new InvalidOperationException("No stack");
        Assert.Null(exception.StackTrace);

        ExceptionFormatter.AppendExceptionDetails(sb, exception);

        var lines = sb.ToString().Split(Environment.NewLine);
        var stackTraceIndex = Array.IndexOf(lines, "StackTrace:");
        Assert.True(stackTraceIndex >= 0);
        Assert.Equal(string.Empty, lines[stackTraceIndex + 1]);
    }

    [Fact]
    public void AppendExceptionDetailsAppendsToExistingBuilderContent()
    {
        var sb = new StringBuilder("PREFIX");

        ExceptionFormatter.AppendExceptionDetails(sb, new InvalidOperationException("X"));

        Assert.StartsWith("PREFIX", sb.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AppendExceptionDetailsPreservesMultilineMessage()
    {
        var sb = new StringBuilder();

        ExceptionFormatter.AppendExceptionDetails(sb, new InvalidOperationException("line1\nline2"));

        Assert.Contains("Message: line1\nline2", sb.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AppendExceptionDetailsFormatsFullExceptionTypeName()
    {
        var sb = new StringBuilder();

        ExceptionFormatter.AppendExceptionDetails(sb, new FormatException("Bad format"));

        Assert.Contains("Type: System.FormatException", sb.ToString(), StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string substring)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(substring, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += substring.Length;
        }

        return count;
    }
}
using DotnetToolkit.McpServer.Validation;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

public sealed class EscalationTableTests
{
    // Conformance C2: every change kind produces requiredLevel >= the §13.2 table value.
    [Theory]
    [InlineData(ChangeKind.Trivia, ValidationLevel.Parse)]
    [InlineData(ChangeKind.Body, ValidationLevel.ProjectCompile)]
    [InlineData(ChangeKind.Signature, ValidationLevel.DependentCompile)]
    [InlineData(ChangeKind.Accessibility, ValidationLevel.DependentCompile)]
    [InlineData(ChangeKind.Inheritance, ValidationLevel.DependentCompile)]
    [InlineData(ChangeKind.Interface, ValidationLevel.DependentCompile)]
    [InlineData(ChangeKind.Attribute, ValidationLevel.DependentCompile)]
    [InlineData(ChangeKind.GenericConstraint, ValidationLevel.DependentCompile)]
    [InlineData(ChangeKind.Nullability, ValidationLevel.DependentCompile)]
    public void EachChangeKindMeetsTableMinimum_C2(ChangeKind kind, ValidationLevel expected)
    {
        Assert.True(EscalationTable.RequiredFor([kind], referencedByTests: false) >= expected);
        Assert.Equal(expected, EscalationTable.LevelFor(kind));
    }

    [Fact]
    public void PatchLevelIsMaxOverChangedSymbols()
    {
        var level = EscalationTable.RequiredForPatch(
        [
            ([ChangeKind.Body], false),
            ([ChangeKind.Signature], false),
            ([ChangeKind.Trivia], false),
        ]);
        Assert.Equal(ValidationLevel.DependentCompile, level);
    }

    [Fact]
    public void TestReferencedChangeEscalatesToTargetedTests()
    {
        Assert.Equal(ValidationLevel.TargetedTests,
            EscalationTable.RequiredFor([ChangeKind.Body], referencedByTests: true));
        Assert.Equal(ValidationLevel.TargetedTests,
            EscalationTable.RequiredFor([ChangeKind.Signature], referencedByTests: true));
        // Trivia is not a real change, so it never forces a test run.
        Assert.Equal(ValidationLevel.Parse,
            EscalationTable.RequiredFor([ChangeKind.Trivia], referencedByTests: true));
    }
}

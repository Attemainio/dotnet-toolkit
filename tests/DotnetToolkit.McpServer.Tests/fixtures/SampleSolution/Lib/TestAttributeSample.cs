namespace Sample.Lib;

// A stand-in for xUnit's real attribute: TestAttributes.IsTestMethod matches on the attribute
// class's simple NAME, so this fixture project (which carries no test-framework reference) can
// still exercise the detection without pulling one in.
public sealed class FactAttribute : System.Attribute
{
}

/// <summary>A [Fact]-attributed method with no static call site, for get_references' testInvocationHint.</summary>
/// <remarks>
/// A real test runner discovers and calls this by reflection, which leaves no call-site edge for
/// any reference index to find — the zero-caller result get_references reports for it is real, but
/// reading it as dead code is the mistake the hint exists to head off.
/// </remarks>
public static class OrphanTestSample
{
    [Fact]
    public static void NeverCalledDirectly()
    {
    }
}

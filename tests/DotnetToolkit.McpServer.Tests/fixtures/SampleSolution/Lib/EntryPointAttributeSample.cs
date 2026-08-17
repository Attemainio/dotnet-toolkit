namespace Sample.Lib;

// A stand-in for the real ModelContextProtocol SDK attribute: EntryPointAttributes matches on the
// attribute class's simple NAME, so this fixture project (which carries no SDK reference) can still
// exercise the detection without pulling one in — same pattern as TestAttributeSample.cs's FactAttribute.
public sealed class McpServerToolAttribute : System.Attribute
{
}

/// <summary>An [McpServerTool]-attributed method with no static call site, for get_references' entryPointHint.</summary>
/// <remarks>
/// The MCP SDK's server host discovers and invokes methods like this by reflection over the assembly,
/// which leaves no call-site edge for any reference index to find — the same shape as the 2026-08-17
/// performance benchmark's Q6, where get_references' zero-caller answer was read as "safe to delete" a
/// live tool entry point.
/// </remarks>
public static class OrphanToolSample
{
    [McpServerTool]
    public static void NeverCalledDirectly()
    {
    }
}

namespace Lib;

/// <summary>
/// Clean on disk. The .editorconfig scopes CS0219 to error and CS0168 to none for this file, and the
/// tests patch the violations in, so the fixture still compiles for every other test.
/// </summary>
public static class SeveritySample
{
    /// <summary>Patched to assign an unused local (CS0219, promoted to error).</summary>
    public static int PromotedWarning() => 0;

    /// <summary>Patched to declare an unused local (CS0168, silenced).</summary>
    public static int SilencedWarning() => 0;
}

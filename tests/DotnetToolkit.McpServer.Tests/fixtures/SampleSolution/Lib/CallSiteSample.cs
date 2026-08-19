namespace Sample.Lib;

/// <summary>
/// A member reached twice from one caller, so get_references has an item whose sites outnumber it —
/// the shape totalSites exists to report, and the one a caller cannot get from totalItems.
/// </summary>
public sealed class CallSiteSample
{
    /// <summary>The target counted at two separate call sites.</summary>
    public int Tick() => 1;

    /// <summary>Calls <see cref="Tick"/> from two lines, so a single item carries two sites.</summary>
    public int Twice()
    {
        var first = Tick();
        return first + Tick();
    }
}

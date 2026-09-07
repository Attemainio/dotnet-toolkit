namespace DotnetToolkit.McpServer.Diagnostics;

/// <summary>The verdict on one thing the verifier checked.</summary>
internal enum CheckStatus
{
    /// <summary>Checked, and correct.</summary>
    Ok,

    /// <summary>Checked and usable, but something about it will bite later.</summary>
    Warn,

    /// <summary>Checked and broken. Any one of these makes the whole run fail.</summary>
    Fail,

    /// <summary>Not applicable here, or not checkable from this process. Never a pass.</summary>
    Skip,
}

/// <summary>One line of the verifier's report.</summary>
/// <param name="Group">The section this belongs under, e.g. <c>environment</c>.</param>
/// <param name="Name">What was checked, as a short stable label.</param>
/// <param name="Status">The verdict.</param>
/// <param name="Detail">What was observed — the value, path or message the verdict is about.</param>
/// <param name="Fix">
/// The concrete repair, when there is one. Present on nearly every failure, because a verifier that
/// reports a wrong path without naming the right one leaves the user doing the arithmetic that produced
/// the mistake in the first place.
/// </param>
/// <remarks>
/// <see cref="CheckStatus.Skip"/> is deliberately distinct from <see cref="CheckStatus.Ok"/>. A run that
/// could not reach the sandbox, or was told not to launch anything, has not verified those things, and
/// reporting them as passes is how a green report comes to mean less than it claims.
/// </remarks>
internal sealed record DoctorCheck(string Group, string Name, CheckStatus Status, string Detail, string? Fix = null)
{
    /// <summary>Builds a passing check.</summary>
    /// <param name="group">The section.</param>
    /// <param name="name">The label.</param>
    /// <param name="detail">What was observed.</param>
    /// <returns>The check.</returns>
    public static DoctorCheck Ok(string group, string name, string detail) =>
        new(group, name, CheckStatus.Ok, detail);

    /// <summary>Builds a warning check.</summary>
    /// <param name="group">The section.</param>
    /// <param name="name">The label.</param>
    /// <param name="detail">What was observed.</param>
    /// <param name="fix">The repair, when there is one.</param>
    /// <returns>The check.</returns>
    public static DoctorCheck Warn(string group, string name, string detail, string? fix = null) =>
        new(group, name, CheckStatus.Warn, detail, fix);

    /// <summary>Builds a failing check.</summary>
    /// <param name="group">The section.</param>
    /// <param name="name">The label.</param>
    /// <param name="detail">What was observed.</param>
    /// <param name="fix">The repair, when there is one.</param>
    /// <returns>The check.</returns>
    public static DoctorCheck Fail(string group, string name, string detail, string? fix = null) =>
        new(group, name, CheckStatus.Fail, detail, fix);

    /// <summary>Builds a not-applicable check.</summary>
    /// <param name="group">The section.</param>
    /// <param name="name">The label.</param>
    /// <param name="detail">Why it did not apply.</param>
    /// <returns>The check.</returns>
    public static DoctorCheck Skip(string group, string name, string detail) =>
        new(group, name, CheckStatus.Skip, detail);
}

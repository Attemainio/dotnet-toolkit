using System.Reflection;

namespace DotnetToolkit.McpServer.Diagnostics;

/// <summary>
/// Identifies the exact build of the server that is running, so a manifest version, a plugin cache and a
/// published binary can be proven to be the same build rather than merely the same version number.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A version number cannot answer the question this exists for.</strong> The manifests, the
/// installed plugin cache and the committed <c>dist/</c> binary all say <c>1.0.1</c> while <c>dist/</c>
/// is a rebuild behind the source — which is exactly the state that made a diagnosis of "the server
/// picks the wrong SDK" impossible to attribute, because source and binary implemented different
/// selection policies under one version. The module version id changes on every compilation and cannot
/// be typed into a manifest by hand, so two artifacts sharing one is real evidence, and two differing
/// is the finding.
/// </para>
/// <para>
/// Reported by <c>ping</c> and by the <c>doctor</c> verb rather than kept internal, because the
/// comparison has to be makeable from inside a live agent session — the place where "which build am I
/// actually talking to" is asked and, before this, could not be answered at all.
/// </para>
/// </remarks>
internal static class BuildIdentity
{
    /// <summary>How many hex characters of the module version id to show.</summary>
    /// <remarks>
    /// Enough to distinguish builds of one plugin by eye, short enough to sit in a one-line <c>ping</c>
    /// reply. Collisions do not matter here: the identifier is compared for equality against another
    /// copy of the same field, never used to look a build up.
    /// </remarks>
    private const int BuildIdLength = 8;

    /// <summary>The running assembly, resolved once.</summary>
    private static readonly Assembly Self = typeof(BuildIdentity).Assembly;

    /// <value>The three-part version the build stamped in, e.g. <c>1.0.1</c>.</value>
    public static string Version { get; } = Self.GetName().Version?.ToString(3) ?? "unknown";

    /// <value>A short, per-compilation identifier for this exact binary.</value>
    public static string Build { get; } =
        Self.ManifestModule.ModuleVersionId.ToString("N")[..BuildIdLength];

    /// <value>The version and build together, as reported to a live session.</value>
    public static string Full { get; } = $"{Version}+build.{Build}";

    /// <value>Where the running assembly was loaded from, or null when it has no file on disk.</value>
    public static string? Location => string.IsNullOrEmpty(Self.Location) ? null : Self.Location;
}

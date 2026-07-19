namespace DotnetToolkit.McpServer.Contracts;

/// <summary>
/// Standard lease outcome for a content-bearing request (spec §8). Given the caller's optional
/// <c>knownVersion</c> and <c>refetch</c> flag, decides whether content must be transmitted.
/// </summary>
public static class Lease
{
    /// <summary>Terse by design: this string is emitted on every lease hit, so its token cost recurs.</summary>
    public const string RefetchHint = "Content omitted; resend with refetch:true if you no longer hold it.";

    public readonly record struct Decision(bool Changed, string? HeldVersion)
    {
        public bool OmitContent => !Changed;
    }

    /// <summary>
    /// <paramref name="current"/> is the live version. Returns <c>Changed=false</c> only when the
    /// caller supplied a <c>knownVersion</c>, did not force <c>refetch</c>, and every supplied layer
    /// still matches — in which case content is omitted and <c>heldVersion</c> echoes what was held.
    /// </summary>
    public static Decision Evaluate(ContentVersion current, string? knownVersion, bool refetch)
    {
        if (refetch || string.IsNullOrWhiteSpace(knownVersion))
            return new Decision(Changed: true, HeldVersion: null);

        var known = ContentVersion.Parse(knownVersion);
        return current.Satisfies(known)
            ? new Decision(Changed: false, HeldVersion: knownVersion)
            : new Decision(Changed: true, HeldVersion: null);
    }
}

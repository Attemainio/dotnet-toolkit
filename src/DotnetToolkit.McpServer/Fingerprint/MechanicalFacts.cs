using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotnetToolkit.McpServer.Fingerprint;

/// <summary>
/// Mechanical facts about a member (spec §9 <c>mechanicalFacts</c>): what it throws, awaits, reads,
/// writes and locks. Every field is derived deterministically from syntax + the semantic model —
/// nothing here is inferred or summarized, so a fact is either present because the code demonstrably
/// does it, or absent. Facts are valid only while the member's <c>body</c> hash is unchanged.
/// </summary>
public sealed record MemberFacts
{
    [JsonPropertyName("implementsMembers")] public IReadOnlyList<string> ImplementsMembers { get; init; } = [];
    [JsonPropertyName("overrides")] public string? Overrides { get; init; }
    [JsonPropertyName("throws")] public IReadOnlyList<string> Throws { get; init; } = [];
    [JsonPropertyName("awaits")] public IReadOnlyList<string> Awaits { get; init; } = [];
    [JsonPropertyName("reads")] public IReadOnlyList<string> Reads { get; init; } = [];
    [JsonPropertyName("writes")] public IReadOnlyList<string> Writes { get; init; } = [];
    [JsonPropertyName("locks")] public IReadOnlyList<string> Locks { get; init; } = [];

    public bool IsEmpty =>
        ImplementsMembers.Count == 0 && Overrides is null && Throws.Count == 0 && Awaits.Count == 0
        && Reads.Count == 0 && Writes.Count == 0 && Locks.Count == 0;
}

public static class MechanicalFactsExtractor
{
    /// <summary>Extracts facts for one member declaration. Returns null when nothing is derivable.</summary>
    public static MemberFacts? Extract(SyntaxNode declaration, ISymbol symbol, SemanticModel model)
    {
        var throws = new SortedSet<string>(StringComparer.Ordinal);
        var awaits = new SortedSet<string>(StringComparer.Ordinal);
        var reads = new SortedSet<string>(StringComparer.Ordinal);
        var writes = new SortedSet<string>(StringComparer.Ordinal);
        var locks = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var node in declaration.DescendantNodes())
        {
            switch (node)
            {
                case ThrowStatementSyntax { Expression: { } thrown }:
                    if (model.GetTypeInfo(thrown).Type is { } thrownType)
                        throws.Add(thrownType.Name);
                    break;

                case ThrowExpressionSyntax throwExpr:
                    if (model.GetTypeInfo(throwExpr.Expression).Type is { } exprType)
                        throws.Add(exprType.Name);
                    break;

                case AwaitExpressionSyntax awaited:
                    if (model.GetSymbolInfo(awaited.Expression).Symbol is { } awaitTarget)
                        awaits.Add(awaitTarget.Name);
                    break;

                case LockStatementSyntax locked:
                    if (model.GetSymbolInfo(locked.Expression).Symbol is { } lockTarget)
                        locks.Add(lockTarget.Name);
                    break;

                case AssignmentExpressionSyntax assignment:
                    // The left side is written; anything else in the member is a read (handled below).
                    if (StateMemberOf(assignment.Left, model, symbol) is { } written)
                        writes.Add(written);
                    break;
            }
        }

        // Reads: state members referenced anywhere that were not recorded as writes.
        foreach (var identifier in declaration.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (StateMemberOf(identifier, model, symbol) is { } read && !writes.Contains(read))
                reads.Add(read);
        }

        var facts = new MemberFacts
        {
            ImplementsMembers = ImplementedMembers(symbol),
            Overrides = OverriddenMember(symbol),
            Throws = [.. throws],
            Awaits = [.. awaits],
            Reads = [.. reads],
            Writes = [.. writes],
            Locks = [.. locks],
        };
        return facts.IsEmpty ? null : facts;
    }

    /// <summary>Field/property of the containing type that this expression refers to, if any.</summary>
    private static string? StateMemberOf(SyntaxNode expression, SemanticModel model, ISymbol owner)
    {
        var target = model.GetSymbolInfo(expression).Symbol;
        if (target is not (IFieldSymbol or IPropertySymbol))
            return null;
        if (target.IsStatic && !SymbolEqualityComparer.Default.Equals(target.ContainingType, owner.ContainingType))
            return null;
        // Only the containing type's own state counts; other objects' members are calls, not state.
        return SymbolEqualityComparer.Default.Equals(target.ContainingType, owner.ContainingType)
            ? target.Name
            : null;
    }

    private static IReadOnlyList<string> ImplementedMembers(ISymbol symbol)
    {
        if (symbol.ContainingType is not { } type)
            return [];
        var implemented = new List<string>();
        foreach (var iface in type.AllInterfaces)
        {
            foreach (var member in iface.GetMembers())
            {
                var impl = type.FindImplementationForInterfaceMember(member);
                if (impl is not null && SymbolEqualityComparer.Default.Equals(impl, symbol))
                    implemented.Add(Workspace.SymbolKey.IdOf(member));
            }
        }
        return implemented;
    }

    private static string? OverriddenMember(ISymbol symbol) => symbol switch
    {
        IMethodSymbol { OverriddenMethod: { } m } => Workspace.SymbolKey.IdOf(m),
        IPropertySymbol { OverriddenProperty: { } p } => Workspace.SymbolKey.IdOf(p),
        IEventSymbol { OverriddenEvent: { } e } => Workspace.SymbolKey.IdOf(e),
        _ => null,
    };
}

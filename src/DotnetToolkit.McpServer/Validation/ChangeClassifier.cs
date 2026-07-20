using System.Text;
using DotnetToolkit.McpServer.Fingerprint;
using DotnetToolkit.McpServer.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotnetToolkit.McpServer.Validation;

/// <summary>
/// Detects, per changed symbol, the mechanical <see cref="ChangeKind"/>s between the base and forked
/// solutions (spec §13.2/§13.4 detectedChanges). Detection is a structural diff of declaration syntax —
/// declaration text (via <see cref="SyntaxFingerprint"/>) plus modifier/attribute/base-list/constraint
/// comparison — never a classifier model, so the escalation routing stays deterministic.
/// </summary>
public static class ChangeClassifier
{
    public sealed record Change(
        string SymbolId, string OldSymbolId, string DisplayString, IReadOnlyList<ChangeKind> Kinds, string Detail,
        string OldVersion, string NewVersion, string ApiImpact);

    public static async Task<List<Change>> DetectAsync(Solution baseSolution, Solution forked, IReadOnlyList<DocumentId> changedDocs)
    {
        var changes = new List<Change>();
        foreach (var docId in changedDocs)
        {
            var baseDoc = baseSolution.GetDocument(docId);
            var newDoc = forked.GetDocument(docId);
            if (baseDoc is null || newDoc is null)
                continue;

            var oldRoot = await baseDoc.GetSyntaxRootAsync();
            var newRoot = await newDoc.GetSyntaxRootAsync();
            if (oldRoot is null || newRoot is null)
                continue;
            var model = await newDoc.GetSemanticModelAsync();
            var baseModel = await baseDoc.GetSemanticModelAsync();

            var oldMap = BuildMap(oldRoot);
            var newMap = BuildMap(newRoot);
            var pairedOld = new HashSet<SyntaxNode>();
            var pairedNew = new HashSet<SyntaxNode>();

            // Exact-key matches: same container + kind + name + arity, differing content.
            foreach (var (key, nw) in newMap)
            {
                if (oldMap.TryGetValue(key, out var old))
                {
                    pairedOld.Add(old);
                    pairedNew.Add(nw);
                    AddChange(changes, model, baseModel, old, nw);
                }
            }

            // Arity/signature changes present as remove+add of the same name (e.g. Spin(int) → Spin(int,int)).
            // Pair unmatched old and new declarations by their name (arity stripped) so the change is detected
            // rather than mistaken for an unrelated add and a removal.
            var removedByName = oldMap
                .Where(kv => !newMap.ContainsKey(kv.Key))
                .GroupBy(kv => NameKey(kv.Key))
                .ToDictionary(g => g.Key, g => g.Select(kv => kv.Value).ToList(), StringComparer.Ordinal);
            foreach (var addedGroup in newMap
                         .Where(kv => !oldMap.ContainsKey(kv.Key))
                         .GroupBy(kv => NameKey(kv.Key)))
            {
                if (!removedByName.TryGetValue(addedGroup.Key, out var removed))
                    continue;
                var added = addedGroup.Select(kv => kv.Value).ToList();
                for (var i = 0; i < Math.Min(added.Count, removed.Count); i++)
                {
                    AddChange(changes, model, baseModel, removed[i], added[i]);
                    pairedOld.Add(removed[i]);
                    pairedNew.Add(added[i]);
                }
            }

            // Renames: changing only the identifier (same container, same signature) leaves both sides
            // unpaired above, since the map key embeds the name and the arity-only pairing groups by name.
            // Pair one remaining removed declaration with one remaining added declaration when they share a
            // signature computed with the name stripped out, so the added side gets a real OldSymbolId to
            // chain against instead of falling through to AddAddedChange's containing-type fallback. Only
            // an UNAMBIGUOUS 1:1 match within the same container qualifies -- two candidates sharing a
            // signature are left as separate remove/add changes rather than guessed at, since nothing here
            // distinguishes "renamed" from "coincidentally similar".
            var unpairedOld = oldMap.Where(kv => !newMap.ContainsKey(kv.Key) && !pairedOld.Contains(kv.Value)).ToList();
            var unpairedNew = newMap.Where(kv => !oldMap.ContainsKey(kv.Key) && !pairedNew.Contains(kv.Value)).ToList();

            var oldBySignature = unpairedOld
                .Select(kv => (Node: kv.Value, Sig: RenameSignatureKey(kv.Key, kv.Value)))
                .Where(t => t.Sig is not null)
                .GroupBy(t => t.Sig)
                .Where(g => g.Count() == 1)
                .ToDictionary(g => g.Key!, g => g.Single().Node, StringComparer.Ordinal);
            var newBySignature = unpairedNew
                .Select(kv => (Node: kv.Value, Sig: RenameSignatureKey(kv.Key, kv.Value)))
                .Where(t => t.Sig is not null)
                .GroupBy(t => t.Sig)
                .Where(g => g.Count() == 1)
                .ToDictionary(g => g.Key!, g => g.Single().Node, StringComparer.Ordinal);

            foreach (var (sig, newNode) in newBySignature)
            {
                if (!oldBySignature.TryGetValue(sig, out var oldNode))
                    continue;
                AddChange(changes, model, baseModel, oldNode, newNode);
                pairedOld.Add(oldNode);
                pairedNew.Add(newNode);
            }
            // Pure additions: a new declaration with no old counterpart at all (not an exact match, not
            // arity-paired with a removal). Left undetected, these made DetectAsync return an empty
            // change list for an addition-only patch, which made the caller compute requiredLevel as
            // parse — the ladder never runs a compiler at that level, so a brand-new member with a
            // genuine compile error could report isSufficient:true. Anchor the change to the containing
            // type: that is the symbol whose lease the caller actually holds when inserting into its
            // body, since the new member itself has no prior version to lease against.
            foreach (var (key, nw) in newMap)
            {
                if (oldMap.ContainsKey(key) || pairedNew.Contains(nw))
                    continue;
                AddAddedChange(changes, model, baseModel, nw, oldMap);
            }

            // Pure removals: an old declaration with no new counterpart at all. Anchor to the removed
            // member's own old symbol/version — that is what a caller doing a targeted removal holds a
            // lease for.
            foreach (var (key, old) in oldMap)
            {
                if (newMap.ContainsKey(key) || pairedOld.Contains(old))
                    continue;
                AddRemovedChange(changes, baseModel, old);
            }
        }
        return changes;
    }

    private static void AddChange(List<Change> changes, SemanticModel? model, SemanticModel? baseModel, SyntaxNode old, SyntaxNode nw)
    {
        var (oldDecl, oldBody) = SyntaxFingerprint.Compute(old);
        var (newDecl, newBody) = SyntaxFingerprint.Compute(nw);
        if (oldDecl == newDecl && oldBody == newBody)
            return; // unchanged (e.g. a sibling within the edited file)

        var symbol = DeclaredSymbol(model, nw);
        if (symbol is null)
            return;

        // The lease the agent held is keyed on the OLD symbol identity; an arity/rename change gives
        // the new symbol a different id, so stale-base checking must use the old id.
        var oldSymbol = DeclaredSymbol(baseModel, old);
        var newSymbolId = SymbolKey.IdOf(symbol);
        var oldSymbolId = oldSymbol is not null ? SymbolKey.IdOf(oldSymbol) : newSymbolId;

        var kinds = Classify(old, nw, oldDecl, newDecl, oldBody, newBody);
        changes.Add(new Change(
            newSymbolId,
            oldSymbolId,
            symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            kinds,
            Describe(kinds),
            Contracts.ContentVersion.Of(oldDecl, oldBody).ToString(),
            Contracts.ContentVersion.Of(newDecl, newBody).ToString(),
            ApiImpactOf(kinds, symbol)));
    }

    /// <summary>The declaration key with any trailing arity (<c>/N</c>) stripped, so overloads/arity
    /// changes of one member share a name for remove+add pairing.</summary>
    /// <summary>
    /// A pairing key for rename detection: container + kind + signature with the identifier itself
    /// stripped out, so <c>RecentLogFor</c> and <c>RecentLogForTest</c> key the same as long as their
    /// parameter/return shape did not also change. Scoped to methods/properties/events -- fields are
    /// excluded because a bare type (e.g. "int") collides across unrelated fields far too often to treat
    /// a match as a reliable rename signal.
    /// </summary>
    private static string? RenameSignatureKey(string mapKey, SyntaxNode node)
    {
        var separator = mapKey.IndexOf("::", StringComparison.Ordinal);
        var container = separator < 0 ? "" : mapKey[..separator];
        var sig = node switch
        {
            MethodDeclarationSyntax m => $"method|{Tokens(m.ReturnType)}|{Tokens(m.TypeParameterList)}|{Tokens(m.ParameterList)}",
            PropertyDeclarationSyntax p => $"prop|{Tokens(p.Type)}",
            EventDeclarationSyntax e => $"event|{Tokens(e.Type)}",
            _ => null,
        };
        return sig is null ? null : container + "::" + sig;
    }

    private static string NameKey(string key)
    {
        var slash = key.LastIndexOf('/');
        return slash < 0 ? key : key[..slash];
    }

    private static List<ChangeKind> Classify(
        SyntaxNode old, SyntaxNode nw, string oldDecl, string newDecl, string? oldBody, string? newBody)
    {
        if (oldDecl == newDecl)
            return [ChangeKind.Body];

        var kinds = new List<ChangeKind>();
        var (oldAccess, oldInherit) = SplitModifiers(Modifiers(old));
        var (newAccess, newInherit) = SplitModifiers(Modifiers(nw));

        if (!oldAccess.SetEquals(newAccess))
            kinds.Add(ChangeKind.Accessibility);
        if (!oldInherit.SetEquals(newInherit))
            kinds.Add(ChangeKind.Inheritance);
        if (Tokens(AttributeLists(old)) != Tokens(AttributeLists(nw)))
            kinds.Add(ChangeKind.Attribute);
        if (Tokens(ConstraintClauses(old)) != Tokens(ConstraintClauses(nw)))
            kinds.Add(ChangeKind.GenericConstraint);
        if (old is BaseTypeDeclarationSyntax to && nw is BaseTypeDeclarationSyntax tn
            && Tokens(to.BaseList) != Tokens(tn.BaseList))
            kinds.Add(ChangeKind.Interface);
        if (SignatureText(old) != SignatureText(nw))
            kinds.Add(ChangeKind.Signature);

        if (kinds.Count == 0)
            kinds.Add(ChangeKind.Signature); // decl moved but sub-part diff was inconclusive
        return kinds;
    }

    private static string ApiImpactOf(IReadOnlyList<ChangeKind> kinds, ISymbol symbol)
    {
        var isPublicSurface = symbol.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected
            or Accessibility.ProtectedOrInternal;
        // A pure addition cannot break an existing caller — nothing depended on a member that did not
        // exist before this patch — so it is treated like Body/Trivia rather than a breaking decl change.
        var declChange = kinds.Any(k => k is not (ChangeKind.Trivia or ChangeKind.Body or ChangeKind.Added));
        if (!declChange)
            return "non-breaking";
        return isPublicSurface ? "breaking-public" : "breaking-internal";
    }

    // ---- declaration map ------------------------------------------------------

    private static Dictionary<string, SyntaxNode> BuildMap(SyntaxNode root)
    {
        var map = new Dictionary<string, SyntaxNode>(StringComparer.Ordinal);
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case BaseTypeDeclarationSyntax or DelegateDeclarationSyntax
                    or BaseMethodDeclarationSyntax or PropertyDeclarationSyntax or EventDeclarationSyntax:
                    map[KeyOf(node)] = node;
                    break;
                case FieldDeclarationSyntax field:
                    foreach (var v in field.Declaration.Variables)
                        map[KeyOf(node) + "$" + v.Identifier.Text] = node;
                    break;
            }
        }
        return map;
    }

    private static string KeyOf(SyntaxNode decl)
    {
        var container = new List<string>();
        foreach (var ancestor in decl.Ancestors())
        {
            switch (ancestor)
            {
                case BaseNamespaceDeclarationSyntax ns:
                    container.Insert(0, ns.Name.ToString());
                    break;
                case BaseTypeDeclarationSyntax t:
                    container.Insert(0, t.Identifier.Text);
                    break;
            }
        }
        var prefix = string.Join('.', container);
        return prefix + "::" + Descriptor(decl);
    }

    private static string Descriptor(SyntaxNode decl) => decl switch
    {
        BaseTypeDeclarationSyntax t => "type " + t.Identifier.Text,
        DelegateDeclarationSyntax d => "delegate " + d.Identifier.Text,
        MethodDeclarationSyntax m => $"method {m.Identifier.Text}/{m.ParameterList.Parameters.Count}",
        ConstructorDeclarationSyntax c => $"ctor/{c.ParameterList.Parameters.Count}",
        PropertyDeclarationSyntax p => "prop " + p.Identifier.Text,
        EventDeclarationSyntax e => "event " + e.Identifier.Text,
        FieldDeclarationSyntax => "field",
        _ => decl.Kind().ToString(),
    };

    // ---- syntax comparison helpers -------------------------------------------

    private static ISymbol? DeclaredSymbol(SemanticModel? model, SyntaxNode node)
    {
        if (model is null)
            return null;
        if (node is FieldDeclarationSyntax field)
            return model.GetDeclaredSymbol(field.Declaration.Variables[0]);
        return model.GetDeclaredSymbol(node);
    }

    private static SyntaxTokenList Modifiers(SyntaxNode node) => node switch
    {
        MemberDeclarationSyntax m => m.Modifiers,
        _ => default,
    };

    private static SyntaxList<AttributeListSyntax> AttributeLists(SyntaxNode node) => node switch
    {
        MemberDeclarationSyntax m => m.AttributeLists,
        _ => default,
    };

    private static SyntaxList<TypeParameterConstraintClauseSyntax> ConstraintClauses(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => m.ConstraintClauses,
        TypeDeclarationSyntax t => t.ConstraintClauses,
        DelegateDeclarationSyntax d => d.ConstraintClauses,
        _ => default,
    };

    /// <summary>Name + parameters + return/element type — the parts that make up a caller-visible signature.</summary>
    private static string SignatureText(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => $"{Tokens(m.ReturnType)} {m.Identifier.Text} {Tokens(m.TypeParameterList)} {Tokens(m.ParameterList)}",
        ConstructorDeclarationSyntax c => $"{c.Identifier.Text} {Tokens(c.ParameterList)}",
        PropertyDeclarationSyntax p => $"{Tokens(p.Type)} {p.Identifier.Text}",
        FieldDeclarationSyntax f => Tokens(f.Declaration.Type),
        DelegateDeclarationSyntax d => $"{Tokens(d.ReturnType)} {d.Identifier.Text} {Tokens(d.ParameterList)}",
        _ => "",
    };

    private static (HashSet<string> Access, HashSet<string> Inherit) SplitModifiers(SyntaxTokenList modifiers)
    {
        var access = new HashSet<string>(StringComparer.Ordinal);
        var inherit = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in modifiers)
        {
            var text = token.Text;
            if (text is "public" or "private" or "protected" or "internal" or "file")
                access.Add(text);
            else if (text is "virtual" or "override" or "abstract" or "sealed" or "new")
                inherit.Add(text);
        }
        return (access, inherit);
    }

    private static string Tokens(SyntaxNode? node)
    {
        if (node is null)
            return "";
        var sb = new StringBuilder();
        foreach (var token in node.DescendantTokens())
        {
            if (sb.Length > 0)
                sb.Append(' ');
            sb.Append(token.Text);
        }
        return sb.ToString();
    }

    private static string Tokens<T>(SyntaxList<T> list) where T : SyntaxNode =>
        string.Join(" ", list.Select(Tokens));

    private static string Describe(IReadOnlyList<ChangeKind> kinds) =>
        string.Join(", ", kinds.Select(k => k.Wire()));
    private static void AddAddedChange(
        List<Change> changes, SemanticModel? model, SemanticModel? baseModel, SyntaxNode nw,
        Dictionary<string, SyntaxNode> oldMap)
    {
        var symbol = DeclaredSymbol(model, nw);
        if (symbol is null)
            return;

        var (newDecl, newBody) = SyntaxFingerprint.Compute(nw);
        var newSymbolId = SymbolKey.IdOf(symbol);
        var newVersion = Contracts.ContentVersion.Of(newDecl, newBody).ToString();

        // The added member has no version of its own to lease against — anchor the stale-base check
        // to its containing type instead, which the caller must already hold to justify writing into
        // its body. A declaration with no type ancestor in this document (a brand-new top-level type)
        // has nothing to anchor to; falling back to its own id makes the stale-base check unsatisfiable
        // by design, so such a patch is refused rather than silently under-validated. Known limitation:
        // adding a wholly new top-level type has no lease path yet.
        var containerNode = nw.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
        var oldSymbolId = newSymbolId;
        var oldVersion = newVersion;
        if (containerNode is not null && oldMap.TryGetValue(KeyOf(containerNode), out var oldContainer))
        {
            var oldContainerSymbol = DeclaredSymbol(baseModel, oldContainer);
            if (oldContainerSymbol is not null)
            {
                var (containerDecl, containerBody) = SyntaxFingerprint.Compute(oldContainer);
                oldSymbolId = SymbolKey.IdOf(oldContainerSymbol);
                oldVersion = Contracts.ContentVersion.Of(containerDecl, containerBody).ToString();
            }
        }

        changes.Add(new Change(
            newSymbolId, oldSymbolId,
            symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            [ChangeKind.Added], "added",
            oldVersion, newVersion,
            ApiImpactOf([ChangeKind.Added], symbol)));
    }

    private static void AddRemovedChange(List<Change> changes, SemanticModel? baseModel, SyntaxNode old)
    {
        var symbol = DeclaredSymbol(baseModel, old);
        if (symbol is null)
            return;

        var (oldDecl, oldBody) = SyntaxFingerprint.Compute(old);
        var oldSymbolId = SymbolKey.IdOf(symbol);
        var oldVersion = Contracts.ContentVersion.Of(oldDecl, oldBody).ToString();

        changes.Add(new Change(
            oldSymbolId, oldSymbolId,
            symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            [ChangeKind.Removed], "removed",
            oldVersion, oldVersion,
            ApiImpactOf([ChangeKind.Removed], symbol)));
    }
}

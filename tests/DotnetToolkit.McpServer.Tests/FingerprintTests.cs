using DotnetToolkit.McpServer.Contracts;
using DotnetToolkit.McpServer.Fingerprint;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

public sealed class FingerprintTests
{
    private static MethodDeclarationSyntax Method(string source) =>
        CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().First();

    private static TypeDeclarationSyntax Type(string source) =>
        CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>().First();

    // Conformance C1: a comment-only or formatting-only edit changes no version-token layer.
    [Fact]
    public void CommentAndFormattingEditsChangeNoLayer_C1()
    {
        var original = SyntaxFingerprint.Compute(Method("""
            class C {
                public int Add(int a, int b) { return a + b; }
            }
            """));

        var reformattedWithComments = SyntaxFingerprint.Compute(Method("""
            class C {
                // adds two numbers
                public int Add(int a,   int b)
                {
                    // running sum
                    return a  +  b;   // done
                }
            }
            """));

        Assert.Equal(original.Decl, reformattedWithComments.Decl);
        Assert.Equal(original.Body, reformattedWithComments.Body);
    }

    [Fact]
    public void BodyChangeMovesBodyLayerButNotDecl()
    {
        var before = SyntaxFingerprint.Compute(Method("class C { int F() { return 1; } }"));
        var after = SyntaxFingerprint.Compute(Method("class C { int F() { return 2; } }"));

        Assert.Equal(before.Decl, after.Decl);
        Assert.NotEqual(before.Body, after.Body);
    }

    [Fact]
    public void SignatureChangeMovesDeclLayer()
    {
        var before = SyntaxFingerprint.Compute(Method("class C { int F(int a) { return a; } }"));
        var after = SyntaxFingerprint.Compute(Method("class C { int F(int a, int b) { return a; } }"));

        Assert.NotEqual(before.Decl, after.Decl);
    }

    [Fact]
    public void TypeHasNoBodyLayerAndIgnoresMemberChanges()
    {
        var before = SyntaxFingerprint.Compute(Type("class C : IBase { void A() {} }"));
        var after = SyntaxFingerprint.Compute(Type("class C : IBase { void A() {} void B() {} }"));

        Assert.Null(before.Body);
        Assert.Equal(before.Decl, after.Decl); // member set is the api layer's concern (Phase 3), not decl
    }

    // Tokens from different tiers carry different layer sets: the agent holds decl|body|refs|api from
    // get_symbol, while the patch classifier computes only the syntax layers. Comparison must be per
    // shared layer — string equality would reject every validly-based patch as stale.
    [Fact]
    public void AgreesWithComparesOnlySharedLayers()
    {
        var syntaxOnly = ContentVersion.Parse("decl:aaaa|body:bbbb");
        var fourLayer = ContentVersion.Parse("decl:aaaa|body:bbbb|refs:cccc|api:dddd");

        Assert.True(syntaxOnly.AgreesWith(fourLayer));   // refs/api simply weren't computed here
        Assert.True(fourLayer.AgreesWith(syntaxOnly));
        Assert.NotEqual(syntaxOnly.ToString(), fourLayer.ToString()); // ...yet they are not equal strings

        // A shared layer that actually moved is still a disagreement.
        Assert.False(syntaxOnly.AgreesWith(ContentVersion.Parse("decl:aaaa|body:ZZZZ")));
        // No overlap at all verifies nothing.
        Assert.False(syntaxOnly.AgreesWith(ContentVersion.Parse("api:dddd")));
    }

    [Fact]
    public void ContentVersionRoundTripsAndComparesPerLayer()
    {
        var full = ContentVersion.Of(decl: "aaaa", body: "bbbb");
        Assert.Equal("decl:aaaa|body:bbbb", full.ToString());
        Assert.Equal(full.ToString(), ContentVersion.Parse(full.ToString()).ToString());

        // A held decl-only lease is satisfied even though the holder never saw the body.
        Assert.True(full.Satisfies(ContentVersion.Parse("decl:aaaa")));
        // A moved body invalidates a held full token.
        Assert.False(full.Satisfies(ContentVersion.Parse("decl:aaaa|body:cccc")));
        // Empty known never satisfies.
        Assert.False(full.Satisfies(ContentVersion.Parse(null)));
    }
}

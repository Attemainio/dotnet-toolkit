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

    /// <summary>
    /// A positional record declared with a semicolon has no open brace, and the absent token reports
    /// SpanStart 0 — so hashing "everything before the open brace" hashed the EMPTY STRING, and every such
    /// record in the solution shared one decl layer, so a change to one record's declaration was
    /// indistinguishable from a change to any other's.
    /// </summary>
    [Fact]
    public void EveryPositionalRecordHasItsOwnDeclLayer()
    {
        var one = SyntaxFingerprint.Compute(Type("public sealed record TypeEntry(int Id, string Name);"));
        var another = SyntaxFingerprint.Compute(Type("public sealed record CallSlice(int Depth);"));
        var sameNameDifferentParameters = SyntaxFingerprint.Compute(Type("public sealed record TypeEntry(int Id);"));

        Assert.NotEqual(one.Decl, another.Decl);
        Assert.NotEqual(one.Decl, sameNameDifferentParameters.Decl);
        Assert.NotEmpty(one.Decl);
        Assert.Null(one.Body);

        // The empty-string hash the bug produced, pinned by value so a regression cannot pass quietly.
        Assert.NotEqual("e3b0c44298fc", one.Decl);
    }

    /// <summary>A record body's members are excluded from decl, exactly as a class's are.</summary>
    [Fact]
    public void ARecordBodysMembersDoNotMoveItsDeclLayer()
    {
        var before = SyntaxFingerprint.Compute(Type("public record R(int A) { void M() {} }"));
        var after = SyntaxFingerprint.Compute(Type("public record R(int A) { void M() {} void N() {} }"));

        Assert.Equal(before.Decl, after.Decl);
    }

}

using Microsoft.CodeAnalysis;

namespace DotnetToolkit.McpServer.Workspace;

/// <summary>Writes Roslyn documents back to the files they came from.</summary>
public static class DocumentWriter
{
    /// <summary>Writes each document's current text to its file path.</summary>
    /// <param name="solution">The solution holding the text to write.</param>
    /// <param name="documents">The documents to write, one per file.</param>
    /// <param name="locator">Resolves a document with no path against the repo root.</param>
    /// <returns>True when every document was written; false if any write failed, in which case some may have been.</returns>
    /// <remarks>
    /// Writes back in the encoding the file was read in. The default here is UTF-8 without a BOM,
    /// which silently strips the BOM Visual Studio puts on files it creates - a whole-file diff on
    /// Windows for a one-identifier rename.
    /// </remarks>
    public static async Task<bool> WriteAsync(Solution solution, IReadOnlyList<DocumentId> documents, SolutionLocator locator)
    {
        try
        {
            foreach (var id in documents)
            {
                var document = solution.GetDocument(id)!;
                var text = await document.GetTextAsync();
                var path = document.FilePath ?? locator.AbsPath(document.Name);
                var encoding = text.Encoding ?? new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                await File.WriteAllTextAsync(path, text.ToString(), encoding);
            }
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

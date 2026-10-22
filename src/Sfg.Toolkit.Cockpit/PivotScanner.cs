using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Sfg.Toolkit.Cockpit;

public sealed record WiredRoute(string Id, string File, int Line);

/// <summary>
/// Syntax-level routing-seam scan: every invocation of the configured route
/// method (default <c>MapRoute</c>) whose first argument is a string literal
/// is one wired route, with file:line evidence. Non-literal route ids are
/// skipped, not guessed. Parse only — no build, no restore, deterministic
/// output for a given tree.
/// </summary>
public static class PivotScanner
{
    private static readonly string[] ExcludedDirs = { "bin", "obj", "node_modules", ".git", ".vs", "packages" };

    public static IReadOnlyList<WiredRoute> Scan(string root, string routeMethod)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Root not found: {root}");
        }

        var wired = new List<WiredRoute>();
        foreach (var file in EnumerateFilesFiltered(root, "*.cs").OrderBy(f => f, StringComparer.Ordinal))
        {
            SyntaxTree tree;
            try
            {
                tree = CSharpSyntaxTree.ParseText(SourceText.From(File.ReadAllText(file), Encoding.UTF8), path: file);
            }
            catch (Exception)
            {
                continue;
            }

            var walker = new PivotWalker(routeMethod, RelativePath(root, file));
            walker.Visit(tree.GetCompilationUnitRoot());
            wired.AddRange(walker.Wired);
        }

        return wired
            .OrderBy(w => w.File, StringComparer.Ordinal)
            .ThenBy(w => w.Line)
            .ThenBy(w => w.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<string> EnumerateFilesFiltered(string directory, string pattern)
    {
        return Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories)
            .Where(path => !IsExcluded(path));
    }

    private static bool IsExcluded(string path)
    {
        foreach (var segment in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            foreach (var excluded in ExcludedDirs)
            {
                if (string.Equals(segment, excluded, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string RelativePath(string root, string path)
    {
        return Path.GetRelativePath(root, path).Replace('\\', '/');
    }

    private sealed class PivotWalker : CSharpSyntaxWalker
    {
        private readonly string _routeMethod;
        private readonly string _file;
        public readonly List<WiredRoute> Wired = new();

        public PivotWalker(string routeMethod, string file)
        {
            _routeMethod = routeMethod;
            _file = file;
        }

        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            if (node.Expression is MemberAccessExpressionSyntax member &&
                string.Equals(member.Name.Identifier.ValueText, _routeMethod, StringComparison.Ordinal) &&
                node.ArgumentList.Arguments.Count > 0 &&
                node.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax literal &&
                literal.Token.IsKind(SyntaxKind.StringLiteralToken))
            {
                Wired.Add(new WiredRoute(
                    literal.Token.ValueText,
                    _file,
                    node.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
            }

            base.VisitInvocationExpression(node);
        }
    }
}

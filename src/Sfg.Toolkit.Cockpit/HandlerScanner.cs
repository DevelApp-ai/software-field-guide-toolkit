using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Sfg.Toolkit.Cockpit;

public sealed record HandlerSite(
    string Name,
    string? Namespace,
    string ProjectPath,
    string File,
    int Line);

/// <summary>
/// Syntax-level handler declaration scan: class and record declarations with
/// their namespaces and nearest project files, file:line evidence included.
/// Missing or ambiguous handler declarations are loud errors, never guesses.
/// </summary>
public static class HandlerScanner
{
    private static readonly string[] ExcludedDirs = { "bin", "obj", "node_modules", ".git", ".vs", "packages" };

    public static IReadOnlyList<HandlerSite> Scan(string root)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Root not found: {root}");
        }

        var sites = new List<HandlerSite>();
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

            var relative = RelativePath(root, file);
            var project = FindProject(root, relative);
            var walker = new DeclarationWalker(relative, project);
            walker.Visit(tree.GetCompilationUnitRoot());
            sites.AddRange(walker.Sites);
        }

        return sites
            .OrderBy(s => s.File, StringComparer.Ordinal)
            .ThenBy(s => s.Line)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Returns the single declaration of <paramref name="name"/>, or throws with evidence.</summary>
    public static HandlerSite Require(IReadOnlyList<HandlerSite> sites, string name, string role, string routeId)
    {
        var matches = sites
            .Where(s => string.Equals(s.Name, name, StringComparison.Ordinal))
            .OrderBy(s => s.File, StringComparer.Ordinal)
            .ThenBy(s => s.Line)
            .ToList();

        if (matches.Count == 0)
        {
            throw new InvalidOperationException(
                $"route '{routeId}': {role} handler '{name}' has no class or record declaration under the root — " +
                "is the handler project inside the scanned root?");
        }

        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                $"route '{routeId}': {role} handler '{name}' is ambiguous — declared {matches.Count} times: " +
                string.Join(", ", matches.Select(m => $"{m.File}:{m.Line}")));
        }

        return matches[0];
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

    private static string FindProject(string root, string file)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(root, file)))!;
        while (directory is not null)
        {
            var csproj = Directory.EnumerateFiles(directory, "*.csproj")
                .OrderBy(p => p, StringComparer.Ordinal)
                .FirstOrDefault();
            if (csproj is not null)
            {
                return csproj;
            }

            if (string.Equals(directory, Path.GetFullPath(root), StringComparison.Ordinal))
            {
                break;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return string.Empty;
    }

    private sealed class DeclarationWalker : CSharpSyntaxWalker
    {
        private readonly string _file;
        private readonly string _project;
        public readonly List<HandlerSite> Sites = new();

        public DeclarationWalker(string file, string project)
        {
            _file = file;
            _project = project;
        }

        public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            Record(node.Identifier.ValueText, NamespaceOf(node), LineOf(node));
            base.VisitClassDeclaration(node);
        }

        public override void VisitRecordDeclaration(RecordDeclarationSyntax node)
        {
            Record(node.Identifier.ValueText, NamespaceOf(node), LineOf(node));
            base.VisitRecordDeclaration(node);
        }

        private void Record(string name, string? ns, int line) => Sites.Add(new HandlerSite(name, ns, _project, _file, line));

        private static int LineOf(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        private static string? NamespaceOf(SyntaxNode node) => node.Ancestors()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .FirstOrDefault()
            ?.Name.ToString();
    }
}

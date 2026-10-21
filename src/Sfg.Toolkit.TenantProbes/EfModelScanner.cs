using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Sfg.Toolkit.TenantProbes;

public sealed record DbContextInfo(
    string Name,
    string? Namespace,
    string ProjectPath,
    string File,
    int Line);

public sealed record EntityInfo(
    string Name,
    string? Namespace,
    string DbSetProperty,
    string File,
    int Line);

public sealed record EfModelScan(
    DbContextInfo Context,
    IReadOnlyList<EntityInfo> Entities,
    string? TargetFramework)
{
    public string ProjectName => Path.GetFileNameWithoutExtension(Context.ProjectPath);
}

/// <summary>
/// Syntax-level EF Core model scanner: finds DbContext subclasses, their
/// <c>DbSet&lt;T&gt;</c> properties, and the declaring namespaces of the
/// entity types. Same truth-from-code rules as the context mapper: parse
/// only, no build, no restore, deterministic output (ordinal sorting).
/// Every inventory entry carries file:line evidence.
/// </summary>
public static class EfModelScanner
{
    private static readonly string[] ExcludedDirs = { "bin", "obj", "node_modules", ".git", ".vs", "packages" };
    private static readonly Regex FirstIdentifier = new("^[A-Za-z_@][A-Za-z0-9_]*", RegexOptions.Compiled);

    public static EfModelScan Scan(string root, TenantProbeConfig config)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Root not found: {root}");
        }

        var contexts = new List<DbContextInfo>();
        var entities = new List<(string Context, string Entity, string DbSetProperty, string File, int Line)>();
        var declarations = new List<(string Name, string? Namespace, string File, int Line)>();

        foreach (var file in EnumerateFilesFiltered(root, "*.cs").OrderBy(f => f, StringComparer.Ordinal))
        {
            ScanFile(root, file, config, contexts, entities, declarations);
        }

        var selected = SelectContext(contexts, config);

        var resolved = new List<EntityInfo>();
        foreach (var entity in entities
                     .Where(e => e.Context == selected.Name)
                     .OrderBy(e => e.Entity, StringComparer.Ordinal)
                     .ThenBy(e => e.DbSetProperty, StringComparer.Ordinal))
        {
            var matches = declarations
                .Where(d => d.Name == entity.Entity)
                .OrderBy(d => d.Namespace, StringComparer.Ordinal)
                .ThenBy(d => d.File, StringComparer.Ordinal)
                .ToList();

            if (matches.Count == 0)
            {
                throw new InvalidOperationException(
                    $"entity '{entity.Entity}' (DbSet at {entity.File}:{entity.Line}) has no class or record " +
                    "declaration under the root — is the entity project inside the scanned root?");
            }

            var namespaces = matches.Select(m => m.Namespace).Distinct().ToList();
            if (namespaces.Count > 1)
            {
                throw new InvalidOperationException(
                    $"entity '{entity.Entity}' is ambiguous: declared in " +
                    string.Join(", ", matches.Select(m => $"{m.Namespace ?? "<global>"} ({m.File}:{m.Line})")));
            }

            resolved.Add(new EntityInfo(entity.Entity, matches[0].Namespace, entity.DbSetProperty, entity.File, entity.Line));
        }

        if (resolved.Count == 0)
        {
            throw new InvalidOperationException(
                $"DbContext '{selected.Name}' ({selected.File}:{selected.Line}) declares no DbSet<> properties — nothing to probe");
        }

        var projectPath = config.Project is not null
            ? Path.GetFullPath(Path.Combine(root, config.Project))
            : selected.ProjectPath;
        if (!File.Exists(projectPath))
        {
            throw new InvalidOperationException($"target project not found: {projectPath}");
        }

        return new EfModelScan(selected, resolved, ReadTargetFramework(projectPath));
    }

    private static DbContextInfo SelectContext(List<DbContextInfo> contexts, TenantProbeConfig config)
    {
        if (config.DbContext is not null)
        {
            var named = contexts
                .Where(c => c.Name == config.DbContext)
                .OrderBy(c => c.File, StringComparer.Ordinal)
                .ToList();
            if (named.Count == 0)
            {
                throw new InvalidOperationException(
                    $"configured dbContext '{config.DbContext}' not found under the root. DbContexts found: " +
                    (contexts.Count == 0
                        ? "none"
                        : string.Join(", ", contexts.Select(c => $"{c.Name} ({c.File}:{c.Line})"))));
            }

            if (named.Count > 1)
            {
                throw new InvalidOperationException(
                    $"configured dbContext '{config.DbContext}' is declared {named.Count} times: " +
                    string.Join(", ", named.Select(c => $"{c.Name} ({c.File}:{c.Line})")));
            }

            return named[0];
        }

        return contexts.Count switch
        {
            0 => throw new InvalidOperationException(
                "no DbContext subclass found under the root — the target must contain a class deriving from DbContext"),
            1 => contexts[0],
            _ => throw new InvalidOperationException(
                "multiple DbContext subclasses found; configure one explicitly (dbContext in tenant-probes.json): " +
                string.Join(", ", contexts.OrderBy(c => c.Name, StringComparer.Ordinal)
                    .Select(c => $"{c.Name} ({c.File}:{c.Line})"))),
        };
    }

    private static string? ReadTargetFramework(string csproj)
    {
        try
        {
            var match = Regex.Match(File.ReadAllText(csproj), @"<TargetFramework>\s*([^<\s]+)\s*</TargetFramework>");
            return match.Success ? match.Groups[1].Value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void ScanFile(
        string root,
        string path,
        TenantProbeConfig config,
        List<DbContextInfo> contexts,
        List<(string Context, string Entity, string DbSetProperty, string File, int Line)> entities,
        List<(string Name, string? Namespace, string File, int Line)> declarations)
    {
        SyntaxTree tree;
        try
        {
            tree = CSharpSyntaxTree.ParseText(SourceText.From(File.ReadAllText(path), Encoding.UTF8), path: path);
        }
        catch (Exception)
        {
            return;
        }

        var relative = RelativePath(root, path);
        var walker = new ModelWalker(config, relative, entities, declarations);
        walker.Visit(tree.GetCompilationUnitRoot());

        foreach (var context in walker.Contexts)
        {
            contexts.Add(new DbContextInfo(
                context.Name,
                context.Namespace,
                FindProject(path) ?? string.Empty,
                relative,
                context.Line));
        }
    }

    private static string? FindProject(string file)
    {
        var directory = Path.GetDirectoryName(file)!;
        while (directory is not null)
        {
            var csproj = Directory.EnumerateFiles(directory, "*.csproj")
                .OrderBy(p => p, StringComparer.Ordinal)
                .FirstOrDefault();
            if (csproj is not null)
            {
                return csproj;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
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

    private sealed record DeclaredContext(string Name, string? Namespace, int Line);

    private sealed class ModelWalker : CSharpSyntaxWalker
    {
        private readonly TenantProbeConfig _config;
        private readonly string _file;
        private readonly List<(string Context, string Entity, string DbSetProperty, string File, int Line)> _entities;
        private readonly List<(string Name, string? Namespace, string File, int Line)> _declarations;
        private readonly List<DeclaredContext> _contexts = new();
        private ClassDeclarationSyntax? _currentContextNode;

        public ModelWalker(
            TenantProbeConfig config,
            string file,
            List<(string Context, string Entity, string DbSetProperty, string File, int Line)> entities,
            List<(string Name, string? Namespace, string File, int Line)> declarations)
        {
            _config = config;
            _file = file;
            _entities = entities;
            _declarations = declarations;
        }

        public IReadOnlyList<DeclaredContext> Contexts => _contexts;

        public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            var isContext = IsContext(node);
            if (isContext)
            {
                _contexts.Add(new DeclaredContext(node.Identifier.ValueText, NamespaceOf(node), LineOf(node)));
            }

            _declarations.Add((node.Identifier.ValueText, NamespaceOf(node), _file, LineOf(node)));

            var parent = _currentContextNode;
            if (isContext)
            {
                _currentContextNode = node;
            }

            if (ReferenceEquals(_currentContextNode, node))
            {
                CollectEntities(node);
            }

            base.VisitClassDeclaration(node);

            _currentContextNode = parent;
        }

        public override void VisitRecordDeclaration(RecordDeclarationSyntax node)
        {
            _declarations.Add((node.Identifier.ValueText, NamespaceOf(node), _file, LineOf(node)));
            base.VisitRecordDeclaration(node);
        }

        private static int LineOf(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        private static string? NamespaceOf(SyntaxNode node) => node.Ancestors()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .FirstOrDefault()
            ?.Name.ToString();

        private bool IsContext(ClassDeclarationSyntax node)
        {
            if (node.BaseList is null)
            {
                return false;
            }

            foreach (var baseType in node.BaseList.Types)
            {
                var first = FirstIdentifier.Match(baseType.Type.ToString().Trim());
                if (!first.Success)
                {
                    continue;
                }

                var name = first.Value.TrimStart('@');
                if (name == "DbContext" || name == _config.DbContext)
                {
                    return true;
                }
            }

            return false;
        }

        private void CollectEntities(ClassDeclarationSyntax node)
        {
            foreach (var member in node.Members)
            {
                if (member is not PropertyDeclarationSyntax property)
                {
                    continue;
                }

                var entity = DbSetEntityName(property);
                if (entity is not null)
                {
                    _entities.Add((node.Identifier.ValueText, entity, property.Identifier.ValueText, _file, LineOf(property)));
                }
            }
        }

        private static string? DbSetEntityName(PropertyDeclarationSyntax property)
        {
            var type = property.Type;
            if (type is QualifiedNameSyntax qualified)
            {
                type = qualified.Right;
            }

            if (type is not GenericNameSyntax generic || generic.Identifier.ValueText != "DbSet")
            {
                return null;
            }

            var argument = generic.TypeArgumentList.Arguments.FirstOrDefault();
            if (argument is null)
            {
                return null;
            }

            var raw = argument.ToString();
            var trimmed = raw.Split('.', '<', '>', ' ')
                .Where(s => s.Length > 0)
                .Last();
            return trimmed;
        }
    }
}

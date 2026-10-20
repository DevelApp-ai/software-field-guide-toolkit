using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Sfg.Toolkit.Core.CodeTruth;

/// <summary>
/// Syntax-level code-truth scanner: discovers the projects of a repository and
/// extracts contexts, declared types, event fields, and identifier usages with
/// file/line evidence. No build, no restore — the scanner parses source only.
/// Output is fully deterministic for a given tree (ordinal sorting everywhere).
/// </summary>
public static class CodeTruthScanner
{
    private static readonly string[] ExcludedDirs = { "bin", "obj", "node_modules", ".git", ".vs", "packages" };
    private static readonly Regex FirstIdentifier = new("^[A-Za-z_@][A-Za-z0-9_]*", RegexOptions.Compiled);

    public static ScanResult Scan(string root, SfgConfig? config = null)
    {
        config ??= SfgConfig.Default;
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Root not found: {root}");
        }

        var projects = DiscoverProjects(root).OrderBy(p => p.Name, StringComparer.Ordinal).ToList();

        var contexts = new List<ContextInfo>();
        var types = new List<TypeRecord>();
        var events = new List<EventRecord>();
        var usages = new List<UsageRecord>();

        foreach (var project in projects)
        {
            contexts.Add(new ContextInfo(
                project.Name,
                config.RoleOf(project.Name),
                project.RelativePath,
                project.TargetFramework,
                project.AssemblyVersion,
                project.ProjectReferences.OrderBy(r => r, StringComparer.Ordinal).ToArray()));

            foreach (var file in ProjectSourceFiles(root, projects, project))
            {
                ScanFile(root, project, file, types, events, usages);
            }
        }

        return new ScanResult(
            root,
            contexts.OrderBy(c => c.Name, StringComparer.Ordinal).ToArray(),
            types.OrderBy(t => t.Project, StringComparer.Ordinal)
                .ThenBy(t => t.File, StringComparer.Ordinal)
                .ThenBy(t => t.Line)
                .ThenBy(t => t.Name, StringComparer.Ordinal)
                .ToArray(),
            events.OrderBy(e => e.Project, StringComparer.Ordinal)
                .ThenBy(e => e.File, StringComparer.Ordinal)
                .ThenBy(e => e.Line)
                .ToArray(),
            usages.OrderBy(u => u.Project, StringComparer.Ordinal)
                .ThenBy(u => u.File, StringComparer.Ordinal)
                .ThenBy(u => u.Line)
                .ThenBy(u => u.Identifier, StringComparer.Ordinal)
                .Distinct()
                .ToArray());
    }

    private sealed record ProjectInfo(
        string Name,
        string DirectoryPath,
        string RelativePath,
        string? TargetFramework,
        string? AssemblyVersion,
        IReadOnlyList<string> ProjectReferences);

    private static IEnumerable<ProjectInfo> DiscoverProjects(string root)
    {
        var result = new List<ProjectInfo>();
        foreach (var csproj in EnumerateFilesFiltered(root, "*.csproj"))
        {
            var name = Path.GetFileNameWithoutExtension(csproj);
            string? targetFramework = null;
            string? assemblyVersion = null;
            var references = new List<string>();
            try
            {
                var doc = XDocument.Load(csproj);
                foreach (var element in doc.Descendants())
                {
                    if (element.Name.LocalName == "TargetFramework" && !string.IsNullOrWhiteSpace(element.Value))
                    {
                        targetFramework = element.Value.Trim();
                    }
                    else if (element.Name.LocalName == "AssemblyVersion" && !string.IsNullOrWhiteSpace(element.Value))
                    {
                        assemblyVersion = element.Value.Trim();
                    }
                    else if (element.Name.LocalName == "ProjectReference")
                    {
                        var include = element.Attribute("Include")?.Value;
                        if (!string.IsNullOrWhiteSpace(include))
                        {
                            references.Add(Path.GetFileNameWithoutExtension(include));
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Unreadable project files are skipped; the scanner is syntax-truth only.
            }

            result.Add(new ProjectInfo(
                name,
                Path.GetDirectoryName(csproj)!,
                RelativePath(root, csproj),
                targetFramework,
                assemblyVersion,
                references));
        }

        return result;
    }

    private static IEnumerable<string> ProjectSourceFiles(string root, List<ProjectInfo> projects, ProjectInfo project)
    {
        return EnumerateFilesFiltered(project.DirectoryPath, "*.cs")
            .Where(file => OwningProject(projects, file).Name == project.Name)
            .OrderBy(f => f, StringComparer.Ordinal);
    }

    private static ProjectInfo OwningProject(List<ProjectInfo> projects, string file)
    {
        var directory = Path.GetDirectoryName(file)!;
        return projects
            .Where(p => directory.StartsWith(p.DirectoryPath + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                        || directory == p.DirectoryPath)
            .OrderByDescending(p => p.DirectoryPath.Length)
            .First();
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

    private static void ScanFile(
        string root,
        ProjectInfo project,
        string path,
        List<TypeRecord> types,
        List<EventRecord> events,
        List<UsageRecord> usages)
    {
        var relative = RelativePath(root, path);
        SyntaxTree tree;
        try
        {
            tree = CSharpSyntaxTree.ParseText(SourceText.From(File.ReadAllText(path), Encoding.UTF8), path: relative);
        }
        catch (Exception)
        {
            return;
        }

        var rootSyntax = tree.GetCompilationUnitRoot();
        var typeCollector = new TypeCollector(project.Name, relative);
        typeCollector.Visit(rootSyntax);
        types.AddRange(typeCollector.Types);
        events.AddRange(typeCollector.Events);

        var usageCollector = new UsageCollector(project.Name, relative);
        usageCollector.Visit(rootSyntax);
        usages.AddRange(usageCollector.Usages);
    }

    private static Accessibility GetAccessibility(SyntaxTokenList modifiers)
    {
        if (modifiers.Any(SyntaxKind.PublicKeyword))
        {
            return Accessibility.Public;
        }

        return Accessibility.Internal;
    }

    private static string? FirstIdent(string text)
    {
        var match = FirstIdentifier.Match(text.Trim());
        return match.Success ? match.Value.TrimStart('@') : null;
    }

    private sealed class TypeCollector : CSharpSyntaxWalker
    {
        private readonly string _project;
        private readonly string _file;
        public readonly List<TypeRecord> Types = new();
        public readonly List<EventRecord> Events = new();

        public TypeCollector(string project, string file)
        {
            _project = project;
            _file = file;
        }

        public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            RecordType(node, TypeKind.Class);
            base.VisitClassDeclaration(node);
        }

        public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
        {
            RecordType(node, TypeKind.Interface);
            base.VisitInterfaceDeclaration(node);
        }

        public override void VisitStructDeclaration(StructDeclarationSyntax node)
        {
            RecordType(node, TypeKind.Struct);
            base.VisitStructDeclaration(node);
        }

        public override void VisitRecordDeclaration(RecordDeclarationSyntax node)
        {
            RecordType(node, TypeKind.Record);
            base.VisitRecordDeclaration(node);
        }

        public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
        {
            RecordType(node, TypeKind.Enum);
            base.VisitEnumDeclaration(node);
        }

        private void RecordType(BaseTypeDeclarationSyntax node, TypeKind kind)
        {
            var ns = node.Ancestors()
                .OfType<BaseNamespaceDeclarationSyntax>()
                .FirstOrDefault()
                ?.Name.ToString() ?? string.Empty;

            var bases = new List<string>();
            var basesRaw = new List<string>();
            var declaration = node as TypeDeclarationSyntax;
            if (declaration?.BaseList is not null)
            {
                foreach (var baseType in declaration.BaseList.Types)
                {
                    var raw = baseType.Type.ToString();
                    basesRaw.Add(raw);
                    var first = FirstIdent(raw);
                    if (first is not null)
                    {
                        bases.Add(first);
                    }
                }
            }

            var members = new List<string>();
            if (declaration is not null)
            {
                foreach (var member in declaration.Members)
                {
                    switch (member)
                    {
                        case PropertyDeclarationSyntax property:
                            members.Add(property.Identifier.ValueText);
                            break;
                        case MethodDeclarationSyntax method:
                            members.Add(method.Identifier.ValueText);
                            break;
                    }
                }
            }

            Types.Add(new TypeRecord(
                node.Identifier.ValueText,
                ns,
                _project,
                kind,
                GetAccessibility(node.Modifiers),
                node.Modifiers.Any(SyntaxKind.AbstractKeyword),
                _file,
                node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                bases,
                basesRaw,
                members));

            foreach (var member in declaration?.Members ?? Enumerable.Empty<MemberDeclarationSyntax>())
            {
                if (member is EventFieldDeclarationSyntax eventField &&
                    eventField.Modifiers.Any(SyntaxKind.PublicKeyword))
                {
                    foreach (var variable in eventField.Declaration.Variables)
                    {
                        Events.Add(new EventRecord(
                            variable.Identifier.ValueText,
                            node.Identifier.ValueText,
                            _project,
                            _file,
                            member.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
                    }
                }
            }
        }
    }

    private sealed class UsageCollector : CSharpSyntaxWalker
    {
        private readonly string _project;
        private readonly string _file;
        public readonly List<UsageRecord> Usages = new();

        public UsageCollector(string project, string file)
        {
            _project = project;
            _file = file;
        }

        public override void VisitIdentifierName(IdentifierNameSyntax node) => Record(node);

        public override void VisitGenericName(GenericNameSyntax node)
        {
            Record(node);
            base.VisitGenericName(node);
        }

        private void Record(SyntaxNode node)
        {
            if (node.Ancestors().OfType<UsingDirectiveSyntax>().Any())
            {
                return;
            }

            // Skip identifiers that are part of a namespace declaration's name.
            var top = node;
            while (top.Parent is NameSyntax)
            {
                top = top.Parent;
            }

            if (top.Parent is BaseNamespaceDeclarationSyntax)
            {
                return;
            }

            Usages.Add(new UsageRecord(
                ((SimpleNameSyntax)node).Identifier.ValueText,
                _project,
                _file,
                node.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
        }
    }
}

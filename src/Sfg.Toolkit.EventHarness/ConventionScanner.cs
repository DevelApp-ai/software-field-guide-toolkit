using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Sfg.Toolkit.EventHarness;

public sealed record ConventionClass(
    string Name,
    string? Namespace,
    string ProjectPath,
    string File,
    int Line);

public sealed record ConventionMember(string Name, string Kind, string File, int Line);

public sealed record ConventionScan(
    ConventionClass Consumer,
    IReadOnlyList<ConventionMember> ConsumerMembers,
    ConventionClass? Outbox,
    IReadOnlyList<ConventionMember> OutboxMembers,
    string TargetProject,
    string TargetFramework);

/// <summary>
/// Syntax-level convention scan (#60): finds the declared consumer class and
/// the outbox class, and verifies the harness conventions are present — the
/// consumer exposes Handle, AppliedEffects, and DeadLetters; the outbox
/// exposes Publish, MarkDispatched, Pending, and Dispatched. Missing or
/// ambiguous declarations are loud errors with file:line evidence, never
/// guesses.
/// </summary>
public static class ConventionScanner
{
    private static readonly string[] ExcludedDirs = { "bin", "obj", "node_modules", ".git", ".vs", "packages" };
    private static readonly string[] ConsumerMemberNames = { "Handle", "AppliedEffects", "DeadLetters" };
    private static readonly string[] OutboxMemberNames = { "Publish", "MarkDispatched", "Pending", "Dispatched" };

    public static ConventionScan Scan(string root, EventHarnessConfig config)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Root not found: {root}");
        }

        var declarations = new List<(ConventionClass Class, List<ConventionMember> Members)>();
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
            var walker = new MemberWalker(relative);
            walker.Visit(tree.GetCompilationUnitRoot());
            foreach (var (name, namespaceName, line, members) in walker.Types)
            {
                declarations.Add((new ConventionClass(name, namespaceName, FindProject(root, relative), relative, line), members));
            }
        }

        var (consumer, consumerMembers) = RequireClass(declarations, config.Consumer, "consumer");
        (ConventionClass Outbox, List<ConventionMember> Members)? outbox = null;
        if (config.Outbox is not null)
        {
            outbox = RequireClass(declarations, config.Outbox, "outbox");
        }

        var targetProject = ResolveTargetProject(root, config, consumer, outbox?.Outbox);
        return new ConventionScan(
            consumer,
            RequireMembers(consumer, consumerMembers, ConsumerMemberNames, "consumer"),
            outbox?.Outbox,
            outbox is null ? Array.Empty<ConventionMember>() : RequireMembers(outbox.Value.Outbox, outbox.Value.Members, OutboxMemberNames, "outbox"),
            targetProject,
            ReadTargetFramework(targetProject) ?? "net8.0");
    }

    private static (ConventionClass Class, List<ConventionMember> Members) RequireClass(
        List<(ConventionClass Class, List<ConventionMember> Members)> declarations,
        string name,
        string role)
    {
        var matches = declarations
            .Where(d => string.Equals(d.Class.Name, name, StringComparison.Ordinal))
            .OrderBy(d => d.Class.File, StringComparer.Ordinal)
            .ThenBy(d => d.Class.Line)
            .ToList();

        if (matches.Count == 0)
        {
            throw new InvalidOperationException(
                $"configured {role} '{name}' has no class or record declaration under the root — " +
                "is the " + role + " project inside the scanned root?");
        }

        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                $"configured {role} '{name}' is ambiguous — declared {matches.Count} times: " +
                string.Join(", ", matches.Select(m => $"{m.Class.File}:{m.Class.Line}")));
        }

        return matches[0];
    }

    private static IReadOnlyList<ConventionMember> RequireMembers(
        ConventionClass @class,
        List<ConventionMember> declared,
        string[] memberNames,
        string role)
    {
        var found = new List<ConventionMember>();
        var missing = new List<string>();
        foreach (var name in memberNames)
        {
            var member = declared.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.Ordinal));
            if (member is null)
            {
                missing.Add(name);
            }
            else
            {
                found.Add(member);
            }
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"{role} '{@class.Name}' ({@class.File}:{@class.Line}) is missing the convention member(s) " +
                string.Join(", ", missing.Select(m => $"'{m}'")) +
                " — the generated conformance suite compiles against these members; " +
                "a missing convention fails loudly here, never silently");
        }

        return found;
    }

    private static string ResolveTargetProject(string root, EventHarnessConfig config, ConventionClass consumer, ConventionClass? outbox)
    {
        if (config.Project is not null)
        {
            var declared = Path.GetFullPath(Path.Combine(root, config.Project));
            if (!File.Exists(declared))
            {
                throw new InvalidOperationException(
                    $"event contract declares project '{config.Project}' but no such project file exists under the root");
            }

            return declared;
        }

        var projects = new List<string> { consumer.ProjectPath };
        if (outbox is not null)
        {
            projects.Add(outbox.ProjectPath);
        }

        var distinct = projects
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        if (distinct.Count == 0)
        {
            throw new InvalidOperationException(
                $"consumer '{consumer.Name}' at {consumer.File}:{consumer.Line} has no project file " +
                "above it — declare the target project with \"project\" in the event contract");
        }

        if (distinct.Count > 1)
        {
            throw new InvalidOperationException(
                "consumer and outbox span more than one project (" +
                string.Join(", ", distinct.Select(Path.GetFileName)) +
                ") — declare the target project with \"project\" in the event contract");
        }

        return distinct[0];
    }

    private static string? ReadTargetFramework(string csproj)
    {
        try
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                File.ReadAllText(csproj), "<TargetFramework>\\s*([^<\\s]+)\\s*</TargetFramework>");
            return match.Success ? match.Groups[1].Value : null;
        }
        catch (Exception)
        {
            return null;
        }
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

    private sealed class MemberWalker : CSharpSyntaxWalker
    {
        private readonly string _file;
        public readonly List<(string Name, string? Namespace, int Line, List<ConventionMember> Members)> Types = new();

        public MemberWalker(string file)
        {
            _file = file;
        }

        public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            Record(node, node.Identifier, node.Members);
            base.VisitClassDeclaration(node);
        }

        public override void VisitRecordDeclaration(RecordDeclarationSyntax node)
        {
            Record(node, node.Identifier, node.Members);
            base.VisitRecordDeclaration(node);
        }

        private void Record(BaseTypeDeclarationSyntax node, SyntaxToken identifier, SyntaxList<MemberDeclarationSyntax> members)
        {
            var collected = new List<ConventionMember>();
            foreach (var member in members)
            {
                switch (member)
                {
                    case MethodDeclarationSyntax method:
                        collected.Add(new ConventionMember(method.Identifier.ValueText, "method", _file, LineOf(method)));
                        break;
                    case PropertyDeclarationSyntax property:
                        collected.Add(new ConventionMember(property.Identifier.ValueText, "property", _file, LineOf(property)));
                        break;
                }
            }

            Types.Add((identifier.ValueText, NamespaceOf(node), LineOf(node), collected));
        }

        private static int LineOf(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        private static string? NamespaceOf(SyntaxNode node) => node.Ancestors()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .FirstOrDefault()
            ?.Name.ToString();
    }
}

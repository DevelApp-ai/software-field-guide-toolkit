using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Sfg.Toolkit.MigrationLint;

/// <summary>
/// Discovers and parses the committed migration history. EF Core style:
/// <c>&lt;14-digit-timestamp&gt;_&lt;Name&gt;.cs</c> files whose <c>Up</c> method
/// drives <c>migrationBuilder</c>. Everything loud: unparseable names, duplicate
/// timestamps, a missing Up method, and unrecognized migrationBuilder calls are
/// errors, never guesses — the order and the classification must be machine-readable.
/// </summary>
public static partial class MigrationParser
{
    [GeneratedRegex(@"^(\d{14})_([A-Za-z][A-Za-z0-9_]*)\.cs$")]
    private static partial Regex FileNamePattern();

    /// <summary>Operations the linter classifies; anything else is a loud error —
    /// an unclassified statement is a guess, and the linter never guesses.</summary>
    private static readonly Dictionary<string, string> PositionalNames = new(StringComparer.Ordinal)
    {
        ["AddColumn"] = "table.column",
        ["DropColumn"] = "table.column",
        ["AlterColumn"] = "table.column",
        ["RenameColumn"] = "table.column",
        ["CreateTable"] = "table",
        ["DropTable"] = "table",
        ["RenameTable"] = "table",
        ["CreateIndex"] = "index",
        ["DropIndex"] = "index",
        ["AddForeignKey"] = "constraint",
        ["DropForeignKey"] = "constraint",
        ["Sql"] = "sql",
    };

    public static IReadOnlyList<MigrationFile> Parse(string migrationsDir)
    {
        if (!Directory.Exists(migrationsDir))
        {
            throw new DirectoryNotFoundException(
                $"migrations directory not found: {migrationsDir} — pass it with --config migration-lint.json (\"migrations\")");
        }

        var files = Directory.GetFiles(migrationsDir, "*.cs")
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Cast<string>()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (files.Length == 0)
        {
            throw new InvalidOperationException(
                $"no migration files in {migrationsDir} — the history is the input, it cannot be empty");
        }

        var parsed = new List<(string Name, string Timestamp, MigrationFile File)>();
        foreach (var name in files)
        {
            var match = FileNamePattern().Match(name);
            if (!match.Success)
            {
                throw new InvalidOperationException(
                    $"migration file '{name}' is not <14-digit-timestamp>_<Name>.cs — EF Core names migrations so the order is machine-readable");
            }

            var timestamp = match.Groups[1].Value;
            var date = DateOnly.ParseExact(timestamp[..8], "yyyyMMdd");
            parsed.Add((name, timestamp, new MigrationFile
            {
                FileName = name,
                Timestamp = timestamp,
                Date = date,
                Statements = ParseFile(Path.Combine(migrationsDir, name), name),
            }));
        }

        foreach (var group in parsed.GroupBy(p => p.Timestamp, StringComparer.Ordinal))
        {
            if (group.Count() > 1)
            {
                throw new InvalidOperationException(
                    $"duplicate migration timestamp {group.Key} ({string.Join(", ", group.Select(g => g.Name))}) — the order is ambiguous");
            }
        }

        return parsed.OrderBy(p => p.Timestamp, StringComparer.Ordinal).Select(p => p.File).ToArray();
    }

    private static IReadOnlyList<MigrationStatement> ParseFile(string path, string fileName)
    {
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path));
        var root = tree.GetCompilationUnitRoot();

        var up = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == "Up");
        if (up is null)
        {
            throw new InvalidOperationException(
                $"migration '{fileName}' has no Up method — the forward migration is what the linter classifies");
        }

        var statements = new List<MigrationStatement>();
        foreach (var invocation in up.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax
                {
                    Expression: IdentifierNameSyntax { Identifier.Text: "migrationBuilder" }
                } access)
            {
                continue;
            }

            var method = access.Name.Identifier.Text;
            var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var args = ExtractArgs(invocation.ArgumentList);
            statements.Add(Classify(fileName, method, line, args));
        }

        return statements;
    }

    private static MigrationStatement Classify(string fileName, string method, int line, CallArgs args)
    {
        if (!PositionalNames.TryGetValue(method, out var shape))
        {
            throw new InvalidOperationException(
                $"unrecognized migrationBuilder call '{method}' at {fileName}:{line} — the linter classifies every " +
                "statement (AddColumn, CreateTable, CreateIndex, AddForeignKey, Sql, RenameColumn, RenameTable, " +
                "AlterColumn, DropColumn, DropTable, DropIndex, DropForeignKey); add the call to the known set " +
                "rather than guessing its classification");
        }

        switch (shape)
        {
            case "table.column":
            {
                var name = args.Get("name", 0);
                var table = args.Get("table", 1);
                var isNullableTightened = method == "AlterColumn" && args.Nullable == false;
                return new MigrationStatement
                {
                    Kind = method switch
                    {
                        "AddColumn" => MigrationKind.Expand,
                        "RenameColumn" => MigrationKind.Migrate,
                        "AlterColumn" => isNullableTightened ? MigrationKind.Contract : MigrationKind.Migrate,
                        _ => MigrationKind.Contract,
                    },
                    Method = method,
                    Object = $"{table}.{name}",
                    RemovesEmittedObject = method is "DropColumn" || isNullableTightened,
                    File = fileName,
                    Line = line,
                };
            }

            case "table":
            {
                var name = args.Get("name", 0);
                return new MigrationStatement
                {
                    Kind = method switch
                    {
                        "CreateTable" => MigrationKind.Expand,
                        _ => MigrationKind.Contract,
                    },
                    Method = method,
                    Object = name,
                    RemovesEmittedObject = method == "DropTable",
                    File = fileName,
                    Line = line,
                };
            }

            case "index" or "constraint":
            {
                var name = args.Get("name", 0);
                return new MigrationStatement
                {
                    Kind = method.StartsWith("Create", StringComparison.Ordinal)
                        ? MigrationKind.Expand
                        : MigrationKind.Contract,
                    Method = method,
                    Object = name,
                    // index and constraint drops are internal wiring: contract,
                    // but never contract on an emitted object — no gate pass covers them
                    RemovesEmittedObject = false,
                    File = fileName,
                    Line = line,
                };
            }

            default: // sql
            {
                return new MigrationStatement
                {
                    Kind = MigrationKind.Migrate,
                    Method = method,
                    Object = "sql",
                    SqlText = args.Literal("sql", 0),
                    File = fileName,
                    Line = line,
                };
            }
        }
    }

    private static CallArgs ExtractArgs(BaseArgumentListSyntax list)
    {
        var args = new CallArgs();
        foreach (var argument in list.Arguments)
        {
            var literal = LiteralValue(argument.Expression);
            if (argument.NameColon is { } colon)
            {
                var name = colon.Name.Identifier.Text;
                if (name == "nullable" && argument.Expression is LiteralExpressionSyntax nullableLiteral)
                {
                    args.Nullable = nullableLiteral.Token.Value switch
                    {
                        true => true,
                        false => false,
                        _ => args.Nullable,
                    };
                    continue;
                }

                args.Add(name, literal);
            }
            else
            {
                args.Add(null, literal);
            }
        }

        return args;
    }

    private static string? LiteralValue(ExpressionSyntax expression)
    {
        if (expression is not LiteralExpressionSyntax literal)
        {
            return null;
        }

        // regular and verbatim string literals share the StringLiteralToken kind
        return literal.Token.IsKind(SyntaxKind.StringLiteralToken)
            ? literal.Token.Value?.ToString()
            : null;
    }

    /// <summary>Arguments as (name, literal): named by EF Core's parameter names,
    /// positional by slot. A non-literal argument keeps a null literal — its
    /// expression text is used for object names, and non-literal SQL is never
    /// inspected (the linter does not guess what a variable contains).</summary>
    private sealed class CallArgs
    {
        private readonly List<(string? Name, string? Literal, string Expression)> _args = new();

        public bool? Nullable { get; set; }

        public void Add(string? name, string? literal)
        {
            // expression text stands in for non-literals: a constant is still a name
            _args.Add((name, literal, literal ?? "<expression>"));
        }

        /// <summary>Named argument by EF Core's name, else the positional slot.</summary>
        public string Get(string name, int index)
        {
            foreach (var (argName, _, expression) in _args)
            {
                if (argName == name)
                {
                    return expression;
                }
            }

            if (index < _args.Count && _args[index].Name is null)
            {
                return _args[index].Expression;
            }

            throw new InvalidOperationException(
                $"migration statement '{name}' argument is missing — EF Core calls are classified from their arguments, never guessed");
        }

        /// <summary>Literal string value or null — non-literal SQL is never guessed about.</summary>
        public string? Literal(string name, int index)
        {
            foreach (var (argName, literal, _) in _args)
            {
                if (argName == name)
                {
                    return literal;
                }
            }

            return index < _args.Count && _args[index].Name is null ? _args[index].Literal : null;
        }
    }
}

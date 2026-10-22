namespace Sfg.Toolkit.Core.CodeTruth;

public enum ContextRole
{
    Host,
    SharedKernel,
    Plugin,
    Other,
}

public enum TypeKind
{
    Class,
    Interface,
    Struct,
    Record,
    Enum,
}

public enum Accessibility
{
    Public,
    Internal,
}

public sealed record ContextInfo(
    string Name,
    ContextRole Role,
    string ProjectFile,
    string? TargetFramework,
    string? AssemblyVersion,
    IReadOnlyList<string> ProjectReferences);

public sealed record TypeRecord(
    string Name,
    string Namespace,
    string Project,
    TypeKind Kind,
    Accessibility Accessibility,
    bool IsAbstract,
    string File,
    int Line,
    IReadOnlyList<string> BaseNames,
    IReadOnlyList<string> BaseNamesRaw,
    IReadOnlyList<string> Members);

public sealed record EventRecord(
    string Name,
    string DeclaringType,
    string Project,
    string File,
    int Line);

public sealed record UsageRecord(
    string Identifier,
    string Project,
    string File,
    int Line);

public sealed record ScanResult(
    string Root,
    IReadOnlyList<ContextInfo> Contexts,
    IReadOnlyList<TypeRecord> Types,
    IReadOnlyList<EventRecord> Events,
    IReadOnlyList<UsageRecord> Usages)
{
    public string RootName => Path.GetFileName(Root.TrimEnd(Path.DirectorySeparatorChar));
}

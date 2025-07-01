public record AssemblyInfo(
    string FileName,
    string FullPath,
    string? AssemblyVersion,
    string? FileVersion,
    string? ProductVersion,
    string? InformationalVersion,
    string SourceArchive,
    string RelativePath);

public record VersionMismatch(
    string FileName,
    List<AssemblyInfo> Occurrences,
    string MismatchType);

public record AssemblyVersionMismatch(
    string FileName,
    List<AssemblyInfo> Occurrences,
    List<string> MismatchTypes,
    IEnumerable<IGrouping<string?, AssemblyInfo>>? AssemblyVersionGroups,
    IEnumerable<IGrouping<string?, AssemblyInfo>>? FileVersionGroups,
    IEnumerable<IGrouping<string?, AssemblyInfo>>? InformationalVersionGroups);

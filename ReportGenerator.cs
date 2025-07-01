using Microsoft.Extensions.Logging;

public class ReportGenerator
{
    private readonly ILogger<ReportGenerator> _logger;

    public ReportGenerator(ILogger<ReportGenerator> logger)
    {
        _logger = logger;
    }

    public void GenerateDetailedReport(List<AssemblyVersionMismatch> mismatches, List<AssemblyInfo> allAssemblies, string reportPath)
    {
        _logger.LogInformation("Generating detailed report to {ReportPath}", reportPath);
        
        using var writer = new StreamWriter(reportPath);
        
        writer.WriteLine("=== .NET SDK VERSION MISMATCH ANALYSIS REPORT ===");
        writer.WriteLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        writer.WriteLine($"Total assemblies analyzed: {allAssemblies.Count}");
        writer.WriteLine($"Assemblies with version mismatches: {mismatches.Count}");
        writer.WriteLine();

        if (mismatches.Count == 0)
        {
            writer.WriteLine("✅ No version mismatches found!");
            return;
        }

        foreach (var mismatch in mismatches.OrderBy(m => m.FileName))
        {
            writer.WriteLine($"🔴 {mismatch.FileName}");
            writer.WriteLine($"   Mismatch Types: {string.Join(", ", mismatch.MismatchTypes)}");
            writer.WriteLine($"   Found in {mismatch.Occurrences.Count} archives");
            writer.WriteLine();

            WriteVersionGroups(writer, "Assembly Versions", mismatch.AssemblyVersionGroups);
            WriteVersionGroups(writer, "File Versions", mismatch.FileVersionGroups);
            WriteVersionGroups(writer, "Informational Versions", mismatch.InformationalVersionGroups);
            
            writer.WriteLine(new string('-', 80));
            writer.WriteLine();
        }

        WriteSummaryStatistics(writer, mismatches, allAssemblies);
    }

    public void GenerateCsvReport(List<AssemblyVersionMismatch> mismatches, string csvReportPath)
    {
        using var writer = new StreamWriter(csvReportPath);
        
        // Write CSV header
        writer.WriteLine("FileName,MismatchTypes,SourceArchive,RelativePath,AssemblyVersion,FileVersion,ProductVersion,InformationalVersion,VersionType,Version,OccurrenceCount");
        
        foreach (var mismatch in mismatches.OrderBy(m => m.FileName))
        {
            var mismatchTypesStr = string.Join("; ", mismatch.MismatchTypes);

            WriteCsvVersionGroups(writer, mismatch, mismatchTypesStr, "AssemblyVersion", mismatch.AssemblyVersionGroups);
            WriteCsvVersionGroups(writer, mismatch, mismatchTypesStr, "FileVersion", mismatch.FileVersionGroups);
            WriteCsvVersionGroups(writer, mismatch, mismatchTypesStr, "InformationalVersion", mismatch.InformationalVersionGroups);
        }
    }

    private void WriteVersionGroups(StreamWriter writer, string label, IEnumerable<IGrouping<string?, AssemblyInfo>>? versionGroups)
    {
        if (versionGroups == null) return;

        writer.WriteLine($"   {label}:");
        foreach (var versionGroup in versionGroups.OrderBy(g => g.Key))
        {
            writer.WriteLine($"     {versionGroup.Key ?? "null"}: {versionGroup.Count()} occurrence(s)");
            foreach (var assembly in versionGroup.OrderBy(a => a.SourceArchive))
            {
                writer.WriteLine($"       📦 {assembly.SourceArchive}");
                writer.WriteLine($"          Path: {assembly.RelativePath}");
            }
        }
        writer.WriteLine();
    }

    private void WriteCsvVersionGroups(StreamWriter writer, AssemblyVersionMismatch mismatch, string mismatchTypesStr, 
        string versionType, IEnumerable<IGrouping<string?, AssemblyInfo>>? versionGroups)
    {
        if (versionGroups == null) return;

        foreach (var versionGroup in versionGroups)
        {
            foreach (var assembly in versionGroup)
            {
                writer.WriteLine($"{EscapeCsvField(assembly.FileName)}," +
                               $"{EscapeCsvField(mismatchTypesStr)}," +
                               $"{EscapeCsvField(assembly.SourceArchive)}," +
                               $"{EscapeCsvField(assembly.RelativePath)}," +
                               $"{EscapeCsvField(assembly.AssemblyVersion ?? "")}," +
                               $"{EscapeCsvField(assembly.FileVersion ?? "")}," +
                               $"{EscapeCsvField(assembly.ProductVersion ?? "")}," +
                               $"{EscapeCsvField(assembly.InformationalVersion ?? "")}," +
                               $"{versionType}," +
                               $"{EscapeCsvField(versionGroup.Key ?? "")}," +
                               $"{versionGroup.Count()}");
            }
        }
    }

    private void WriteSummaryStatistics(StreamWriter writer, List<AssemblyVersionMismatch> mismatches, List<AssemblyInfo> allAssemblies)
    {
        writer.WriteLine("=== SUMMARY STATISTICS ===");
        writer.WriteLine($"Archives processed: {allAssemblies.Select(a => a.SourceArchive).Distinct().Count()}");
        writer.WriteLine($"Unique assembly names: {allAssemblies.Select(a => a.FileName).Distinct().Count()}");
        writer.WriteLine($"Assemblies with version mismatches: {mismatches.Count}");
        
        var topMismatches = mismatches
            .OrderByDescending(m => m.Occurrences.Count)
            .Take(10)
            .ToList();

        if (topMismatches.Any())
        {
            writer.WriteLine();
            writer.WriteLine("Top 10 assemblies by archive count:");
            foreach (var mismatch in topMismatches)
            {
                writer.WriteLine($"  {mismatch.FileName}: {mismatch.Occurrences.Count} archives");
            }
        }
    }

    private string EscapeCsvField(string field)
    {
        if (field.Contains('"') || field.Contains(',') || field.Contains('\n') || field.Contains('\r'))
        {
            return $"\"{field.Replace("\"", "\"\"")}\"";
        }
        return field;
    }
}

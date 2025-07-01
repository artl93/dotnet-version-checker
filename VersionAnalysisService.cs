using Microsoft.Extensions.Logging;

public class VersionAnalysisService
{
    private readonly ILogger _logger;

    public VersionAnalysisService(ILogger logger)
    {
        _logger = logger;
    }

    public List<AssemblyVersionMismatch> FindVersionMismatches(List<AssemblyInfo> allAssemblies)
    {
        _logger.LogInformation("Analyzing version mismatches...");
        
        var assemblyGroups = allAssemblies
            .GroupBy(a => a.FileName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();

        _logger.LogInformation("Found {Count} assemblies that appear in multiple archives", assemblyGroups.Count);

        var assembliesWithMismatches = new List<AssemblyVersionMismatch>();

        foreach (var group in assemblyGroups)
        {
            var assemblies = group.ToList();
            var hasVersionMismatch = false;
            var mismatchTypes = new List<string>();
            
            // Check for assembly version mismatches
            var assemblyVersions = assemblies
                .Where(a => !string.IsNullOrEmpty(a.AssemblyVersion))
                .GroupBy(a => a.AssemblyVersion)
                .Where(g => g.Any())
                .ToList();
            
            if (assemblyVersions.Count > 1)
            {
                hasVersionMismatch = true;
                mismatchTypes.Add("Assembly Version");
            }

            // Check for file version mismatches
            var fileVersions = assemblies
                .Where(a => !string.IsNullOrEmpty(a.FileVersion))
                .GroupBy(a => a.FileVersion)
                .Where(g => g.Any())
                .ToList();
            
            if (fileVersions.Count > 1)
            {
                hasVersionMismatch = true;
                mismatchTypes.Add("File Version");
            }

            // Check for informational version mismatches
            var infoVersions = assemblies
                .Where(a => !string.IsNullOrEmpty(a.InformationalVersion))
                .GroupBy(a => a.InformationalVersion)
                .Where(g => g.Any())
                .ToList();
            
            if (infoVersions.Count > 1)
            {
                hasVersionMismatch = true;
                mismatchTypes.Add("Informational Version");
            }

            // Only add to report if there are actual version mismatches
            if (hasVersionMismatch)
            {
                assembliesWithMismatches.Add(new AssemblyVersionMismatch(
                    group.Key,
                    assemblies,
                    mismatchTypes,
                    assemblyVersions.Count > 1 ? assemblyVersions : null,
                    fileVersions.Count > 1 ? fileVersions : null,
                    infoVersions.Count > 1 ? infoVersions : null
                ));
            }
        }

        return assembliesWithMismatches;
    }

    public void ReportResults(List<AssemblyVersionMismatch> mismatches, List<AssemblyInfo> allAssemblies)
    {
        _logger.LogInformation("=== VERSION MISMATCH ANALYSIS RESULTS ===");
        _logger.LogInformation("Total assemblies analyzed: {Count}", allAssemblies.Count);
        _logger.LogInformation("Assemblies with version mismatches: {Count}", mismatches.Count);

        if (mismatches.Count == 0)
        {
            _logger.LogInformation("✅ No version mismatches found!");
            return;
        }

        _logger.LogInformation("");
        
        foreach (var mismatch in mismatches.OrderBy(m => m.FileName))
        {
            _logger.LogWarning("🔴 {FileName} has {MismatchTypes} across {Count} archives:",
                mismatch.FileName,
                string.Join(", ", mismatch.MismatchTypes),
                mismatch.Occurrences.Count);

            // Group by version type and show each version with its occurrences
            LogVersionGroups("Assembly Versions", mismatch.AssemblyVersionGroups);
            LogVersionGroups("File Versions", mismatch.FileVersionGroups);
            LogVersionGroups("Informational Versions", mismatch.InformationalVersionGroups);
            
            _logger.LogInformation("");
        }

        // Generate summary reports
        var reportPath = Path.Combine(Directory.GetCurrentDirectory(), "version-mismatch-report.txt");
        var csvReportPath = Path.Combine(Directory.GetCurrentDirectory(), "version-mismatch-report.csv");
        
        var reportGenerator = new ReportGenerator();
        reportGenerator.GenerateDetailedReport(mismatches, allAssemblies, reportPath);
        reportGenerator.GenerateCsvReport(mismatches, csvReportPath);
        
        _logger.LogInformation("📄 Detailed report saved to: {ReportPath}", reportPath);
        _logger.LogInformation("📊 CSV report saved to: {CsvReportPath}", csvReportPath);
    }

    private void LogVersionGroups(string label, IEnumerable<IGrouping<string?, AssemblyInfo>>? versionGroups)
    {
        if (versionGroups == null) return;

        _logger.LogWarning("  {Label}:", label);
        foreach (var versionGroup in versionGroups.OrderBy(g => g.Key))
        {
            _logger.LogWarning("    {Version}: {Count} occurrence(s)", 
                versionGroup.Key ?? "null", versionGroup.Count());
            foreach (var assembly in versionGroup.Take(3)) // Show first 3 archives for brevity
            {
                _logger.LogWarning("      📦 {Archive} ({Path})", 
                    assembly.SourceArchive, assembly.RelativePath);
            }
            if (versionGroup.Count() > 3)
            {
                _logger.LogWarning("      ... and {More} more", versionGroup.Count() - 3);
            }
        }
    }
}

using Microsoft.Extensions.Logging;

public class DotNetVersionAnalyzer
{
    private readonly ILogger _logger;
    private readonly string _tempPath;
    private readonly ArchiveExtractionService _extractionService;
    private readonly AssemblyAnalysisService _assemblyService;
    private readonly VersionAnalysisService _versionService;

    public DotNetVersionAnalyzer(ILogger logger, string tempPath)
    {
        _logger = logger;
        _tempPath = tempPath;
        _extractionService = new ArchiveExtractionService(logger);
        _assemblyService = new AssemblyAnalysisService(logger);
        _versionService = new VersionAnalysisService(logger);
    }

    public async Task AnalyzeAsync(string assetsPath, int maxArchives = int.MaxValue)
    {
        var archiveFiles = _extractionService.GetArchiveFiles(assetsPath);
        if (maxArchives < archiveFiles.Count)
        {
            archiveFiles = archiveFiles.Take(maxArchives).ToList();
        }
        
        _logger.LogInformation("Found {Count} archive files to process", archiveFiles.Count);
        
        var startTime = DateTime.Now;

        // Step 1: Extract all archives in parallel
        await _extractionService.ExtractArchivesAsync(archiveFiles, _tempPath);
        var extractionTime = DateTime.Now - startTime;
        _logger.LogInformation("⏱️ Extraction completed in {Duration}", extractionTime);

        // Step 2: Analyze all extracted assemblies
        var analysisStart = DateTime.Now;
        var allAssemblies = await _assemblyService.AnalyzeAssembliesAsync(_tempPath);
        var analysisTime = DateTime.Now - analysisStart;
        _logger.LogInformation("⏱️ Assembly analysis completed in {Duration}", analysisTime);

        // Step 3: Find version mismatches
        var mismatchStart = DateTime.Now;
        var mismatches = _versionService.FindVersionMismatches(allAssemblies);
        var mismatchTime = DateTime.Now - mismatchStart;
        _logger.LogInformation("⏱️ Version mismatch analysis completed in {Duration}", mismatchTime);

        // Step 4: Report results
        _versionService.ReportResults(mismatches, allAssemblies);
        
        var totalTime = DateTime.Now - startTime;
        _logger.LogInformation("⏱️ Total analysis time: {Duration}", totalTime);
    }
}

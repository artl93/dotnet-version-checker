using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SharpCompress.Archives;
using SharpCompress.Common;
using System.IO.Compression;
using System.Reflection;

// Top-level program logic
if (args.Contains("--help") || args.Contains("-h"))
{
    ShowHelp();
    return;
}

var assetsPath = Path.Combine(Directory.GetCurrentDirectory(), "assets");
var tempPath = Path.Combine(Path.GetTempPath(), "dotnet-version-analysis", Guid.NewGuid().ToString());
var testMode = args.Contains("--test") || args.Contains("-t");
var maxArchives = testMode ? 3 : int.MaxValue;
var logger = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information)).CreateLogger("VersionAnalyzer");

try
{
    logger.LogInformation("Starting version analysis...");
    if (testMode)
        logger.LogInformation("🧪 Running in TEST MODE - processing only first {MaxArchives} archives", maxArchives);
    logger.LogInformation("Assets path: {AssetsPath}", assetsPath);
    logger.LogInformation("Temp extraction path: {TempPath}", tempPath);

    Directory.CreateDirectory(tempPath);
    var analyzer = new DotNetVersionAnalyzer(logger, tempPath);
    await analyzer.AnalyzeAsync(assetsPath, maxArchives);
}
catch (Exception ex)
{
    logger.LogError(ex, "Analysis failed");
}
finally
{
    var skipCleanup = args.Contains("--keep-temp") || args.Contains("-k");
    if (Directory.Exists(tempPath) && !skipCleanup)
    {
        try
        {
            Directory.Delete(tempPath, true);
            logger.LogInformation("Cleaned up temporary directory");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to cleanup temporary directory: {TempPath}", tempPath);
        }
    }
    else if (skipCleanup)
    {
        logger.LogInformation("⚠️ Temporary files kept at: {TempPath}", tempPath);
    }
}

static void ShowHelp()
{
    Console.WriteLine(".NET SDK Version Analyzer");
    Console.WriteLine("========================");
    Console.WriteLine();
    Console.WriteLine("Analyzes .NET SDK archives to find version mismatches between identical assemblies");
    Console.WriteLine("across different distribution packages.");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run                    Run full analysis (WARNING: Takes hours!)");
    Console.WriteLine("  dotnet run -- --test          Test mode (first 3 archives only)");
    Console.WriteLine("  dotnet run -- --keep-temp     Don't delete temporary files");
    Console.WriteLine("  dotnet run -- --help          Show this help");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --test, -t       Test mode - process only first 3 archives");
    Console.WriteLine("  --keep-temp, -k  Keep temporary extraction directories");
    Console.WriteLine("  --help, -h       Show this help message");
    Console.WriteLine();
    Console.WriteLine("Output:");
    Console.WriteLine("  - Console output with progress and results");
    Console.WriteLine("  - version-mismatch-report.txt (detailed text report)");
    Console.WriteLine("  - version-mismatch-report.csv (CSV format for analysis)");
    Console.WriteLine();
    Console.WriteLine("⚠️  WARNING: Full analysis can take several hours and use 10+ GB disk space!");
    Console.WriteLine("   Recommended: Run with --test first to verify everything works.");
}

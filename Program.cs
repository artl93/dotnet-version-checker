using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Check for help before setting up DI
if (args.Contains("--help") || args.Contains("-h"))
{
    ShowHelp();
    return;
}

// Configure and build host with dependency injection
var builder = Host.CreateApplicationBuilder(args);

// Configure services
builder.Services.Configure<AnalysisOptions>(options =>
{
    options.AssetsPath = Path.Combine(Directory.GetCurrentDirectory(), "assets");
    options.TempPath = Path.Combine(Path.GetTempPath(), "dotnet-version-analysis", Guid.NewGuid().ToString());
    options.TestMode = args.Contains("--test") || args.Contains("-t");
    options.MaxArchives = options.TestMode ? 3 : int.MaxValue;
    options.KeepTempFiles = args.Contains("--keep-temp") || args.Contains("-k");
});

// Register services as singletons
builder.Services.AddSingleton<ArchiveExtractionService>();
builder.Services.AddSingleton<AssemblyAnalysisService>();
builder.Services.AddSingleton<VersionAnalysisService>();
builder.Services.AddSingleton<ReportGenerator>();
builder.Services.AddSingleton<DotNetVersionAnalyzer>();

// Configure logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);

// Register the main application service
builder.Services.AddSingleton<VersionAnalyzerApp>();

var host = builder.Build();

// Run the application
try
{
    var app = host.Services.GetRequiredService<VersionAnalyzerApp>();
    await app.RunAsync();
}
catch (Exception ex)
{
    var logger = host.Services.GetRequiredService<ILogger<Program>>();
    logger.LogError(ex, "Application failed");
    Environment.ExitCode = 1;
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

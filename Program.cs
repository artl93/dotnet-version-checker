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

namespace VersionAnalyzer;

public class Program
{
    private static readonly ILogger<Program> _logger = LoggerFactory.Create(builder => 
        builder.AddConsole().SetMinimumLevel(LogLevel.Information)).CreateLogger<Program>();

    public static async Task Main(string[] args)
    {
        // Check for help
        if (args.Contains("--help") || args.Contains("-h"))
        {
            ShowHelp();
            return;
        }

        var assetsPath = Path.Combine(Directory.GetCurrentDirectory(), "assets");
        var tempPath = Path.Combine(Path.GetTempPath(), "dotnet-version-analysis", Guid.NewGuid().ToString());
        
        // Check for test mode (limit to first few archives)
        var testMode = args.Contains("--test") || args.Contains("-t");
        var maxArchives = testMode ? 3 : int.MaxValue;
        
        try
        {
            _logger.LogInformation("Starting version analysis...");
            if (testMode)
            {
                _logger.LogInformation("🧪 Running in TEST MODE - processing only first {MaxArchives} archives", maxArchives);
            }
            _logger.LogInformation("Assets path: {AssetsPath}", assetsPath);
            _logger.LogInformation("Temp extraction path: {TempPath}", tempPath);

            Directory.CreateDirectory(tempPath);

            var analyzer = new DotNetVersionAnalyzer(_logger, tempPath);
            await analyzer.AnalyzeAsync(assetsPath, maxArchives);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Analysis failed");
        }
        finally
        {
            // Cleanup temp directory (unless in test mode and user wants to inspect)
            var skipCleanup = args.Contains("--keep-temp") || args.Contains("-k");
            
            if (Directory.Exists(tempPath) && !skipCleanup)
            {
                try
                {
                    Directory.Delete(tempPath, true);
                    _logger.LogInformation("Cleaned up temporary directory");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to cleanup temporary directory: {TempPath}", tempPath);
                }
            }
            else if (skipCleanup)
            {
                _logger.LogInformation("⚠️ Temporary files kept at: {TempPath}", tempPath);
            }
        }
    }

    private static void ShowHelp()
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

public class DotNetVersionAnalyzer
{
    private readonly ILogger _logger;
    private readonly string _tempPath;
    private readonly ConcurrentBag<AssemblyInfo> _allAssemblies = new();
    private readonly SemaphoreSlim _extractionSemaphore = new(Environment.ProcessorCount);

    public DotNetVersionAnalyzer(ILogger logger, string tempPath)
    {
        _logger = logger;
        _tempPath = tempPath;
    }

    public async Task AnalyzeAsync(string assetsPath, int maxArchives = int.MaxValue)
    {
        var archiveFiles = GetArchiveFiles(assetsPath);
        if (maxArchives < archiveFiles.Count)
        {
            archiveFiles = archiveFiles.Take(maxArchives).ToList();
        }
        
        _logger.LogInformation("Found {Count} archive files to process", archiveFiles.Count);
        
        var startTime = DateTime.Now;

        // Step 1: Extract all archives in parallel
        await ExtractArchivesAsync(archiveFiles);
        var extractionTime = DateTime.Now - startTime;
        _logger.LogInformation("⏱️ Extraction completed in {Duration}", extractionTime);

        // Step 2: Analyze all extracted assemblies
        var analysisStart = DateTime.Now;
        await AnalyzeAssembliesAsync();
        var analysisTime = DateTime.Now - analysisStart;
        _logger.LogInformation("⏱️ Assembly analysis completed in {Duration}", analysisTime);

        // Step 3: Find version mismatches
        var mismatchStart = DateTime.Now;
        FindVersionMismatches();
        var mismatchTime = DateTime.Now - mismatchStart;
        _logger.LogInformation("⏱️ Version mismatch analysis completed in {Duration}", mismatchTime);
        
        var totalTime = DateTime.Now - startTime;
        _logger.LogInformation("⏱️ Total analysis time: {Duration}", totalTime);
    }

    private List<string> GetArchiveFiles(string assetsPath)
    {
        var supportedExtensions = new[] { ".zip", ".tar.gz", ".exe", ".rpm", ".deb", ".pkg", ".nupkg" };
        
        var files = Directory.GetFiles(assetsPath, "*", SearchOption.TopDirectoryOnly)
            .Where(f => supportedExtensions.Any(ext => 
                f.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ||
                (ext == ".tar.gz" && f.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))))
            .Where(f => !f.EndsWith(".sha512", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Log file sizes for estimation
        var totalSize = files.Sum(f => new FileInfo(f).Length);
        _logger.LogInformation("📊 Total archive size: {Size:F2} GB ({Count} files)", 
            totalSize / (1024.0 * 1024.0 * 1024.0), files.Count);
        
        return files;
    }

    private async Task ExtractArchivesAsync(List<string> archiveFiles)
    {
        _logger.LogInformation("📦 Extracting {Count} archives...", archiveFiles.Count);
        
        var completed = 0;
        var tasks = archiveFiles.Select(async archiveFile =>
        {
            await _extractionSemaphore.WaitAsync();
            try
            {
                await ExtractArchiveAsync(archiveFile);
                var current = Interlocked.Increment(ref completed);
                _logger.LogInformation("📦 Extracted {Current}/{Total} archives ({Percentage:F1}%): {FileName}", 
                    current, archiveFiles.Count, (double)current / archiveFiles.Count * 100, Path.GetFileName(archiveFile));
            }
            finally
            {
                _extractionSemaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        _logger.LogInformation("✅ Extraction completed");
    }

    private async Task ExtractArchiveAsync(string archiveFile)
    {
        var archiveName = Path.GetFileName(archiveFile);
        var extractPath = Path.Combine(_tempPath, SanitizeDirectoryName(archiveName));
        
        // Check if already extracted (idempotent extraction)
        if (Directory.Exists(extractPath) && Directory.EnumerateFileSystemEntries(extractPath).Any())
        {
            _logger.LogDebug("Skipping extraction of {Archive} - already extracted to {Path}", archiveName, extractPath);
            return;
        }
        
        try
        {
            Directory.CreateDirectory(extractPath);
            _logger.LogDebug("Extracting {Archive} to {Path}", archiveName, extractPath);

            if (archiveFile.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                archiveFile.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
            {
                await ExtractZipAsync(archiveFile, extractPath);
            }
            else if (archiveFile.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                await ExtractTarGzAsync(archiveFile, extractPath);
            }
            else if (archiveFile.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                await ExtractWindowsInstallerAsync(archiveFile, extractPath);
            }
            else if (archiveFile.EndsWith(".rpm", StringComparison.OrdinalIgnoreCase) ||
                     archiveFile.EndsWith(".deb", StringComparison.OrdinalIgnoreCase) ||
                     archiveFile.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase))
            {
                await ExtractPackageAsync(archiveFile, extractPath);
            }

            _logger.LogDebug("Successfully extracted {Archive}", archiveName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract {Archive}", archiveName);
        }
    }

    private Task ExtractZipAsync(string zipFile, string extractPath)
    {
        return Task.Run(() =>
        {
            using var archive = ZipFile.OpenRead(zipFile);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;

                var destinationPath = Path.Combine(extractPath, entry.FullName);
                var destinationDir = Path.GetDirectoryName(destinationPath);
                
                if (!string.IsNullOrEmpty(destinationDir))
                {
                    Directory.CreateDirectory(destinationDir);
                }

                if (!entry.FullName.EndsWith('/'))
                {
                    entry.ExtractToFile(destinationPath, true);
                    
                    // Check if this is an embedded zip or nupkg file that should be extracted
                    if (IsNestedArchive(destinationPath))
                    {
                        ExtractNestedArchive(destinationPath, extractPath);
                    }
                }
            }
        });
    }

    private async Task ExtractTarGzAsync(string tarGzFile, string extractPath)
    {
        await Task.Run(() =>
        {
            using var archive = ArchiveFactory.Open(tarGzFile);
            foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
            {
                if (string.IsNullOrEmpty(entry.Key)) continue;
                
                var destinationPath = Path.Combine(extractPath, entry.Key);
                var destinationDir = Path.GetDirectoryName(destinationPath);
                
                if (!string.IsNullOrEmpty(destinationDir))
                {
                    Directory.CreateDirectory(destinationDir);
                }

                using var entryStream = entry.OpenEntryStream();
                using var fileStream = File.Create(destinationPath);
                entryStream.CopyTo(fileStream);
                
                // Check if this is an embedded archive that should be extracted
                if (IsNestedArchive(destinationPath))
                {
                    ExtractNestedArchive(destinationPath, extractPath);
                }
            }
        });
    }

    private async Task ExtractWindowsInstallerAsync(string exeFile, string extractPath)
    {
        // Many .NET SDK .exe files are actually self-extracting zip files
        try
        {
            using var archive = ArchiveFactory.Open(exeFile);
            foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
            {
                if (string.IsNullOrEmpty(entry.Key)) continue;
                
                var destinationPath = Path.Combine(extractPath, entry.Key);
                var destinationDir = Path.GetDirectoryName(destinationPath);
                
                if (!string.IsNullOrEmpty(destinationDir))
                {
                    Directory.CreateDirectory(destinationDir);
                }

                using var entryStream = entry.OpenEntryStream();
                using var fileStream = File.Create(destinationPath);
                await entryStream.CopyToAsync(fileStream);
                
                // Check if this is an embedded archive that should be extracted
                if (IsNestedArchive(destinationPath))
                {
                    ExtractNestedArchive(destinationPath, extractPath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not extract {ExeFile} as archive, might be a real installer", Path.GetFileName(exeFile));
        }
    }

    private async Task ExtractPackageAsync(string packageFile, string extractPath)
    {
        try
        {
            using var archive = ArchiveFactory.Open(packageFile);
            foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
            {
                if (string.IsNullOrEmpty(entry.Key)) continue;
                
                var destinationPath = Path.Combine(extractPath, entry.Key);
                var destinationDir = Path.GetDirectoryName(destinationPath);
                
                if (!string.IsNullOrEmpty(destinationDir))
                {
                    Directory.CreateDirectory(destinationDir);
                }

                using var entryStream = entry.OpenEntryStream();
                using var fileStream = File.Create(destinationPath);
                await entryStream.CopyToAsync(fileStream);
                
                // Check if this is an embedded archive that should be extracted
                if (IsNestedArchive(destinationPath))
                {
                    ExtractNestedArchive(destinationPath, extractPath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not extract {PackageFile}", Path.GetFileName(packageFile));
        }
    }

    private async Task AnalyzeAssembliesAsync()
    {
        _logger.LogInformation("Analyzing extracted assemblies...");
        
        // First, collect all assembly files from all archives
        var allAssemblyFiles = new ConcurrentBag<(string filePath, string archiveName, string archiveDir)>();
        
        var collectionTasks = Directory.GetDirectories(_tempPath)
            .Select(async archiveDir =>
            {
                var archiveName = Path.GetFileName(archiveDir);
                await CollectAssemblyFilesAsync(archiveDir, archiveName, allAssemblyFiles);
            });

        await Task.WhenAll(collectionTasks);
        
        var totalFiles = allAssemblyFiles.Count;
        _logger.LogInformation("Found {Count} assembly files to analyze", totalFiles);
        
        // Now analyze all assemblies in parallel with controlled concurrency
        var assemblySemaphore = new SemaphoreSlim(Environment.ProcessorCount * 2);
        var progress = 0;
        
        var analysisTasks = allAssemblyFiles.Select(async fileInfo =>
        {
            await assemblySemaphore.WaitAsync();
            try
            {
                var assemblyInfo = await AnalyzeAssemblyAsync(fileInfo.filePath, fileInfo.archiveName, fileInfo.archiveDir);
                if (assemblyInfo != null)
                {
                    _allAssemblies.Add(assemblyInfo);
                }
                
                var currentProgress = Interlocked.Increment(ref progress);
                if (currentProgress % 100 == 0 || currentProgress == totalFiles)
                {
                    _logger.LogInformation("Analyzed {Current}/{Total} assemblies ({Percentage:F1}%)", 
                        currentProgress, totalFiles, (double)currentProgress / totalFiles * 100);
                }
            }
            finally
            {
                assemblySemaphore.Release();
            }
        });

        await Task.WhenAll(analysisTasks);
        
        _logger.LogInformation("Found {Count} valid assemblies total", _allAssemblies.Count);
    }

    private async Task CollectAssemblyFilesAsync(string archiveDir, string archiveName, 
        ConcurrentBag<(string filePath, string archiveName, string archiveDir)> allAssemblyFiles)
    {
        await Task.Run(() =>
        {
            var assemblyFiles = Directory.GetFiles(archiveDir, "*.dll", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(archiveDir, "*.exe", SearchOption.AllDirectories))
                .Where(f => IsManagedAssembly(f));

            foreach (var file in assemblyFiles)
            {
                allAssemblyFiles.Add((file, archiveName, archiveDir));
            }
            
            _logger.LogDebug("Collected {Count} assembly files from {Archive}", 
                assemblyFiles.Count(), archiveName);
        });
    }

    private bool IsManagedAssembly(string filePath)
    {
        try
        {
            using var fileStream = File.OpenRead(filePath);
            using var peReader = new PEReader(fileStream);
            
            return peReader.HasMetadata && peReader.GetMetadataReader().IsAssembly;
        }
        catch
        {
            return false;
        }
    }

    private Task<AssemblyInfo?> AnalyzeAssemblyAsync(string assemblyPath, string sourceArchive, string archiveDir)
    {
        return Task.Run(() =>
        {
            try
            {
                var fileName = Path.GetFileName(assemblyPath);
                var relativePath = Path.GetRelativePath(archiveDir, assemblyPath);

                // Get version info using PEReader
                using var fileStream = File.OpenRead(assemblyPath);
                var assemblyName = GetAssemblyName(fileStream, fileName);
                
                if (assemblyName == null)
                {
                    return null;
                }
                
                var assemblyVersion = assemblyName.Version?.ToString();
                
                // Try to get file version and product version from Win32 resources or attributes
                var fileVersion = GetFileVersion(assemblyPath);
                var productVersion = GetProductVersion(fileStream);
                var informationalVersion = GetInformationalVersion(fileStream);

                return new AssemblyInfo(
                    fileName,
                    assemblyPath,
                    assemblyVersion,
                    fileVersion,
                    productVersion,
                    informationalVersion,
                    sourceArchive,
                    relativePath);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to analyze assembly {Assembly}", assemblyPath);
                return null;
            }
        });
    }

    private static AssemblyName? GetAssemblyName(Stream stream, string fileName)
    {
        try
        {
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
            {
                return null;
            }

            var metadataReader = peReader.GetMetadataReader();
            var assemblyDefinition = metadataReader.GetAssemblyDefinition();
            var assemblyName = assemblyDefinition.GetAssemblyName();

            return assemblyName;
        }
        catch
        {
            return null;
        }
    }

    private string? GetFileVersion(string assemblyPath)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var versionInfo = FileVersionInfo.GetVersionInfo(assemblyPath);
                return versionInfo.FileVersion;
            }
            
            // On non-Windows, try to read from assembly attributes using PEReader
            using var fileStream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(fileStream);
            
            if (!peReader.HasMetadata)
            {
                return null;
            }
            
            var metadataReader = peReader.GetMetadataReader();
            var assemblyDefinition = metadataReader.GetAssemblyDefinition();
            
            foreach (var attributeHandle in assemblyDefinition.GetCustomAttributes())
            {
                var attribute = metadataReader.GetCustomAttribute(attributeHandle);
                if (IsAttributeType(metadataReader, attribute, "AssemblyFileVersionAttribute"))
                {
                    return GetAttributeStringValue(metadataReader, attribute);
                }
            }
        }
        catch
        {
            // Ignore errors
        }
        return null;
    }

    private string? GetProductVersion(Stream stream)
    {
        try
        {
            stream.Position = 0;
            using var peReader = new PEReader(stream);
            
            if (!peReader.HasMetadata)
            {
                return null;
            }
            
            var metadataReader = peReader.GetMetadataReader();
            var assemblyDefinition = metadataReader.GetAssemblyDefinition();
            
            foreach (var attributeHandle in assemblyDefinition.GetCustomAttributes())
            {
                var attribute = metadataReader.GetCustomAttribute(attributeHandle);
                if (IsAttributeType(metadataReader, attribute, "AssemblyProductAttribute"))
                {
                    return GetAttributeStringValue(metadataReader, attribute);
                }
            }
        }
        catch
        {
            // Ignore errors
        }
        return null;
    }

    private string? GetInformationalVersion(Stream stream)
    {
        try
        {
            stream.Position = 0;
            using var peReader = new PEReader(stream);
            
            if (!peReader.HasMetadata)
            {
                return null;
            }
            
            var metadataReader = peReader.GetMetadataReader();
            var assemblyDefinition = metadataReader.GetAssemblyDefinition();
            
            foreach (var attributeHandle in assemblyDefinition.GetCustomAttributes())
            {
                var attribute = metadataReader.GetCustomAttribute(attributeHandle);
                if (IsAttributeType(metadataReader, attribute, "AssemblyInformationalVersionAttribute"))
                {
                    return GetAttributeStringValue(metadataReader, attribute);
                }
            }
        }
        catch
        {
            // Ignore errors
        }
        return null;
    }

    private bool IsAttributeType(MetadataReader metadataReader, CustomAttribute attribute, string attributeName)
    {
        try
        {
            if (attribute.Constructor.Kind == HandleKind.MethodDefinition)
            {
                var method = metadataReader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);
                var type = metadataReader.GetTypeDefinition(method.GetDeclaringType());
                var typeName = metadataReader.GetString(type.Name);
                return typeName == attributeName;
            }
            else if (attribute.Constructor.Kind == HandleKind.MemberReference)
            {
                var memberRef = metadataReader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                if (memberRef.Parent.Kind == HandleKind.TypeReference)
                {
                    var typeRef = metadataReader.GetTypeReference((TypeReferenceHandle)memberRef.Parent);
                    var typeName = metadataReader.GetString(typeRef.Name);
                    return typeName == attributeName;
                }
            }
        }
        catch
        {
            // Ignore errors
        }
        return false;
    }

    private string? GetAttributeStringValue(MetadataReader metadataReader, CustomAttribute attribute)
    {
        try
        {
            var value = attribute.DecodeValue(new SimpleCustomAttributeTypeProvider());
            if (value.FixedArguments.Length > 0 && value.FixedArguments[0].Value is string stringValue)
            {
                return stringValue;
            }
        }
        catch
        {
            // Ignore errors
        }
        return null;
    }

    // Simple custom attribute type provider for basic string attributes
    private class SimpleCustomAttributeTypeProvider : ICustomAttributeTypeProvider<object>
    {
        public object GetPrimitiveType(PrimitiveTypeCode typeCode)
        {
            return typeCode switch
            {
                PrimitiveTypeCode.String => typeof(string),
                PrimitiveTypeCode.Boolean => typeof(bool),
                PrimitiveTypeCode.Int32 => typeof(int),
                PrimitiveTypeCode.Int64 => typeof(long),
                _ => typeof(object)
            };
        }

        public object GetSystemType() => typeof(Type);
        public object GetSZArrayType(object elementType) => Array.CreateInstance((Type)elementType, 0).GetType();
        public object GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => typeof(object);
        public object GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => typeof(object);
        public object GetTypeFromSerializedName(string name) => typeof(object);
        public PrimitiveTypeCode GetUnderlyingEnumType(object type) => PrimitiveTypeCode.Int32;
        public bool IsSystemType(object type) => false;
    }

    // ...existing code...

    private void FindVersionMismatches()
    {
        _logger.LogInformation("Analyzing version mismatches...");
        
        var assemblyGroups = _allAssemblies
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

        // Report results
        ReportResults(assembliesWithMismatches);
    }

    private void ReportResults(List<AssemblyVersionMismatch> mismatches)
    {
        _logger.LogInformation("=== VERSION MISMATCH ANALYSIS RESULTS ===");
        _logger.LogInformation("Total assemblies analyzed: {Count}", _allAssemblies.Count);
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
            if (mismatch.AssemblyVersionGroups != null)
            {
                _logger.LogWarning("  Assembly Versions:");
                foreach (var versionGroup in mismatch.AssemblyVersionGroups.OrderBy(g => g.Key))
                {
                    _logger.LogWarning("    {Version}: {Count} occurrence(s)", 
                        versionGroup.Key ?? "null", versionGroup.Count());
                    foreach (var assembly in versionGroup.Take(3)) // Show first 3 archives for brevity
                    {
                        _logger.LogWarning("      � {Archive} ({Path})", 
                            assembly.SourceArchive, assembly.RelativePath);
                    }
                    if (versionGroup.Count() > 3)
                    {
                        _logger.LogWarning("      ... and {More} more", versionGroup.Count() - 3);
                    }
                }
            }

            if (mismatch.FileVersionGroups != null)
            {
                _logger.LogWarning("  File Versions:");
                foreach (var versionGroup in mismatch.FileVersionGroups.OrderBy(g => g.Key))
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

            if (mismatch.InformationalVersionGroups != null)
            {
                _logger.LogWarning("  Informational Versions:");
                foreach (var versionGroup in mismatch.InformationalVersionGroups.OrderBy(g => g.Key))
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
            
            _logger.LogInformation("");
        }

        // Generate summary reports
        var reportPath = Path.Combine(Directory.GetCurrentDirectory(), "version-mismatch-report.txt");
        var csvReportPath = Path.Combine(Directory.GetCurrentDirectory(), "version-mismatch-report.csv");
        GenerateDetailedReport(mismatches, reportPath);
        GenerateCsvReport(mismatches, csvReportPath);
        _logger.LogInformation("📄 Detailed report saved to: {ReportPath}", reportPath);
        _logger.LogInformation("📊 CSV report saved to: {CsvReportPath}", csvReportPath);
    }

    private void GenerateDetailedReport(List<AssemblyVersionMismatch> mismatches, string reportPath)
    {
        using var writer = new StreamWriter(reportPath);
        
        writer.WriteLine("=== .NET SDK VERSION MISMATCH ANALYSIS REPORT ===");
        writer.WriteLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        writer.WriteLine($"Total assemblies analyzed: {_allAssemblies.Count}");
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

            // Show version breakdowns
            if (mismatch.AssemblyVersionGroups != null)
            {
                writer.WriteLine("   Assembly Versions:");
                foreach (var versionGroup in mismatch.AssemblyVersionGroups.OrderBy(g => g.Key))
                {
                    writer.WriteLine($"     {versionGroup.Key ?? "null"}: {versionGroup.Count()} occurrence(s)");
                    foreach (var assembly in versionGroup.OrderBy(a => a.SourceArchive))
                    {
                        writer.WriteLine($"       � {assembly.SourceArchive}");
                        writer.WriteLine($"          Path: {assembly.RelativePath}");
                    }
                }
                writer.WriteLine();
            }

            if (mismatch.FileVersionGroups != null)
            {
                writer.WriteLine("   File Versions:");
                foreach (var versionGroup in mismatch.FileVersionGroups.OrderBy(g => g.Key))
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

            if (mismatch.InformationalVersionGroups != null)
            {
                writer.WriteLine("   Informational Versions:");
                foreach (var versionGroup in mismatch.InformationalVersionGroups.OrderBy(g => g.Key))
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
            
            writer.WriteLine(new string('-', 80));
            writer.WriteLine();
        }

        // Add summary statistics
        writer.WriteLine("=== SUMMARY STATISTICS ===");
        writer.WriteLine($"Archives processed: {_allAssemblies.Select(a => a.SourceArchive).Distinct().Count()}");
        writer.WriteLine($"Unique assembly names: {_allAssemblies.Select(a => a.FileName).Distinct().Count()}");
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

    private void GenerateCsvReport(List<AssemblyVersionMismatch> mismatches, string csvReportPath)
    {
        using var writer = new StreamWriter(csvReportPath);
        
        // Write CSV header
        writer.WriteLine("FileName,MismatchTypes,SourceArchive,RelativePath,AssemblyVersion,FileVersion,ProductVersion,InformationalVersion,VersionType,Version,OccurrenceCount");
        
        foreach (var mismatch in mismatches.OrderBy(m => m.FileName))
        {
            var mismatchTypesStr = string.Join("; ", mismatch.MismatchTypes);

            // Write entries for assembly version mismatches
            if (mismatch.AssemblyVersionGroups != null)
            {
                foreach (var versionGroup in mismatch.AssemblyVersionGroups)
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
                                       $"AssemblyVersion," +
                                       $"{EscapeCsvField(versionGroup.Key ?? "")}," +
                                       $"{versionGroup.Count()}");
                    }
                }
            }

            // Write entries for file version mismatches
            if (mismatch.FileVersionGroups != null)
            {
                foreach (var versionGroup in mismatch.FileVersionGroups)
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
                                       $"FileVersion," +
                                       $"{EscapeCsvField(versionGroup.Key ?? "")}," +
                                       $"{versionGroup.Count()}");
                    }
                }
            }

            // Write entries for informational version mismatches
            if (mismatch.InformationalVersionGroups != null)
            {
                foreach (var versionGroup in mismatch.InformationalVersionGroups)
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
                                       $"InformationalVersion," +
                                       $"{EscapeCsvField(versionGroup.Key ?? "")}," +
                                       $"{versionGroup.Count()}");
                    }
                }
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

    private static string SanitizeDirectoryName(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return string.Join("_", name.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));
    }

    private bool IsNestedArchive(string filePath)
    {
        var fileName = Path.GetFileName(filePath).ToLowerInvariant();
        return fileName.EndsWith(".zip") || 
               fileName.EndsWith(".nupkg") ||
               fileName.EndsWith(".jar") ||
               fileName.EndsWith(".war") ||
               fileName.EndsWith(".ear");
    }

    private void ExtractNestedArchive(string nestedArchivePath, string baseExtractPath)
    {
        try
        {
            var nestedFileName = Path.GetFileNameWithoutExtension(nestedArchivePath);
            var nestedExtractPath = Path.Combine(baseExtractPath, "nested", nestedFileName);
            
            Directory.CreateDirectory(nestedExtractPath);
            
            _logger.LogDebug("📦 Extracting nested archive: {Archive}", Path.GetFileName(nestedArchivePath));
            
            // Extract the nested archive
            using var nestedArchive = ZipFile.OpenRead(nestedArchivePath);
            foreach (var entry in nestedArchive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;

                var destinationPath = Path.Combine(nestedExtractPath, entry.FullName);
                var destinationDir = Path.GetDirectoryName(destinationPath);
                
                if (!string.IsNullOrEmpty(destinationDir))
                {
                    Directory.CreateDirectory(destinationDir);
                }

                if (!entry.FullName.EndsWith('/'))
                {
                    entry.ExtractToFile(destinationPath, true);
                    
                    // Recursively extract if this is also a nested archive (limit depth to prevent infinite loops)
                    var depth = nestedExtractPath.Split(Path.DirectorySeparatorChar).Count(p => p == "nested");
                    if (depth < 3 && IsNestedArchive(destinationPath))
                    {
                        ExtractNestedArchive(destinationPath, nestedExtractPath);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to extract nested archive: {Archive}", Path.GetFileName(nestedArchivePath));
        }
    }
}
}

using System.Collections.Concurrent;
using System.IO.Compression;
using Microsoft.Extensions.Logging;
using SharpCompress.Archives;
using SharpCompress.Common;

public class ArchiveExtractionService
{
    private readonly ILogger<ArchiveExtractionService> _logger;
    private readonly SemaphoreSlim _extractionSemaphore = new(Environment.ProcessorCount);

    public ArchiveExtractionService(ILogger<ArchiveExtractionService> logger)
    {
        _logger = logger;
    }

    public List<string> GetArchiveFiles(string assetsPath)
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

    public async Task ExtractArchivesAsync(List<string> archiveFiles, string tempPath)
    {
        _logger.LogInformation("📦 Extracting {Count} archives...", archiveFiles.Count);
        
        var completed = 0;
        var tasks = archiveFiles.Select(async archiveFile =>
        {
            await _extractionSemaphore.WaitAsync();
            try
            {
                await ExtractArchiveAsync(archiveFile, tempPath);
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

    private async Task ExtractArchiveAsync(string archiveFile, string tempPath)
    {
        var archiveName = Path.GetFileName(archiveFile);
        var extractPath = Path.Combine(tempPath, SanitizeDirectoryName(archiveName));
        
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

    private static string SanitizeDirectoryName(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return string.Join("_", name.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));
    }
}

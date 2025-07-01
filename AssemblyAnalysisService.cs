using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

public class AssemblyAnalysisService
{
    private readonly ILogger _logger;

    public AssemblyAnalysisService(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<List<AssemblyInfo>> AnalyzeAssembliesAsync(string tempPath)
    {
        _logger.LogInformation("Analyzing extracted assemblies...");
        
        var allAssemblies = new ConcurrentBag<AssemblyInfo>();
        
        // First, collect all assembly files from all archives
        var allAssemblyFiles = new ConcurrentBag<(string filePath, string archiveName, string archiveDir)>();
        
        var collectionTasks = Directory.GetDirectories(tempPath)
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
                    allAssemblies.Add(assemblyInfo);
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
        
        var result = allAssemblies.ToList();
        _logger.LogInformation("Found {Count} valid assemblies total", result.Count);
        
        return result;
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
}

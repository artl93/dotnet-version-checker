# .NET SDK Version Analyzer

This tool analyzes .NET SDK archives to find version mismatches between identical assemblies across different distribution packages.

## Purpose

The .NET SDK is distributed in multiple formats (zip, tar.gz, exe, rpm, deb, pkg) for different platforms. This tool ensures that the same assembly has consistent version information across all distribution packages.

## What it does

1. **Extracts Archives**: Automatically extracts all supported archive formats in parallel to temporary directories
2. **Analyzes Assemblies**: Scans all .NET assemblies (.dll, .exe files) and extracts version information:
   - Assembly Version
   - File Version  
   - Product Version
   - Informational Version
3. **Finds Mismatches**: Identifies cases where the same assembly filename has different version information across archives
4. **Generates Reports**: Creates both console output and detailed text reports

## Supported Archive Formats

- ZIP files (.zip)
- Compressed tar files (.tar.gz)
- Windows installers (.exe) - when they contain extractable content
- RPM packages (.rpm)
- Debian packages (.deb)
- macOS packages (.pkg)

## Usage

```bash
# Build the project
dotnet build

# Run the analyzer (full analysis - WARNING: This will take a long time!)
dotnet run

# Run in test mode (processes only first 3 archives for quick testing)
dotnet run -- --test

# Run in test mode and keep temporary files for inspection
dotnet run -- --test --keep-temp

# Show help
dotnet run -- --help
```

The tool expects to find archive files in the `assets/` directory relative to the executable.

### Command Line Options

- `--test` or `-t`: Test mode - processes only the first 3 archives
- `--keep-temp` or `-k`: Don't delete temporary extraction directories
- `--help` or `-h`: Show help information

### Performance Notes

⚠️ **WARNING**: A full analysis of all .NET SDK archives can take several hours and use significant disk space (potentially 10+ GB for temporary extraction). 

**Recommended workflow:**
1. First run with `--test` to verify everything works
2. Then run the full analysis: `dotnet run`

## Output

The tool provides:

1. **Console Output**: Real-time progress and summary of findings
2. **Detailed Report**: A comprehensive text file (`version-mismatch-report.txt`) containing:
   - All version mismatches found
   - Detailed version information for each assembly
   - Summary statistics
   - Top assemblies by archive count

## Example Output

```
=== VERSION MISMATCH ANALYSIS RESULTS ===
Total assemblies analyzed: 1,234
Version mismatches found: 5

🔴 System.Text.Json.dll has Assembly Version Mismatch across 3 archives:
  📦 dotnet-sdk-10.0.100-preview.6.25326.107-win-x64.zip
     Path: shared/Microsoft.NETCore.App/10.0.0-preview.6.25326.107/System.Text.Json.dll
     Assembly Version: 10.0.0.0
     File Version: 10.0.25326.107

  📦 dotnet-sdk-10.0.100-preview.6.25326.107-linux-x64.tar.gz
     Path: shared/Microsoft.NETCore.App/10.0.0-preview.6.25326.107/System.Text.Json.dll
     Assembly Version: 10.0.1.0
     File Version: 10.0.25326.107
```

## Requirements

- .NET 8.0 or later
- Sufficient disk space for temporary extraction (archives will be extracted to temp directories)
- Memory proportional to the number of assemblies being analyzed

## Dependencies

- **SharpCompress**: For handling various archive formats
- **System.Reflection.Metadata**: For PE file analysis and .NET assembly metadata reading
- **Microsoft.Extensions.Logging**: For structured logging

## Notes

- The tool automatically cleans up temporary extraction directories
- Extraction is performed in parallel for better performance
- Non-managed files are automatically filtered out
- Failed extractions are logged but don't stop the analysis

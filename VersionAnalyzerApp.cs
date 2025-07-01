using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public class VersionAnalyzerApp
{
    private readonly ILogger<VersionAnalyzerApp> _logger;
    private readonly AnalysisOptions _options;
    private readonly DotNetVersionAnalyzer _analyzer;

    public VersionAnalyzerApp(
        ILogger<VersionAnalyzerApp> logger,
        IOptions<AnalysisOptions> options,
        DotNetVersionAnalyzer analyzer)
    {
        _logger = logger;
        _options = options.Value;
        _analyzer = analyzer;
    }

    public async Task RunAsync()
    {
        try
        {
            _logger.LogInformation("Starting version analysis...");
            if (_options.TestMode)
            {
                _logger.LogInformation("🧪 Running in TEST MODE - processing only first {MaxArchives} archives", 
                    _options.MaxArchives);
            }
            _logger.LogInformation("Assets path: {AssetsPath}", _options.AssetsPath);
            _logger.LogInformation("Temp extraction path: {TempPath}", _options.TempPath);

            Directory.CreateDirectory(_options.TempPath);
            await _analyzer.AnalyzeAsync(_options.AssetsPath, _options.MaxArchives);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Analysis failed");
            throw;
        }
        finally
        {
            await CleanupAsync();
        }
    }

    private async Task CleanupAsync()
    {
        if (!Directory.Exists(_options.TempPath))
            return;

        if (!_options.KeepTempFiles)
        {
            try
            {
                await Task.Run(() => Directory.Delete(_options.TempPath, true));
                _logger.LogInformation("Cleaned up temporary directory");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup temporary directory: {TempPath}", _options.TempPath);
            }
        }
        else
        {
            _logger.LogInformation("⚠️ Temporary files kept at: {TempPath}", _options.TempPath);
        }
    }
}

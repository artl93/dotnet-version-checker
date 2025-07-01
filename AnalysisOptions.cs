public class AnalysisOptions
{
    public string AssetsPath { get; set; } = string.Empty;
    public string TempPath { get; set; } = string.Empty;
    public bool TestMode { get; set; }
    public int MaxArchives { get; set; }
    public bool KeepTempFiles { get; set; }
}

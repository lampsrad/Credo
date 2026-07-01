using System.Diagnostics;

namespace Credo;

public class AppConfig
{
    public string? BackupPath { get; }
    public string? BrowserPath { get; }
    private Process? browserProcess;
    public string ConnectionKey { get; set; } = "XPS";
    public string SqlInstance { get; set; } = "SQL";
    public string DbName { get; set; } = "Credo";
    public string? DownloadsPath { get; }
    private static string? ExpandPath(string? value) =>
    string.IsNullOrWhiteSpace(value) ? null : Environment.ExpandEnvironmentVariables(value);
    public string MachineName { get; }
    public decimal? MarketValue { get; set; }

    public AppConfig(IConfiguration configuration)
    {
        MachineName = Environment.MachineName;
        SqlInstance = MachineName == "ROG" ? "SQL16" : "SQL";
        BackupPath = ExpandPath(configuration["Paths:Backup"]);
        DownloadsPath = ExpandPath(configuration["Paths:Downloads"]);
        BrowserPath =  MachineName=="ROG"? ExpandPath(configuration["Paths:BrowserROG"]) : ExpandPath(configuration["Paths:BrowserXPS"]);
    }
    public void StartBrowser(string url)
    {
        if (string.IsNullOrWhiteSpace(BrowserPath) || !File.Exists(BrowserPath))
            return;
        var p = new Process();
        p.StartInfo.FileName = BrowserPath;
        p.StartInfo.Arguments = url;
        p.Start();
        browserProcess = p;
    }
    public void StopBrowser()
    {
        try
        {
            browserProcess?.CloseMainWindow();
            browserProcess?.Close();
        }
        catch
        {
        }
        browserProcess = null;
    }
}

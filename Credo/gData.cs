using System.Diagnostics;

namespace Credo;

public static class gData
{
    public static string BackupPath { get; set; } = "C:\\Users\\lamps\\OneDrive\\Database\\SQL16\\Backup\\Credo\\";
    public static string connectionKey { get; set; } = "Local";
    public static string DownloadsPath { get; set; } = "C:\\Users\\lamps\\Downloads\\Credo\\";
    public static string dbName { get; set; } = "Credo";
    public static bool FirstTime { get; set; } = true;

    public static Dictionary<string, string> Currencies { get; set; } = new Dictionary<string, string>
    {
          {"EUR","EURUSD=X"},
        {"GBP","GBPUSD=X"},
         {"HKD","HKDUSD=X"},
          {"CAD","CADUSD=X"},
           {"DKK","DKKUSD=X"},
         {"CHF","CHFUSD=X"},
        {"ZAR","ZAR" }
    };
    public static string? machineName { get; set; }
    public static Process? process { get; set; }
    public static void StartBrowser(string url)
    {
        Process g = new Process();
        process = g;
        g.StartInfo.FileName = machineName == "XPS" ? @"C:\Program Files\Google\Chrome\Application\chrome.exe" : @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe";
        g.StartInfo.Arguments = url;
        g.Start();
    }
}

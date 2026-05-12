using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace Credo.Pages;

public partial class Backup
{
    [Inject] Repo repo { get; set; } = default!;
    [Inject] NavigationManager nav { get; set; } = default!;
    [Inject] ILogger<Backup> logger { get; set; } = default!;
    [Inject] AppConfig cfg { get; set; } = default!;
    [Parameter] public string? Data { get; set; }
    IList<string> Files = new List<string>();
    string Model { get; set; } = string.Empty;
    string? InputText { get; set; }
    string? Title { get; set; }
    string? ErrorMessage { get; set; }

    protected override async Task OnInitializedAsync()
    {
        await Task.Run(() =>
        {
            var files = Directory.GetFiles($"{cfg.BackupPath}", "*.bak");
            foreach (string file in files)
            {
                var m1 = Regex.Match(file, @"\w+\.\w+$");
                Files.Add(m1.Value);
            }
        });
    }
    protected override void OnParametersSet()
    {
        Title = Data switch
        {
            "Backup" => "Backup",
            "Restore" => "Restore",
            _ => null
        };
        Title = Data;
    }
    private async Task SubmitAsync()
    {
        ErrorMessage = null;
        if (string.IsNullOrEmpty(cfg.BackupPath))
        {
            ErrorMessage = "BackupPath is not configured.";
            return;
        }
        string file = string.Empty;
        if (Title == "Backup")
        {
            if (string.IsNullOrEmpty(InputText))
                file = Model;
            else
                file = $"{InputText}.bak";
            string fn = Path.Combine(cfg.BackupPath, file);
            if (File.Exists(fn))
                File.Delete(fn);
            try
            {
                await repo.SqlBackupAsync(fn);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "SQL backup failed for {FileName}", fn);
                ErrorMessage = $"Backup failed: {ex.Message}";
                return;
            }
        }
        if (Title == "Restore")
        {
            try
            {
                await repo.SqlRestoreAsync(Path.Combine(cfg.BackupPath, Model));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "SQL restore failed for {FileName}", Model);
                ErrorMessage = $"Restore failed: {ex.Message}";
                return;
            }
        }
        nav.NavigateTo("/");
    }
}

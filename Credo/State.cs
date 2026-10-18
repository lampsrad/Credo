namespace Credo;

public class State
{
    public event Action<bool>? Progress;
    public event Func<Task<string>>? FileNameGet;
    public event Func<string, string, string, Task<byte[]?>>? FileUpload;
    public event Func<string, string, string, Task<string>>? MessageShow;

    public double ProgressVal { get; set; }
    public string? TitleD { get; set; }

    public void Hide()
    {
        ProgressVal = 0;
        Progress?.Invoke(false);
    }
    public async Task<string?> ShowFilePicker()
    {
        if (FileNameGet is null) return null;
        var file = await FileNameGet.Invoke();
        return file;
    }
    public async Task<byte[]?> ShowFileUpload(string title, string message, string accept)
    {
        if (FileUpload is null) return null;
        return await FileUpload.Invoke(title, message, accept);
    }
    public async Task<string?> ShowMessage(string title, string message, string button)
    {
        if (MessageShow is null) return null;
        var mes = await MessageShow.Invoke(title, message, button);
        return mes;
    }
    public void ShowProgress(string title = "Progress", bool showProgress = true, double initialProgress = 0)
    {
        TitleD = title;
        ProgressVal = initialProgress;
        Progress?.Invoke(true);
    }
    public void UpdateProgress(double progress, string? title = null)
    {
        if (Progress is not null)
        {
            TitleD = title ?? TitleD;
            ProgressVal = Math.Max(0, Math.Min(100, progress)); // Clamp to 0-100
            Progress?.Invoke(true);
        }
    }

}

using System.Threading.Tasks;

namespace AMCCA.App.Services;

public interface IDialogService
{
    Task ShowAlertAsync(string title, string message);
    Task<bool> ShowConfirmAsync(string title, string message);
    Task<string?> ShowPromptAsync(string title, string message, string defaultValue = "");
}

public class DialogService : IDialogService
{
    public Task ShowAlertAsync(string title, string message)
    {
        System.Windows.MessageBox.Show(message, title, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        return Task.CompletedTask;
    }

    public Task<bool> ShowConfirmAsync(string title, string message)
    {
        var res = System.Windows.MessageBox.Show(message, title, System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        return Task.FromResult(res == System.Windows.MessageBoxResult.Yes);
    }

    public Task<string?> ShowPromptAsync(string title, string message, string defaultValue = "")
    {
        // Simple input prompt fallback
        return Task.FromResult<string?>(defaultValue);
    }
}

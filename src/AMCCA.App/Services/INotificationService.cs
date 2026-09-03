using System;
using System.Collections.ObjectModel;

namespace AMCCA.App.Services;

public record NotificationItem(string Message, string Type, DateTime Timestamp);

public interface INotificationService
{
    ObservableCollection<NotificationItem> Notifications { get; }
    void AddNotification(string message, string type = "Info");
    void Clear();
}

public class NotificationService : INotificationService
{
    public ObservableCollection<NotificationItem> Notifications { get; } = new();

    public void AddNotification(string message, string type = "Info")
    {
        Notifications.Insert(0, new NotificationItem(message, type, DateTime.Now));
        if (Notifications.Count > 50)
        {
            Notifications.RemoveAt(Notifications.Count - 1);
        }
    }

    public void Clear()
    {
        Notifications.Clear();
    }
}

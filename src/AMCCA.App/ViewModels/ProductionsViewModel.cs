using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using AMCCA.App.Common;
using AMCCA.App.Services;
using AMCCA.Core.Domain;

namespace AMCCA.App.ViewModels;

public record ProductionItem(
    string Id,
    string Title,
    string NicheId,
    string State,
    string CreatedAt,
    string UpdatedAt)
{
    public string Topic => Title;
}

public class ProductionsViewModel : ViewModelBase
{
    private readonly ProductionService _productionService;
    private readonly IDialogService _dialogService;
    private readonly INotificationService _notificationService;

    private ProductionItem? _selectedProduction;
    private string _newTopic = string.Empty;
    private string _newNiche = "tech";

    public ObservableCollection<ProductionItem> Productions { get; } = new();

    public ProductionItem? SelectedProduction
    {
        get => _selectedProduction;
        set => SetProperty(ref _selectedProduction, value);
    }

    public string NewTopic
    {
        get => _newTopic;
        set => SetProperty(ref _newTopic, value);
    }

    public string NewNiche
    {
        get => _newNiche;
        set => SetProperty(ref _newNiche, value);
    }

    public ICommand RefreshCommand { get; }
    public ICommand CreateProductionCommand { get; }
    public ICommand CancelProductionCommand { get; }

    public ProductionsViewModel(
        ProductionService productionService,
        IDialogService dialogService,
        INotificationService notificationService)
    {
        _productionService = productionService;
        _dialogService = dialogService;
        _notificationService = notificationService;

        RefreshCommand = new AsyncRelayCommand(LoadProductionsAsync);
        CreateProductionCommand = new AsyncRelayCommand(CreateProductionAsync, () => !string.IsNullOrWhiteSpace(NewTopic));
        CancelProductionCommand = new AsyncRelayCommand(CancelProductionAsync, () => SelectedProduction != null);

        _ = LoadProductionsAsync();
    }

    public async Task LoadProductionsAsync()
    {
        Productions.Clear();
        try
        {
            var rows = await _productionService.ListRecentAsync(50);
            foreach (var p in rows)
            {
                Productions.Add(new ProductionItem(p.Id, p.Title ?? string.Empty, p.NicheId ?? string.Empty, p.State, p.CreatedAt, p.UpdatedAt));
            }
        }
        catch (Exception ex)
        {
            _notificationService.AddNotification($"Failed to load productions: {ex.Message}", "Error");
        }
    }

    public async Task CreateProductionAsync()
    {
        if (string.IsNullOrWhiteSpace(NewTopic)) return;

        try
        {
            var correlationId = Guid.NewGuid().ToString("N");
            var prod = await _productionService.CreateProductionAsync(
                title: NewTopic,
                language: "en",
                autonomyMode: "COLLABORATIVE",
                correlationId: correlationId,
                nicheId: NewNiche);

            _notificationService.AddNotification($"Created production {prod.Id} ({NewTopic})", "Success");
            NewTopic = string.Empty;
            await LoadProductionsAsync();
        }
        catch (Exception ex)
        {
            _notificationService.AddNotification($"Error creating production: {ex.Message}", "Error");
        }
    }

    public async Task CancelProductionAsync()
    {
        if (SelectedProduction == null) return;

        var confirmed = await _dialogService.ShowConfirmAsync("Confirm Cancellation", $"Cancel production {SelectedProduction.Id}?");
        if (!confirmed) return;

        try
        {
            var correlationId = Guid.NewGuid().ToString("N");
            await _productionService.TransitionAsync(
                productionId: SelectedProduction.Id,
                toState: "CANCELLED",
                actorType: "OPERATOR",
                correlationId: correlationId);

            _notificationService.AddNotification($"Cancelled production {SelectedProduction.Id}", "Info");
            await LoadProductionsAsync();
        }
        catch (Exception ex)
        {
            _notificationService.AddNotification($"Error cancelling production: {ex.Message}", "Error");
        }
    }
}

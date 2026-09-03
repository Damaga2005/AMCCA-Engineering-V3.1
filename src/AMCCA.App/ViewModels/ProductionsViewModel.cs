using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using AMCCA.App.Common;
using AMCCA.App.Services;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.StateMachine;
using Dapper;

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
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly StateMachineRegistry? _stateMachine;
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
        DatabaseConnectionFactory connectionFactory,
        IDialogService dialogService,
        INotificationService notificationService,
        StateMachineRegistry? stateMachine = null)
    {
        _connectionFactory = connectionFactory;
        _stateMachine = stateMachine;
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
            using var conn = await _connectionFactory.CreateOpenConnectionAsync();
            var rows = await conn.QueryAsync<ProductionItem>(
                "SELECT id AS Id, title AS Title, niche_id AS NicheId, state AS State, created_at AS CreatedAt, updated_at AS UpdatedAt FROM productions ORDER BY created_at DESC LIMIT 50;");

            foreach (var r in rows)
            {
                Productions.Add(r);
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
            var id = UlidGenerator.NewUlid();
            var now = DateTimeOffset.UtcNow.ToString("O");
            using var conn = await _connectionFactory.CreateOpenConnectionAsync();
            await conn.ExecuteAsync(@"
                INSERT INTO productions (id, state, title, language, niche_id, autonomy_mode, schema_version, created_at, updated_at)
                VALUES (@Id, 'INIT', @Title, 'en', @NicheId, 'COLLABORATIVE', '3.1.0', @Now, @Now);
            ", new { Id = id, Title = NewTopic, NicheId = NewNiche, Now = now });

            _notificationService.AddNotification($"Created production {id} ({NewTopic})", "Success");
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
            using var conn = await _connectionFactory.CreateOpenConnectionAsync();
            await conn.ExecuteAsync(@"
                UPDATE productions
                SET state = 'CANCELLED', updated_at = datetime('now')
                WHERE id = @Id;
            ", new { Id = SelectedProduction.Id });

            _notificationService.AddNotification($"Cancelled production {SelectedProduction.Id}", "Info");
            await LoadProductionsAsync();
        }
        catch (Exception ex)
        {
            _notificationService.AddNotification($"Error cancelling production: {ex.Message}", "Error");
        }
    }
}

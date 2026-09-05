using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using AMCCA.App.Common;
using AMCCA.App.Services;
using AMCCA.Core.Contracts;
using AMCCA.Core.Jobs;
using AMCCA.Core.Operator;

namespace AMCCA.App.ViewModels;

/// <summary>
/// SPEC/14 + SPEC/62 operator job queue: a paged view of jobs with their leases, and the one operator
/// action SPEC/14 requires -- requeueing a dead-lettered job, which "is never silently dropped and never
/// automatically retried; it waits for an operator".
///
/// Refresh is manual rather than event-driven: SPEC/62 asks for event-driven updates, but there is no
/// domain-event subscription reaching the UI layer in this build, and a polling timer would be exactly
/// the fabricated liveness SPEC/62 warns against. Manual refresh is the honest option until an event
/// channel exists.
/// </summary>
public class JobQueueViewModel : ViewModelBase
{
    public const string AllStatesLabel = "(all states)";

    private readonly OperatorControlService _operatorControlService;
    private readonly IDialogService _dialogService;
    private readonly INotificationService _notificationService;

    private string _selectedState = AllStatesLabel;
    private JobQueueEntry? _selectedJob;
    private int _pageIndex;
    private int _pageSize = 50;
    private int _totalCount;
    private bool _isLoading;

    // Loads are started from the constructor and from the filter/page-size setters as well as from the
    // Refresh button, so several can be in flight at once. Each load takes a token and only applies its
    // results if no newer load has started since -- otherwise a slow earlier query could overwrite the
    // grid with rows for a filter the operator has already moved off.
    private int _loadRequestToken;

    // SPEC/60 obligation 7: a long-running load must be cancelable, not just superseded. Cancelling this
    // actually stops the in-flight query via the CancellationToken passed to OperatorControlService,
    // instead of merely discarding results the query would still finish computing.
    private CancellationTokenSource? _loadCts;

    public ObservableCollection<JobQueueEntry> Jobs { get; } = new();
    public ObservableCollection<string> AvailableStates { get; } = new();

    public string SelectedState
    {
        get => _selectedState;
        set
        {
            if (SetProperty(ref _selectedState, value))
            {
                PageIndex = 0;
                _ = LoadJobsAsync();
            }
        }
    }

    public JobQueueEntry? SelectedJob
    {
        get => _selectedJob;
        set
        {
            if (SetProperty(ref _selectedJob, value))
            {
                OnPropertyChanged(nameof(CanRequeueSelectedJob));
            }
        }
    }

    public int PageIndex
    {
        get => _pageIndex;
        private set
        {
            if (SetProperty(ref _pageIndex, value))
            {
                OnPropertyChanged(nameof(PageSummary));
                OnPropertyChanged(nameof(CanGoToPreviousPage));
                OnPropertyChanged(nameof(CanGoToNextPage));
            }
        }
    }

    public int PageSize
    {
        get => _pageSize;
        set
        {
            if (value > 0 && SetProperty(ref _pageSize, value))
            {
                // TotalPages and everything derived from it change with the page size even when the row
                // count does not, and TotalCount's setter will not fire if the count is unchanged.
                OnPropertyChanged(nameof(TotalPages));
                OnPropertyChanged(nameof(PageSummary));
                OnPropertyChanged(nameof(CanGoToPreviousPage));
                OnPropertyChanged(nameof(CanGoToNextPage));

                PageIndex = 0;
                _ = LoadJobsAsync();
            }
        }
    }

    public int TotalCount
    {
        get => _totalCount;
        private set
        {
            if (SetProperty(ref _totalCount, value))
            {
                OnPropertyChanged(nameof(TotalPages));
                OnPropertyChanged(nameof(PageSummary));
                OnPropertyChanged(nameof(CanGoToPreviousPage));
                OnPropertyChanged(nameof(CanGoToNextPage));
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public int TotalPages => TotalCount <= 0 ? 1 : (TotalCount + PageSize - 1) / PageSize;

    public string PageSummary => $"Page {PageIndex + 1} of {TotalPages} — {TotalCount} job(s)";

    public bool CanGoToPreviousPage => PageIndex > 0;

    public bool CanGoToNextPage => PageIndex + 1 < TotalPages;

    /// <summary>
    /// Enables the button only for a dead-lettered row. This is a presentation hint, not the decision:
    /// SPEC/62 forbids caching a permission, so the real check is re-asked atomically inside
    /// JobManager.RequeueDeadLetterJobAsync, which refuses with AMCCA-JOB-003 if the job left DEAD_LETTER
    /// between the screen loading and the click.
    /// </summary>
    public bool CanRequeueSelectedJob => SelectedJob?.IsDeadLettered == true;

    public ICommand RefreshCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand PreviousPageCommand { get; }
    public ICommand RequeueDeadLetterJobCommand { get; }
    public ICommand CancelLoadCommand { get; }

    public JobQueueViewModel(
        OperatorControlService operatorControlService,
        IDialogService dialogService,
        INotificationService notificationService)
    {
        _operatorControlService = operatorControlService;
        _dialogService = dialogService;
        _notificationService = notificationService;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        NextPageCommand = new AsyncRelayCommand(GoToNextPageAsync, () => CanGoToNextPage);
        PreviousPageCommand = new AsyncRelayCommand(GoToPreviousPageAsync, () => CanGoToPreviousPage);
        RequeueDeadLetterJobCommand = new AsyncRelayCommand(RequeueSelectedJobAsync, () => CanRequeueSelectedJob);
        CancelLoadCommand = new RelayCommand(() => _loadCts?.Cancel());

        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        await LoadAvailableStatesAsync();
        await LoadJobsAsync();
    }

    public async Task LoadAvailableStatesAsync()
    {
        try
        {
            var previous = SelectedState;
            var states = await _operatorControlService.ListDistinctJobStatesAsync();

            AvailableStates.Clear();
            AvailableStates.Add(AllStatesLabel);
            foreach (var state in states)
            {
                AvailableStates.Add(state);
            }

            // Keep the operator's filter if the state still exists; otherwise fall back to all states
            // without firing a reload, since LoadJobsAsync runs right after this.
            if (!AvailableStates.Contains(previous))
            {
                SetProperty(ref _selectedState, AllStatesLabel, nameof(SelectedState));
                PageIndex = 0;
            }
        }
        catch (Exception ex)
        {
            _notificationService.AddNotification($"Failed to load job states: {ex.Message}", "Error");
        }
    }

    public async Task LoadJobsAsync()
    {
        var token = ++_loadRequestToken;
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        IsLoading = true;
        try
        {
            var stateFilter = SelectedState == AllStatesLabel ? null : SelectedState;
            var pageSize = PageSize;

            var total = await _operatorControlService.CountJobsAsync(stateFilter, cts.Token);

            // A page that no longer exists (rows drained since the last load) lands on the last page
            // rather than showing an empty grid with no explanation.
            var totalPages = total <= 0 ? 1 : (total + pageSize - 1) / pageSize;
            var pageIndex = PageIndex >= totalPages ? totalPages - 1 : PageIndex;

            var entries = await _operatorControlService.ListJobsAsync(stateFilter, pageSize, pageIndex * pageSize, cts.Token);

            if (token != _loadRequestToken)
            {
                return; // a newer load has started; its results are the ones that count
            }

            TotalCount = total;
            PageIndex = pageIndex;

            Jobs.Clear();
            foreach (var entry in entries)
            {
                Jobs.Add(entry);
            }

            SelectedJob = null;
        }
        catch (OperationCanceledException)
        {
            // The operator cancelled this load deliberately (SPEC/60 obligation 7); a newer load or
            // page/filter change already took over, or nothing has -- either way this is not an error.
        }
        catch (Exception ex)
        {
            _notificationService.AddNotification($"Failed to load job queue: {ex.Message}", "Error");
        }
        finally
        {
            if (token == _loadRequestToken)
            {
                IsLoading = false;
            }
            if (ReferenceEquals(_loadCts, cts))
            {
                _loadCts = null;
            }
            cts.Dispose();
        }
    }

    public async Task GoToNextPageAsync()
    {
        if (!CanGoToNextPage) return;
        PageIndex++;
        await LoadJobsAsync();
    }

    public async Task GoToPreviousPageAsync()
    {
        if (!CanGoToPreviousPage) return;
        PageIndex--;
        await LoadJobsAsync();
    }

    public async Task RequeueSelectedJobAsync()
    {
        var job = SelectedJob;
        if (job == null) return;

        var confirmed = await _dialogService.ShowConfirmAsync(
            "Confirm Requeue",
            $"Requeue dead-lettered job {job.Id} ({job.Type})? It has used {job.Attempt} of {job.MaxAttempts} attempts.");
        if (!confirmed) return;

        try
        {
            var correlationId = Guid.NewGuid().ToString("N");
            await _operatorControlService.RequeueDeadLetterJobAsync(
                operatorId: "operator",
                jobId: job.Id,
                reason: "Operator requeued a dead-lettered job from the Job Queue",
                correlationId: correlationId);

            _notificationService.AddNotification($"Job {job.Id} requeued.", "Success");
            await LoadJobsAsync();
        }
        catch (AmccaException ex)
        {
            // SPEC/62: an error reaching the UI carries its SPEC/05 code, a human message and an
            // operator action. ex.Message already begins with "[ErrorCode] ..." (AmccaException's own
            // constructor), so prefixing ex.ErrorCode again here would show the code twice.
            _notificationService.AddNotification(
                $"{ex.Message} Refresh the queue to see the job's current state.",
                "Error");
        }
        catch (Exception ex)
        {
            _notificationService.AddNotification($"Failed to requeue job {job.Id}: {ex.Message}", "Error");
        }
    }
}

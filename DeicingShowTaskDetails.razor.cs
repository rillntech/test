using Acdm.UI.AirsideOptimizer.Model.Deice.TaskDetails;
using Acdm.UI.AirsideOptimizer.Services.ResourceData;
using Acdm.UI.AirsideOptimizer.Shared.Auth.Pages;
using Microsoft.AspNetCore.Components;
using System.Threading.Tasks;

namespace Acdm.UI.AirsideOptimizer.Pages.DeicingPlanner;

public partial class DeicingShowTaskDetails : AuthBasePage
{
    [Parameter] public bool Visible { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    [Parameter] public IEnumerable<string>? TaskTypeOptions { get; set; }
    [Parameter] public IEnumerable<string>? ResourceOptions { get; set; }
    [Parameter] public IEnumerable<string>? DeicingHandlerOptions { get; set; }
    [Parameter] public IEnumerable<string>? PadTruckOptions { get; set; }
    [Parameter] public IEnumerable<string>? FixedPadTruckOptions { get; set; }
    [Parameter] public int? TaskId { get; set; }

    [Inject]
    private IResourceServiceGeneric<TaskDetailsDTONew> TaskInformationService { get; set; } = null!;

    public TaskDetailsDTONew? TaskInformation { get; set; }
    private bool _isInitialDataLoadComplete = false;
    private int? _loadedTaskId;
    private int _activeTab = 0;
    private bool _identificationExpanded = true;
    private bool _timesExpanded = false;
    private bool _requestExpanded = false;
    private bool _otherExpanded = false;

    public DeicingShowTaskDetails()
        : base("Deice.DeicingShowTaskDetails", string.Empty, isComponent: true)
    {
    }

    protected override async Task OnBaseInitializedAsync()
    {
        await base.OnBaseInitializedAsync();
    }

    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();

        if (!TaskId.HasValue)
        {
            TaskInformation = null;
            _loadedTaskId = null;
            _isInitialDataLoadComplete = true;
            return;
        }

        if (_loadedTaskId == TaskId.Value && TaskInformation is not null)
        {
            _isInitialDataLoadComplete = true;
            return;
        }

        _isInitialDataLoadComplete = false;
        TaskInformation = null;

        try
        {
            TaskInformation = await TaskInformationService.GetShowTaskAsync(
                CurrentUser.UserToken.ApiAccessToken,
                TaskId.Value.ToString());
            _loadedTaskId = TaskId.Value;
        }
        catch (Exception ex)
        {
            _ = this.Log.LogError(ex, "Error loading task details for task id {TaskId}.", TaskId);
            TaskInformation = null;
        }
        finally
        {
            _isInitialDataLoadComplete = true;
        }
    }

    private void OnTabChanged(int newIndex)
    {
        _activeTab = newIndex;
        StateHasChanged();
    }

    private void ToggleIdentification()
    {
        _identificationExpanded = !_identificationExpanded;
        StateHasChanged();
    }

    private void ToggleTimes()
    {
        _timesExpanded = !_timesExpanded;
        StateHasChanged();
    }

    private void ToggleRequest()
    {
        _requestExpanded = !_requestExpanded;
        StateHasChanged();
    }

    private void ToggleOther()
    {
        _otherExpanded = !_otherExpanded;
        StateHasChanged();
    }

    protected IEnumerable<string> TaskTypeOptionsList
        => (TaskTypeOptions is not null && TaskTypeOptions.Any())
            ? TaskTypeOptions
            : new[] { TaskInformation?.DeicingResource ?? string.Empty };

    protected IEnumerable<string> ResourceOptionsList
        => (ResourceOptions is not null && ResourceOptions.Any())
            ? ResourceOptions
            : new[] { TaskInformation?.ResourceType ?? string.Empty };

    protected IEnumerable<string> DeicingHandlerOptionsList
        => (DeicingHandlerOptions is not null && DeicingHandlerOptions.Any())
            ? DeicingHandlerOptions
            : new[] { TaskInformation?.DeicingHandler ?? string.Empty };

    protected IEnumerable<string> PadTruckOptionsList
        => (PadTruckOptions is not null && PadTruckOptions.Any())
            ? PadTruckOptions
            : new[] { TaskInformation?.RequestedPadOrTruck ?? string.Empty };

    protected IEnumerable<string> FixedPadTruckOptionsList
        => (FixedPadTruckOptions is not null && FixedPadTruckOptions.Any())
            ? FixedPadTruckOptions
            : new[] { TaskInformation?.RequestedFixedPadOrTruck ?? string.Empty };

    public static string Show(string? value, string empty = "--")
        => string.IsNullOrWhiteSpace(value) ? empty : value!;

    public static string ShowBool(bool? value)
        => value.HasValue ? (value.Value ? "Yes" : "No") : "--";

    public static string ShowTime(DateTimeOffset? dto)
        => dto.HasValue ? dto.Value.ToString("dd MMM yyyy HH:mm") : "--";

    public static string ShowDuration(TimeSpan? ts)
        => ts.HasValue ? ts.Value.ToString(@"hh\:mm\:ss") : "--";
}
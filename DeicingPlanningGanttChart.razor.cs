using Acdm.Deice.InformationServices.Dto.Planner;
using Acdm.InformationServices.Dto;
using Acdm.PubSub.Subscriber;
using Acdm.UI.AirsideOptimizer.API.ResourceData;
using Acdm.UI.AirsideOptimizer.Model.Deice;
using Acdm.UI.AirsideOptimizer.PubSubSubscriber;
using Acdm.UI.AirsideOptimizer.Shared.Auth.Pages;
using Blazored.Toast.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Newtonsoft.Json;
using Radzen;
using Radzen.Blazor;

namespace Acdm.UI.AirsideOptimizer.Pages.DeicingPlanner
{
    public partial class DeicingPlanningGanttChart : AuthBasePage
    {
        /// <summary>
        /// Publish key used to subscribe to resource replan notifications via SignalR.
        /// </summary>
        public enum ResourceReplanPublishType
        {
            PublishResourceReplan
        }

        [Parameter] public List<ResourceItem>? Data { get; set; }
        [Parameter] public bool ShowTimeline { get; set; } = true;
        [Parameter] public DateTime TimelineStart { get; set; }
        [Parameter] public int SlotMinutes { get; set; }
        [Inject] public IResourceApiGeneric<ResourcePlannerDto> ResourcePlannerService { get; set; } = null!;
        [Inject] public ISignalRSubscriptionService<ResourcePlannerDto> SignalRService { get; set; } = null!;
        public List<ResourcePlannerDto> resourcePlannerDtos { get; set; } = new();

        public ResourceToolTipDto toolTipDto { get; set; } = new();

        public List<TaskPopupInfoDto> TaskPopupInfoDtos { get; set; } = new();

        [Parameter] public int PlanningHorizon { get; set; }
        [Parameter] public int SelectedHours { get; set; }
         private int SnapCellMinutes { get; set; } = 5;

        private readonly EventHandler<PublishedChangeEventArgs> _realTimeNotificationEventHandlerReference;
        [Inject] public IResourceApiGeneric<OperatorDeicePlannerDto> ReplanService { get; set; } = null!;
        [Inject] public IResourceApiGeneric<ResourceToolTipDto> TaskTooltipService { get; set; } = null!;

        public bool ShouldShowLabel(DateTime slot) =>
            SelectedHours <= 6 || slot.Minute == 0;

        //  Canvas geometry 
        private List<DateTime> _timeSlots = new();
        private List<DateTime> _daySlots = new();
        private double _totalCanvasMinutes;

        public DateTime CanvasOrigin => TimelineStart;
        public DateTime SnappedStart => _snappedStart;
        private DateTime _snappedStart;
        private DateTime _snappedEnd;
        private string _currentTimeLeftPct = "0%";
        private string _horizonLeftPct = "0%";

        //  Drag state
        private AllocationDto? _dragAlloc;
        private ResourcePlannerDto? _dragSourcePlanner;
        private int _dragSourceRowIndex = -1;
        private int _dragHoverRowIndex = -1;
        private bool _isDragging;
        private double _dragStartClientX;
        private double _dragStartClientY;
        private double _ghostClientX;
        private double _ghostClientY;
        private TaskPopupInfoDto? _taskPopupInfo;
        private double _tooltipX;
        private double _tooltipY;
        // Availability snap-grid 
        /// Cache: resourceId → run-length segments for the snap-grid overlay.
        /// Built once when drag starts, covering every visible row.
        /// Key = ResourceId, Value = list of (leftPct, widthPct, isOccupied).
        private Dictionary<int, List<(string LeftPct, string WidthPct, bool IsOccupied)>> _allRowAvailability = new();

        [Inject] private ContextMenuService FlightMenu { get; set; } = null!;

        private AllocationDto? _contextMenuAllocation;
        private bool _taskDetailsVisible;
        private int? _taskDetailsTaskId;

        //  Lifecycle
        public DeicingPlanningGanttChart() : base("Deice.DeicingPlanningGanttChart", string.Empty, true)
        {
            _realTimeNotificationEventHandlerReference = async (object? sender, PublishedChangeEventArgs args)
                => await InvokeAsync(() => HandleRealTimeNotification(sender, args));
        }

        protected override async Task OnBaseInitializedAsync()
        {
            BuildTimeSlots();
            BuildDaySlots();
            _currentTimeLeftPct = GetMarkerLeftPct(DateTime.UtcNow);
            _horizonLeftPct = GetMarkerLeftPct(DateTime.UtcNow.AddHours(PlanningHorizon));
            await GetResourceDataAsync();
            await GetTooltipInfo();
            try
            {
                await InitializeRealTimeUpdatesAsync();
            }
            catch (Exception ex)
            {
                _ = this.Log.LogError(ex, "Error occurred while initializing SignalR for DeicingPlanningGanttChart.");
            }
        }

        protected override void OnParametersSet()
        {
            BuildTimeSlots();
            BuildDaySlots();
            _currentTimeLeftPct = GetMarkerLeftPct(DateTime.UtcNow);
            _horizonLeftPct = GetMarkerLeftPct(DateTime.UtcNow.AddHours(PlanningHorizon));
        }

        private void OpenMenuAtClick(MouseEventArgs e, AllocationDto alloc)
        {
            _contextMenuAllocation = alloc;
            FlightMenu.Open(e, _ => BuildContextMenu());
        }

        private async Task OnGanttContextMenuItemClick(MenuItemEventArgs args)
        {
            FlightMenu.Close();

            if (!string.Equals(
                    args.Value?.ToString(),
                    GanttChartTabsConstants.ShowTaskDetailsCommandName,
                    StringComparison.Ordinal))
            {
                return;
            }

            var taskId = GetTaskIdForShowDetails(_contextMenuAllocation);
            if (!taskId.HasValue)
                return;

            _taskDetailsTaskId = taskId;
            _taskDetailsVisible = true;
            await InvokeAsync(StateHasChanged);
        }

        /// <summary>
        /// Maps the allocation to the task identifier expected by <see cref="DeicingShowTaskDetails"/>.
        /// If your API expects a different field (for example deicing usage id), adjust this method.
        /// </summary>
        private int? GetTaskIdForShowDetails(AllocationDto? alloc)
        {
            if (alloc?.FlightId is int id)
                return id;

            return null;
        }

        private Task OnTaskDetailsClosed()
        {
            _taskDetailsVisible = false;
            _taskDetailsTaskId = null;
            StateHasChanged();
            return Task.CompletedTask;
        }

        private RenderFragment BuildContextMenu() => builder =>
        {
            builder.OpenComponent<RadzenMenu>(0);
            builder.AddAttribute(1, "Click", EventCallback.Factory.Create<MenuItemEventArgs>(this, OnGanttContextMenuItemClick));
            builder.AddAttribute(2, "ChildContent", (RenderFragment)(childBuilder =>
            {
                childBuilder.OpenComponent<RadzenMenuItem>(3);
                childBuilder.AddAttribute(4, "Text", Translation.GetEntry("Deice.DeicingPlanningEditTask"));
                childBuilder.AddAttribute(5, "Value", GanttChartTabsConstants.EditTaskCommandName);
                childBuilder.CloseComponent();

                childBuilder.OpenComponent<RadzenMenuItem>(6);
                childBuilder.AddAttribute(7, "Text", Translation.GetEntry("Deice.DeicingPlanningShowtaskDetails"));
                childBuilder.AddAttribute(8, "Value", GanttChartTabsConstants.ShowTaskDetailsCommandName);
                childBuilder.CloseComponent();

                childBuilder.OpenComponent<RadzenMenuItem>(9);
                childBuilder.AddAttribute(10, "Text", Translation.GetEntry("Deice.DeicingPlanningShowtaskAssignmentproposal"));
                childBuilder.AddAttribute(11, "Value", GanttChartTabsConstants.ShowTaskAssigmentProposalCommandName);
                childBuilder.CloseComponent();

                childBuilder.OpenComponent<RadzenMenuItem>(12);
                childBuilder.AddAttribute(13, "Text", Translation.GetEntry("Deice.DeicingPlanningUnassignTaskFromResource"));
                childBuilder.AddAttribute(14, "Value", GanttChartTabsConstants.UnAssignTaskFromResourceCommandName);
                childBuilder.CloseComponent();

                childBuilder.OpenComponent<RadzenMenuItem>(15);
                childBuilder.AddAttribute(16, "Text", Translation.GetEntry("Deice.DeicingPlanningMarktask"));
                childBuilder.AddAttribute(17, "Value", GanttChartTabsConstants.MarkTaskCommandName);
                childBuilder.CloseComponent();
            }));
            builder.CloseComponent();
        };


        private async Task GetTooltipInfo()
        {
            try
            {
                var data = await TaskTooltipService.GetAsync(CurrentUser.UserToken.ApiAccessToken);
                toolTipDto = data ?? new();
            }
            catch (Exception ex)
            {
                _ = this.Log.LogError(ex, "Error occurred while loading tooltip data for DeicingPlanningGanttChart.");
            }
        }

        private void ShowTooltip(MouseEventArgs e, AllocationDto alloc)
        {
            if (_isDragging == true) return;
            if (alloc.FlightId.HasValue)
            {
                _taskPopupInfo = toolTipDto.TaskPopups?.FirstOrDefault(f => f.FlightId == alloc.FlightId);
                _tooltipX = e.ClientX;
                _tooltipY = e.ClientY;
            }
        }

        private void HideTooltip() => _taskPopupInfo = null;

        private async Task InitializeRealTimeUpdatesAsync()
        {
            var deicePubSubUrl = Configuration.GetValue<string>("InternalUrls:DeicePubSubUrl");

            if (string.IsNullOrWhiteSpace(deicePubSubUrl))
            {
                _ = this.Log.LogError("DeicingPlanningGanttChart.SignalRConfigurationLogError");
                return;
            }

            SignalRService.OnDataUpdated += _realTimeNotificationEventHandlerReference;

            await SignalRService.InitializeAsync(
                ResourceReplanPublishType.PublishResourceReplan,
                CurrentUser.UserToken.ApiAccessToken,
                deicePubSubUrl,
                message =>
                {
                    _ = this.Log.LogWarning($"SignalR connection warning: {message}");
                    Toast.ShowWarning(Translation.GetEntry(message));
                }
            );
        }

        private async Task HandleRealTimeNotification(object? sender, PublishedChangeEventArgs args)
        {
            try
            {
                _ = sender;
                if (args?.ChangedData is null) return;

                if (args.ChangedData is AuditDto<ResourcePlannerDto> notification)
                {
                    if (SignalRSubscriptionService<ResourcePlannerDto>.IsUpdateNotification(notification)
                        && notification.NewRecord is not null)
                    {
                        var index = resourcePlannerDtos.FindIndex(r => r.ResourceId == notification.NewRecord.ResourceId);
                        if (index >= 0)
                            resourcePlannerDtos[index] = notification.NewRecord;
                        else
                            resourcePlannerDtos.Add(notification.NewRecord);
                    }
                }

                await InvokeAsync(StateHasChanged);
            }
            catch (Exception ex)
            {
                _ = this.Log.LogError(ex, "Error occurred while handling real-time resource planner notification.");
            }
        }

        public override async void OnDispose()
        {
            SignalRService.OnDataUpdated -= _realTimeNotificationEventHandlerReference;
            await SignalRService.UnSubscribeChanges();
            base.OnDispose();
        }

        public bool IsAllocationVisible(AllocationDto alloc)
        {
            if (alloc.DeiceStartTime is null || alloc.DeiceEndTime is null) return false;
            var canvasEnd = TimelineStart.AddMinutes(_totalCanvasMinutes);
            return alloc.DeiceEndTime.Value.UtcDateTime > TimelineStart
                && alloc.DeiceStartTime.Value.UtcDateTime < canvasEnd;
        }

        public string GetAllocationLeftPct(DateTimeOffset? start)
        {
            if (start is null || _totalCanvasMinutes <= 0) return "0%";
            var canvasStart = TimelineStart;
            var effective = start.Value.UtcDateTime < canvasStart ? canvasStart : start.Value.UtcDateTime;
            var pct = (effective - canvasStart).TotalMinutes / _totalCanvasMinutes * 100.0;
            pct = Math.Max(0, Math.Min(100, pct));
            return $"{pct:F4}%";
        }

        public string GetAllocationWidthPct(DateTimeOffset? start, DateTimeOffset? end)
        {
            if (start is null || end is null || _totalCanvasMinutes <= 0) return "0.1%";
            var canvasStart = TimelineStart;
            var canvasEnd = TimelineStart.AddMinutes(_totalCanvasMinutes);
            var effectiveStart = start.Value.UtcDateTime < canvasStart ? canvasStart : start.Value.UtcDateTime;
            var effectiveEnd = end.Value.UtcDateTime > canvasEnd ? canvasEnd : end.Value.UtcDateTime;
            var durationMinutes = (effectiveEnd - effectiveStart).TotalMinutes;
            if (durationMinutes <= 0) return "0.1%";
            var pct = durationMinutes / _totalCanvasMinutes * 100.0;
            pct = Math.Max(0.1, pct);
            return $"{pct:F4}%";
        }

        public string GetAllocationStyle(AllocationDto alloc, int overlapIndex = 0, int pillHeightPx = 24)
        {
            const int rowHeightPx = 36;
            var left = GetAllocationLeftPct(alloc.DeiceStartTime);
            var width = GetAllocationWidthPct(alloc.DeiceStartTime, alloc.DeiceEndTime);
            var background = alloc.IsMarkTask
                ? "linear-gradient(to bottom, #C3C3C3 50%, #87CEEB 50%)"
                : "#C3C3C3";
            var topPx = overlapIndex == 0
                ? (rowHeightPx - pillHeightPx) / 2
                : overlapIndex * (pillHeightPx + 2);
            return $"left:{left}; width:{width}; top:{topPx}px; height:{pillHeightPx}px; " +
                   $"position:absolute; background:{background};";
        }

        /// Ghost pill style — fixed position tracking the pointer during drag.
        public string GetDragStyle()
        {
            if (_dragAlloc is null) return string.Empty;
            var width = GetAllocationWidthPct(_dragAlloc.DeiceStartTime, _dragAlloc.DeiceEndTime);
            var background = _dragAlloc.IsMarkTask
                ? "linear-gradient(to bottom, #C3C3C3 50%, #87CEEB 50%)"
                : "#C3C3C3";
            return $"position:fixed; left:{_ghostClientX:F0}px; top:{_ghostClientY:F0}px; " +
                   $"width:{width}; height:24px; background:{background}; " +
                   $"opacity:0.75; z-index:9999; pointer-events:none; " +
                   $"border-radius:3px; box-shadow:0 4px 12px rgba(0,0,0,0.25);";
        }

        //  Drag handlers 

        private void OnPillPointerDown(PointerEventArgs e, AllocationDto alloc, ResourceItem sourceItem, int rowIndex)
        {
            if (e.Button != 0) return;

            _dragAlloc = alloc;
            _dragSourcePlanner = resourcePlannerDtos
                .Find(f => f.ResourceId == sourceItem.Id
                    && string.Equals(f.ResourceType.ToString(), sourceItem.ResourceType, StringComparison.OrdinalIgnoreCase));
            _dragSourceRowIndex = rowIndex;
            _dragHoverRowIndex = rowIndex;
            _isDragging = true;
            _dragStartClientX = e.ClientX;
            _dragStartClientY = e.ClientY;
            _ghostClientX = e.ClientX;
            _ghostClientY = e.ClientY - 12;

            // Build the snap-grid for ALL rows once at drag start
            BuildAllRowAvailability();
        }

        private void OnPointerMove(PointerEventArgs e)
        {
            if (!_isDragging || _dragAlloc is null) return;

            _ghostClientX = e.ClientX;
            _ghostClientY = e.ClientY - 12;

            var deltaY = e.ClientY - _dragStartClientY;
            const int rowHeightPx = 36;
            var rowDelta = (int)Math.Round(deltaY / (double)rowHeightPx);
            _dragHoverRowIndex = Math.Clamp(_dragSourceRowIndex + rowDelta, 0, _rowCount - 1);
        }

        private async Task OnPointerUp(PointerEventArgs e)
        {
            if (!_isDragging || _dragAlloc is null)
            {
                ResetDrag();
                return;
            }

            var alloc = _dragAlloc;
            var sourcePlanner = _dragSourcePlanner;
            var targetRowIndex = _dragHoverRowIndex;

            var deltaXPx = e.ClientX - _dragStartClientX;
            var minutesPerPixel = _totalCanvasMinutes / GetBodyWidthPx();
            var deltaMinutes = deltaXPx * minutesPerPixel;

            ResetDrag();

            if (sourcePlanner is null || alloc.DeiceStartTime is null || alloc.DeiceEndTime is null)
                return;

            var duration = alloc.DeiceEndTime.Value - alloc.DeiceStartTime.Value;
            var newStart = alloc.DeiceStartTime.Value.AddMinutes(deltaMinutes);
            var newEnd = newStart + duration;

            // Snap to SlotMinutes boundary
            if (SlotMinutes > 0)
            {
                var totalFromOrigin = (newStart.UtcDateTime - TimelineStart).TotalMinutes;
                var snapped = Math.Round(totalFromOrigin / SlotMinutes) * SlotMinutes;
                newStart = new DateTimeOffset(TimelineStart.AddMinutes(snapped), TimeSpan.Zero);
                newEnd = newStart + duration;
            }

            var targetItem = GetItemAtRowIndex(targetRowIndex);
            if (targetItem is null) return;

            var targetPlanner = resourcePlannerDtos
                .Find(f => f.ResourceId == targetItem.Id
                    && string.Equals(f.ResourceType.ToString(), targetItem.ResourceType, StringComparison.OrdinalIgnoreCase));

            if (targetItem.Id == sourcePlanner.ResourceId && Math.Abs(deltaMinutes) < 1)
                return;

            // Block drop onto occupied or impaired minutes
            if (!IsDropAllowed(targetPlanner, newStart.UtcDateTime, newEnd.UtcDateTime))
                return;

            // Build OperatorDeicePlannerDto
            var previousAllocation = new AllocationDto
            {
                ResourceId = alloc.ResourceId,
                ResourceName = alloc.ResourceName,
                ResourceType  = alloc.ResourceType,
                FlightId = alloc.FlightId,
                StartEpochMinute = alloc.StartEpochMinute,
                DurationMin=alloc.DurationMin,
                DeiceStartTime = alloc.DeiceStartTime,
                DeiceEndTime = alloc.DeiceEndTime,
                FlightName = alloc.FlightName,
                IsMarkTask = alloc.IsMarkTask
            };

            var newAllocation = new AllocationDto
            {
                ResourceId = targetItem.Id,
                ResourceName = targetItem.Name,
                ResourceType = Enum.Parse<Acdm.Deice.InformationServices.Dto.Enums.ResourceTypes>(targetItem.ResourceType, ignoreCase: true),
                FlightId = alloc.FlightId,
                StartEpochMinute = alloc.StartEpochMinute,
                DurationMin = alloc.DurationMin,
                DeiceStartTime = newStart,
                DeiceEndTime = newEnd,
                FlightName = alloc.FlightName,
                IsMarkTask = alloc.IsMarkTask
            };

            var replanDto = new OperatorDeicePlannerDto
            {
                IsReplan = true,
                PreviousAllocation = previousAllocation,
                NewAllocation = newAllocation
            };
            await SubmitReplanAsync(replanDto);
        }

        internal void OnPointerCancel(PointerEventArgs e) => ResetDrag();

        //  Availability snap-grid helpers

        /// <summary>
        /// Builds snap-grid segments for every visible row at once.
        /// Each cell is <see cref="SnapCellMinutes"/> wide (default 5).
        /// Called once when the drag starts — avoids rebuilding on every pointermove.
        /// </summary>
        private void BuildAllRowAvailability()
        {
            _allRowAvailability.Clear();
            if (_totalCanvasMinutes <= 0 || Data is null) return;

            foreach (var item in Data)
            {
                if (item is null) continue;

                var planner = resourcePlannerDtos
                    .Find(f => f.ResourceId == item.Id
                        && string.Equals(f.ResourceType.ToString(), item.ResourceType, StringComparison.OrdinalIgnoreCase));

                _allRowAvailability[item.Id] = BuildSnapCells(planner);
            }
        }

        /// <summary>
        /// Returns snap-grid cells for a single planner row.
        /// Each cell spans <see cref="SnapCellMinutes"/> minutes; its colour is determined by
        /// whether <b>any</b> minute inside that cell is '1' (occupied) in
        /// <c>ResourceAvailabilityPerMinute</c>, or whether the cell overlaps an impairment.
        /// </summary>
        public List<(string LeftPct, string WidthPct, bool IsOccupied)> BuildSnapCells(ResourcePlannerDto? planner)
        {
            var result = new List<(string, string, bool)>();
            if (_totalCanvasMinutes <= 0) return result;

            var snapMinutes = Math.Max(SnapCellMinutes, 1);
            var cellWidthPct = snapMinutes / _totalCanvasMinutes * 100.0;
            var av = planner?.Availability?.ResourceAvailabilityPerMinute;
            var canvasEnd = TimelineStart.AddMinutes(_totalCanvasMinutes);

            var current = 0;
            while (current < (int)_totalCanvasMinutes)
            {
                var cellEnd = current + snapMinutes;
                var leftPct = current / _totalCanvasMinutes * 100.0;

                var occupied = IsCellOccupiedByAvailability(av, current, cellEnd)
                            || IsCellOccupiedByImpairment(planner, current, cellEnd, canvasEnd);

                result.Add(($"{leftPct:F4}%", $"{cellWidthPct:F4}%", occupied));
                current += snapMinutes;
            }

            return result;
        }

        /// <summary>
        /// Returns true when any minute in [<paramref name="cellStart"/>, <paramref name="cellEnd"/>)
        /// is marked '1' in the availability bit-string.
        /// </summary>
        private static bool IsCellOccupiedByAvailability(string? av, int cellStart, int cellEnd)
        {
            if (string.IsNullOrEmpty(av)) return false;

            for (var m = cellStart; m < cellEnd && m < av.Length; m++)
            {
                if (av[m] == '1') return true;
            }

            return false;
        }

        /// <summary>
        /// Returns true when the cell time range overlaps any impairment on the planner.
        /// </summary>
        private bool IsCellOccupiedByImpairment(ResourcePlannerDto? planner, int cellStart, int cellEnd, DateTime canvasEnd)
        {
            if (planner?.Impairments is not { Count: > 0 }) return false;

            var cellStartTime = TimelineStart.AddMinutes(cellStart);
            var cellEndTime = TimelineStart.AddMinutes(cellEnd);

            foreach (var imp in planner.Impairments)
            {
                var impStart = imp.StartTime.UtcDateTime;
                var impEnd = imp.EndTime?.UtcDateTime ?? canvasEnd;

                if (cellStartTime < impEnd && cellEndTime > impStart) return true;
            }

            return false;
        }

        /// <summary>
        /// Returns the cached snap-grid cells for a row identified by its <see cref="ResourceItem"/>.
        /// Falls back to an empty list when no availability data exists.
        /// </summary>
        public List<(string LeftPct, string WidthPct, bool IsOccupied)> GetSnapCellsForItem(ResourceItem? item)
        {
            if (item is null || !_allRowAvailability.TryGetValue(item.Id, out var cells))
                return new();
            return cells;
        }

        /// <summary>
        /// Returns true when every minute in [dropStart, dropEnd) is available (bit = '0')
        /// and the range does not overlap any impairment on the target planner.
        /// </summary>
        public bool IsDropAllowed(ResourcePlannerDto? targetPlanner, DateTime dropStart, DateTime dropEnd)
        {
            if (targetPlanner is null) return true;

            return !IsRangeOccupiedByAvailability(targetPlanner, dropStart, dropEnd)
                && !IsRangeOccupiedByImpairment(targetPlanner, dropStart, dropEnd);
        }

        /// <summary>
        /// Returns true when any minute in [<paramref name="dropStart"/>, <paramref name="dropEnd"/>)
        /// is marked '1' in the availability bit-string.
        /// </summary>
        private bool IsRangeOccupiedByAvailability(ResourcePlannerDto targetPlanner, DateTime dropStart, DateTime dropEnd)
        {
            var av = targetPlanner.Availability?.ResourceAvailabilityPerMinute;
            if (string.IsNullOrEmpty(av)) return false;

            var startIdx = (int)Math.Floor((dropStart - TimelineStart).TotalMinutes);
            var endIdx = (int)Math.Ceiling((dropEnd - TimelineStart).TotalMinutes);

            for (var m = startIdx; m < endIdx; m++)
            {
                if (m < 0 || m >= av.Length) continue;
                if (av[m] == '1') return true;
            }

            return false;
        }

        /// <summary>
        /// Returns true when the drop range overlaps any impairment on the planner.
        /// </summary>
        private bool IsRangeOccupiedByImpairment(ResourcePlannerDto targetPlanner, DateTime dropStart, DateTime dropEnd)
        {
            if (targetPlanner.Impairments is not { Count: > 0 }) return false;

            var canvasEnd = TimelineStart.AddMinutes(_totalCanvasMinutes);

            foreach (var imp in targetPlanner.Impairments)
            {
                var impStart = imp.StartTime.UtcDateTime;
                var impEnd = imp.EndTime?.UtcDateTime ?? canvasEnd;

                if (dropStart < impEnd && dropEnd > impStart) return true;
            }

            return false;
        }

        // Helpers 

        private int _rowCount => Data?.Count ?? 0;

        private ResourceItem? GetItemAtRowIndex(int rowIndex)
        {
            if (Data is null || rowIndex < 0 || rowIndex >= Data.Count) return null;
            return Data[rowIndex];
        }

        private double GetBodyWidthPx() => Math.Max(SelectedHours, 1) * 120.0;

        private async Task SubmitReplanAsync(OperatorDeicePlannerDto dto)
        {
            try
            {
                var data = await ReplanService.AddAsync<OperatorDeicePlannerDto, IEnumerable<ResourcePlannerDto>>(dto, CurrentUser.UserToken.ApiAccessToken);
                if (data is null || data.Any(a => a.Errors != null))
                {
                    return;
                }
                resourcePlannerDtos = data.ToList();
                Toast.ShowSuccess(Translation.GetEntry("Deice.ReplanSuccess"));
            }
            catch (Exception ex)
            {
                _ = this.Log.LogError(ex, "Error occurred while submitting deice replan.");
            }
        }

        private void ResetDrag()
        {
            _dragAlloc = null;
            _dragSourcePlanner = null;
            _dragSourceRowIndex = -1;
            _dragHoverRowIndex = -1;
            _isDragging = false;
            _allRowAvailability.Clear();
        }

        // ── Data loading ──────────────────────────────────────────────────────────
        private async Task GetResourceDataAsync()
        {
            try
            {
                var data = await ResourcePlannerService.GetAllAsync(CurrentUser.UserToken.ApiAccessToken);
                resourcePlannerDtos = data?.ToList() ?? new();
            }
            catch (Exception ex)
            {
                _ = this.Log.LogError(ex, "Error occurred while loading Planner for Deice.");
            }
        }

        // ── Impairment segments ───────────────────────────────────────────────────
        public List<(string LeftPct, string WidthPct)> GetImpairmentSegments(int resourceId)
        {
            var result = new List<(string, string)>();
            if (_totalCanvasMinutes <= 0) return result;

            var impairments = resourcePlannerDtos
                .FirstOrDefault(r => r.ResourceId == resourceId)
                ?.Impairments;

            if (impairments is null || impairments.Count == 0) return result;

            var canvasStart = TimelineStart;
            var canvasEnd = TimelineStart.AddMinutes(_totalCanvasMinutes);

            foreach (var imp in impairments)
            {
                var start = imp.StartTime;
                var impEnd = imp.EndTime ?? (DateTimeOffset)canvasEnd;

                if (impEnd.UtcDateTime <= canvasStart || start.UtcDateTime >= canvasEnd)
                    continue;

                var clampedStart = start.UtcDateTime < canvasStart ? canvasStart : start.UtcDateTime;
                var clampedEnd = impEnd.UtcDateTime > canvasEnd ? canvasEnd : impEnd.UtcDateTime;

                var leftPct = (clampedStart - canvasStart).TotalMinutes / _totalCanvasMinutes * 100.0;
                var widthPct = (clampedEnd - clampedStart).TotalMinutes / _totalCanvasMinutes * 100.0;

                if (widthPct <= 0) continue;

                result.Add(($"{Math.Max(0, leftPct):F4}%", $"{widthPct:F4}%"));
            }

            return result;
        }

        // ── Marker / slot helpers ────────────────────────────────────────────────
        public string GetMarkerLeftPct(DateTime utcTime)
        {
            var canvasMinutes = Math.Max(SelectedHours, 1) * 60.0;
            if (canvasMinutes <= 0) return "0%";
            var truncated = new DateTime(utcTime.Year, utcTime.Month, utcTime.Day,
                                         utcTime.Hour, utcTime.Minute, 0, DateTimeKind.Utc);
            var pct = (truncated - TimelineStart).TotalMinutes / canvasMinutes * 100.0;
            return $"{pct:F4}%";
        }

        private string GetSlotLeftPct(DateTime slot)
        {
            if (_totalCanvasMinutes <= 0) return "0%";
            var pct = (slot - TimelineStart).TotalMinutes / _totalCanvasMinutes * 100.0;
            return $"{pct:F4}%";
        }

        private string GetSlotWidthPct()
        {
            if (_totalCanvasMinutes <= 0) return "0%";
            var pct = SlotMinutes / _totalCanvasMinutes * 100.0;
            return $"{pct:F4}%";
        }

        public string GetDayLabelLeftPct(DateTime day)
        {
            if (day.Date == TimelineStart.Date) return "0%";
            if (_totalCanvasMinutes <= 0) return "0%";
            var midnight = DateTime.SpecifyKind(day.Date, DateTimeKind.Utc);
            var pct = (midnight - TimelineStart).TotalMinutes / _totalCanvasMinutes * 100.0;
            return $"{pct:F4}%";
        }

        private void BuildDaySlots()
        {
            _daySlots.Clear();
            if (_timeSlots.Count == 0) return;

            DateTime? currentDay = null;
            int slotCount = 0;

            foreach (var slot in _timeSlots.GroupBy(f => f.Date))
            {
                var slotDay = slot.Key;
                if (currentDay == null)
                {
                    currentDay = slotDay;
                    slotCount = 1;
                }
                else if (slotDay == currentDay)
                {
                    slotCount++;
                }
                else
                {
                    _daySlots.Add(currentDay.Value);
                    currentDay = slotDay;
                    slotCount = 1;
                }
            }

            if (currentDay != null && slotCount > 0)
                _daySlots.Add(currentDay.Value);
        }

        private void BuildTimeSlots()
        {
            _timeSlots.Clear();
            if (SlotMinutes <= 0) return;

            var canvasEnd = TimelineStart.AddHours(Math.Max(SelectedHours, 1));

            var startRemainder = TimelineStart.Minute % SlotMinutes;
            _snappedStart = startRemainder == 0
                ? new DateTime(TimelineStart.Year, TimelineStart.Month, TimelineStart.Day,
                               TimelineStart.Hour, TimelineStart.Minute, 0, DateTimeKind.Utc)
                : new DateTime(TimelineStart.Year, TimelineStart.Month, TimelineStart.Day,
                               TimelineStart.Hour, TimelineStart.Minute, 0, DateTimeKind.Utc)
                    .AddMinutes(SlotMinutes - startRemainder);

            var endRemainder = canvasEnd.Minute % SlotMinutes;
            _snappedEnd = endRemainder == 0
                ? new DateTime(canvasEnd.Year, canvasEnd.Month, canvasEnd.Day,
                               canvasEnd.Hour, canvasEnd.Minute, 0, DateTimeKind.Utc)
                : new DateTime(canvasEnd.Year, canvasEnd.Month, canvasEnd.Day,
                               canvasEnd.Hour, canvasEnd.Minute, 0, DateTimeKind.Utc)
                    .AddMinutes(SlotMinutes - endRemainder);

            var current = _snappedStart;
            while (current <= _snappedEnd)
            {
                _timeSlots.Add(current);
                current = current.AddMinutes(SlotMinutes);
            }

            _totalCanvasMinutes = (canvasEnd - TimelineStart).TotalMinutes;
            if (_totalCanvasMinutes <= 0) _totalCanvasMinutes = SlotMinutes;
        }
    }
}
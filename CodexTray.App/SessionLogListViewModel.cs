using CodexTray.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Globalization;

namespace CodexTray.App;

/// <summary>
/// Paged Time / Model / Cost / Speed rows for one harness, without persisting the open page.
/// </summary>
public sealed partial class SessionLogListViewModel : ObservableObject
{
    public const int PageSize = 20;

    private IReadOnlyList<SessionLogEntry> m_Entries = [];
    private int m_Page;
    private bool m_SpeedEstimated;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleGlyph))]
    [NotifyPropertyChangedFor(nameof(ToggleTooltip))]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<SessionLogRow> Rows { get; private set; } = [];

    [ObservableProperty]
    public partial string Summary { get; private set; } = "0 records";

    [ObservableProperty]
    public partial string PageLabel { get; private set; } = "1 / 1";

    [ObservableProperty]
    public partial bool CanGoPrevious { get; private set; }

    [ObservableProperty]
    public partial bool CanGoNext { get; private set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; } = true;

    [ObservableProperty]
    public partial string EmptyText { get; private set; } = "No session logs";

    public string ToggleGlyph => IsOpen ? "\uE72B" : "\uE81C";

    public string ToggleTooltip => IsOpen ? "Back to usage" : "Session logs";

    /// <summary>
    /// Shows or hides the log list for this harness.
    /// </summary>
    [RelayCommand]
    public void Toggle()
    {
        IsOpen = !IsOpen;
    }

    /// <summary>
    /// Replaces the row source from one collect and keeps the current page when it still exists.
    /// </summary>
    public void Update(TokenCostStatistics? statistics)
    {
        m_Entries = statistics?.SessionLogs ?? [];
        m_SpeedEstimated = statistics?.IsSpeedEstimated == true;
        int pages = PageCount;
        if (m_Page >= pages)
        {
            m_Page = pages - 1;
        }

        if (m_Page < 0)
        {
            m_Page = 0;
        }

        EmptyText = statistics == null ? "N/A" : "No session logs";
        Publish();
    }

    /// <summary>
    /// Moves to the previous page of 20 rows.
    /// </summary>
    [RelayCommand]
    public void PreviousPage()
    {
        if (m_Page <= 0)
        {
            return;
        }

        m_Page--;
        Publish();
    }

    /// <summary>
    /// Moves to the next page of 20 rows.
    /// </summary>
    [RelayCommand]
    public void NextPage()
    {
        if (!CanGoNext)
        {
            return;
        }

        m_Page++;
        Publish();
    }

    private int PageCount => Math.Max(1, (m_Entries.Count + PageSize - 1) / PageSize);

    private void Publish()
    {
        int start = m_Page * PageSize;
        int count = Math.Min(PageSize, Math.Max(0, m_Entries.Count - start));
        SessionLogRow[] rows = new SessionLogRow[count];
        for (int index = 0; index < count; index++)
        {
            rows[index] = CreateRow(m_Entries[start + index]);
        }

        Rows = rows;
        Summary = string.Create(CultureInfo.InvariantCulture, $"{m_Entries.Count} records");
        PageLabel = string.Create(CultureInfo.InvariantCulture, $"{m_Page + 1} / {PageCount}");
        CanGoPrevious = m_Page > 0;
        CanGoNext = m_Page + 1 < PageCount;
        IsEmpty = m_Entries.Count == 0;
    }

    private SessionLogRow CreateRow(SessionLogEntry entry)
    {
        DateTime local = entry.Timestamp.ToLocalTime().DateTime;
        string speed = FormatSpeed(entry);
        return new SessionLogRow
        {
            Time = local.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            TimeTooltip = local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            Model = entry.Model,
            Cost = FormatCost(entry),
            Speed = speed,
        };
    }

    private string FormatSpeed(SessionLogEntry entry)
    {
        if (entry.GetOutputTokensPerSecond() is not decimal speed)
        {
            return "—";
        }

        string prefix = m_SpeedEstimated ? "~" : string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"{prefix}{speed:0.0} tok/s");
    }

    private static string FormatCost(SessionLogEntry entry)
    {
        if (entry.CostUsd is not decimal cost)
        {
            return "—";
        }

        string text = cost.ToString("$0.0000", CultureInfo.InvariantCulture);
        return entry.HasUnpricedUsage ? text + "*" : text;
    }
}

/// <summary>
/// Display strings for one session-log row.
/// </summary>
public sealed class SessionLogRow
{
    public string Time { get; init; } = string.Empty;

    public string TimeTooltip { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public string Cost { get; init; } = string.Empty;

    public string Speed { get; init; } = string.Empty;
}

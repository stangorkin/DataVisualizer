namespace DataVizAgent.Models;

public sealed class ReportDocument
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; set; } = "Untitled Report";
    public string? DatasetName { get; set; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; private set; } = DateTimeOffset.UtcNow;
    public List<ReportPage> Pages { get; } = [];
    public Guid? ActivePageId { get; set; }

    public ReportPage GetOrCreateActivePage()
    {
        if (Pages.Count == 0)
        {
            var page = new ReportPage();
            Pages.Add(page);
            ActivePageId = page.Id;
            return page;
        }

        ReportPage? active = ActivePageId is null
            ? null
            : Pages.FirstOrDefault(p => p.Id == ActivePageId.Value);

        if (active is null)
        {
            active = Pages[0];
            ActivePageId = active.Id;
        }

        return active;
    }

    public ReportPage AddPage(string? title = null)
    {
        string resolvedTitle = string.IsNullOrWhiteSpace(title)
            ? $"Page {Pages.Count + 1}"
            : title.Trim();

        var page = new ReportPage { Title = resolvedTitle };
        Pages.Add(page);
        ActivePageId = page.Id;
        Touch();
        return page;
    }

    /// <summary>Removes a page, keeping at least one page in the report. Returns true when removed.</summary>
    public bool RemovePage(Guid pageId)
    {
        if (Pages.Count <= 1)
            return false;

        ReportPage? page = Pages.FirstOrDefault(p => p.Id == pageId);
        if (page is null)
            return false;

        int index = Pages.IndexOf(page);
        Pages.Remove(page);

        if (ActivePageId == pageId)
            ActivePageId = Pages[Math.Min(index, Pages.Count - 1)].Id;

        Touch();
        return true;
    }

    public bool SetActivePage(Guid pageId)
    {
        if (Pages.All(p => p.Id != pageId) || ActivePageId == pageId)
            return false;

        ActivePageId = pageId;
        return true;
    }

    /// <summary>Returns an existing page matching the name (case-insensitive) or creates a new one.</summary>
    public ReportPage GetOrCreatePageByName(string title)
    {
        ReportPage? existing = Pages.FirstOrDefault(p =>
            string.Equals(p.Title, title?.Trim(), StringComparison.OrdinalIgnoreCase));

        return existing ?? AddPage(title);
    }

    /// <summary>Moves the page to a new index, clamping to valid bounds. Returns true when the order changed.</summary>
    public bool MovePage(Guid pageId, int newIndex)
    {
        ReportPage? page = Pages.FirstOrDefault(p => p.Id == pageId);
        if (page is null)
            return false;

        int currentIndex = Pages.IndexOf(page);
        int targetIndex = Math.Clamp(newIndex, 0, Pages.Count - 1);
        if (currentIndex == targetIndex)
            return false;

        Pages.RemoveAt(currentIndex);
        Pages.Insert(targetIndex, page);
        Touch();
        return true;
    }

    public void Touch() => UpdatedUtc = DateTimeOffset.UtcNow;
}

public sealed class ReportPage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; set; } = "Page 1";
    public List<ChartVisual> Visuals { get; } = [];
}

public sealed class ChartVisual
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public ChartSpec Chart { get; private set; }
    public ReportVisualLayout Layout { get; set; }

    public ChartVisual(ChartSpec chart, ReportVisualLayout layout)
    {
        Chart = chart ?? throw new ArgumentNullException(nameof(chart));
        Layout = layout ?? throw new ArgumentNullException(nameof(layout));
    }

    public void UpdateChart(ChartSpec chart)
    {
        Chart = chart ?? throw new ArgumentNullException(nameof(chart));
    }
}

public sealed record ReportVisualLayout(double X, double Y, double Width, double Height)
{
    public static ReportVisualLayout Default { get; } = new(0, 0, 6, 5);
}
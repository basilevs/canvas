namespace Canvas.Dtos;

/// <summary>
/// Represents one chronological page of a board's stroke history, oldest-first,
/// together with the pagination totals needed to walk all pages.
/// </summary>
public sealed record HistoryPageResponse(
    int PageNumber,
    int TotalEvents,
    int TotalPages,
    IReadOnlyList<StrokeEventResponse> Events);

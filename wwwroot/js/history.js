// Shared history loader used by both the join flow (app.js) and the replay
// engine (replay.js). Auto-paginates the cacheable REST endpoint oldest-first
// until a 404 is hit. A 404 on page 1 means the board has no history.
export async function fetchAllHistory(boardName) {
  const events = [];
  let page = 1;

  for (;;) {
    const response = await fetch(`/api/boards/${encodeURIComponent(boardName)}/history/${page}`);
    if (response.status === 404) {
      break;
    }

    if (!response.ok) {
      throw new Error(`Failed to load history (page ${page}): ${response.status}`);
    }

    const body = await response.json();
    const pageEvents = body.events ?? body.Events ?? [];
    events.push(...pageEvents);

    const totalPages = body.totalPages ?? body.TotalPages ?? page;
    if (page >= totalPages) {
      break;
    }

    page += 1;
  }

  return events;
}

// Folds the append-only event log into the set of currently visible strokes,
// de-duplicated by stroke id: Add inserts, Remove deletes. Insertion order is
// preserved so strokes render in the order they were drawn.
export function foldEvents(events) {
  const strokes = new Map();

  for (const event of events) {
    const type = event.type ?? event.Type;
    const stroke = event.stroke ?? event.Stroke;
    if (!stroke) {
      continue;
    }

    const strokeId = stroke.id ?? stroke.Id;
    if (type === 'Remove') {
      strokes.delete(strokeId);
    } else {
      strokes.set(strokeId, stroke);
    }
  }

  return [...strokes.values()];
}

// The event-log timestamp of the last event, used as the inclusive `sinceTimestamp`
// for the atomic JoinBoard tail replay. Returns the Unix epoch when there is no history.
export function lastEventTimestamp(events) {
  if (events.length === 0) {
    return new Date(0).toISOString();
  }

  const last = events[events.length - 1];
  return last.timestamp ?? last.Timestamp ?? new Date(0).toISOString();
}

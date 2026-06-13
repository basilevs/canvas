---
goal: Keep large-history boards fast to load while preserving on-demand replay, via derived keyframes over the append-only event log
version: 1.0
date_created: 2026-06-13
last_updated: 2026-06-13
owner: basilevs
status: 'On Hold'
tags: [feature, history, replay, performance, compaction, keyframe]
---

# Introduction

![Status: On Hold](https://img.shields.io/badge/status-On_Hold-orange)

Keep boards with large drawing histories fast to open while still supporting full replay. The append-only event log introduced by [feature-replay-history-1.md](./feature-replay-history-1.md) grows without bound, so reconstructing or replaying a long-lived board by folding its entire history becomes increasingly slow. This plan introduces **history compaction**: a derived, periodically materialized **keyframe** (a checkpoint of board state at a point in time) that lets a client load current state and start drawing immediately — over the keyframe plus the small tail of subsequent events — without scanning the whole log.

The two goals are **quick load on large histories** and **replay on demand**, and they are *not* in conflict: drawing happens over the keyframe, while the complete history is retained and fetched **lazily or in the background** for replay. Compaction therefore adds derived keyframes and organizes the log for range fetching; it does **not** delete history that replay needs.

> **Scope: post-MVP enhancement.** This feature is intentionally excluded from the MVP ([feature-collaborative-whiteboard-1.md](./feature-collaborative-whiteboard-1.md)) and is only meaningful once the append-only event log exists. It depends on [feature-replay-history-1.md](./feature-replay-history-1.md) (the `StrokeEvent` log, `StrokeEventService`, and the `StrokeEvents` time-series collection) and directly addresses that plan's anticipated scaling concerns (its RISK-001 "future streaming/chunked loading" and RISK-003 "periodic snapshots for very large boards"). This plan deliberately specifies **requirements only**; the implementation task breakdown is intentionally deferred.

## 1. Requirements & Constraints

- **REQ-001**: Maintain a periodically materialized **keyframe** (checkpoint) capturing board state at a horizon timestamp, so a client can load current state and begin drawing immediately from the keyframe plus the bounded tail of subsequent events — without scanning or folding the entire event log
- **REQ-002**: Retain the **complete append-only history** in the event log; compaction never discards events required to replay any past moment. It adds derived keyframes and organizes the log for efficient access — it is a performance optimization, not a data-deletion mechanism
- **REQ-003**: Initial board load cost is **independent of total history length** — it is a function of the latest keyframe plus the post-keyframe tail, not of how many strokes the board has accumulated over its lifetime (goal 1)
- **REQ-004**: Replay reconstructs from `{nearest preceding keyframe + subsequent events}`; history older than the loaded keyframe is fetched **lazily or in the background** (e.g. when the user scrubs earlier in the timeline), never on the initial load path (goal 2)
- **REQ-005**: Live drawing operates over the keyframe — current board state is `keyframe + post-keyframe events`. New strokes continue to append to the log unchanged, and undo/`Remove` semantics ([feature-replay-history-1.md](./feature-replay-history-1.md), REQ-009) are unaffected
- **REQ-006**: Keyframes are **derived data**, fully reconstructable by folding the event log. Loss or corruption of a keyframe degrades only load performance, never correctness — current state remains recoverable from the live `Board.ActiveStrokes` snapshot and the complete history remains in the log
- **REQ-007**: A keyframe preserves per-stroke identity metadata (`UserId`, `Timestamp`, stroke `Id`) — it is a **set of strokes, not a flattened image** — so that owner visibility moderation / history cut-off filtering (HiddenRanges, [feature-visibility-moderation-1.md](./feature-visibility-moderation-1.md) / [feature-history-cutoff-moderation-1.md](./feature-history-cutoff-moderation-1.md)) still applies correctly to keyframed history at serve time
- **REQ-008**: Replay over a keyframed board is **visually equivalent** to replay over the full uncompacted log from the keyframe forward; older segments, once lazily fetched, replay identically — there is no fidelity loss for retained history
- **REQ-009**: When materializing a keyframe, compaction **may** collapse provably-redundant churn — an `Add` later negated by a `Remove`/undo, and exact duplicates — because these do not affect the reconstructed state; it must not remove any event still required to replay a retained earlier moment
- **REQ-010**: Keyframe placement segments the timeline so replay can **range/stream** bounded time windows rather than loading the whole log at once, supporting fast load and on-demand replay together
- **REQ-011**: Compaction is a **background maintenance operation** (scheduled and/or triggered by history size/age thresholds), never on the live draw, load, or replay request path, and must not stall drawing, undo, or replay
- **REQ-012**: Compaction is **idempotent and safe under concurrent stroke ingestion** — repeated runs, or a run while new events arrive, yield a consistent keyframe + log with no lost or duplicated strokes; the keyframe horizon lags live activity by a safe margin so it never collides with in-flight strokes or undo's reach
- **REQ-013**: Keyframe cadence and any segmentation/retention thresholds are **configurable** with sensible defaults
- **CON-001**: Backend is ASP.NET Core 10.0 with MongoDB Atlas
- **CON-002**: The event log lives in a **native MongoDB time-series collection** (`StrokeEvents`), which is append-only-oriented with limited in-place update/delete support and no unique indexes. Keyframes are periodically rewritten derived documents and so must be stored in a **separate regular collection** (e.g. `BoardKeyframes`), keyed by board + horizon timestamp; any optional pruning of collapsed churn must use operations the time-series collection actually supports
- **CON-003**: The live `Board.ActiveStrokes` snapshot remains the **authoritative current state**; keyframes are an additional, derived load/replay optimization and never replace it
- **CON-004**: Identity used for any access or moderation decision is **server-assigned**; client-supplied identity is never trusted (consistent with the parent plan's SEC-002)
- **DEP-001**: Depends on the append-only event log — `Models/StrokeEvent.cs`, `Services/StrokeEventService.cs`, and the `StrokeEvents` time-series collection — introduced by [feature-replay-history-1.md](./feature-replay-history-1.md)
- **DEP-002**: Coexists with the MVP live `Board.ActiveStrokes` snapshot and its `LastActivityAt` TTL; whole-board expiry is a distinct lifecycle concern, not history keyframing (see [feature-board-management-1.md](./feature-board-management-1.md))
- **DEP-003**: Must preserve the per-stroke `UserId`/`Timestamp` metadata that [feature-visibility-moderation-1.md](./feature-visibility-moderation-1.md) and [feature-history-cutoff-moderation-1.md](./feature-history-cutoff-moderation-1.md) filter on
- **PAT-001**: Snapshotting (log + checkpoint) — fast load from the latest checkpoint, with lazy/background hydration of the older log tail for replay

## 2. Implementation Steps

> Tasks are intentionally not yet defined for this plan — requirements only. The implementation phase breakdown will be authored separately once these requirements are reviewed.

## 3. Alternatives

- **ALT-001**: Reconstruct current state by folding the entire event log on every load — rejected; cost grows with total history, defeating goal 1. A keyframe makes load independent of history length (REQ-003)
- **ALT-002**: Lossy checkpoint that **deletes** pre-horizon events — rejected; it breaks replay-on-demand of older moments. History is retained and fetched lazily instead (REQ-002, REQ-004)
- **ALT-003**: Store keyframes inside the `StrokeEvents` time-series collection — rejected; time-series collections are append-only measurement stores ill-suited to a periodically rewritten derived document. Use a companion regular collection (CON-002)
- **ALT-004**: Eagerly load the full history in the background for every board open — rejected as the default; fetch lazily on replay demand to avoid wasted bandwidth/IO for the majority of opens that never replay (background prefetch may be an opt-in optimization)
- **ALT-005**: Treat the live `Board.ActiveStrokes` snapshot as the only keyframe — insufficient on its own; it captures current state but carries no timestamped temporal segmentation, so replay range-fetching (REQ-010) still needs explicit keyframes/segment markers
- **ALT-006**: Render keyframes as rasterized images for fast load — rejected for the same reasons as the snapshot-format decision ([feature-collaborative-whiteboard-1.md](./feature-collaborative-whiteboard-1.md), ALT-006): a keyframe must remain a per-caller-filterable set of vector strokes (REQ-007), not a flattened bitmap

## 4. Dependencies

- **DEP-001**: `Services/StrokeEventService.cs` + the `StrokeEvents` time-series collection (from [feature-replay-history-1.md](./feature-replay-history-1.md)) — the history this plan keyframes and range-serves
- **DEP-002**: `Models/StrokeEvent.cs` (from [feature-replay-history-1.md](./feature-replay-history-1.md)) — carries the embedded `Stroke` (`UserId`, `Timestamp`, `Id`) a keyframe must preserve
- **DEP-003**: `Models/Board.cs` / `Services/BoardService.cs` (from [feature-collaborative-whiteboard-1.md](./feature-collaborative-whiteboard-1.md)) — the authoritative `ActiveStrokes` snapshot and `LastActivityAt`, which a keyframe complements but never replaces
- **DEP-004**: HiddenRanges + ownership (from [feature-visibility-moderation-1.md](./feature-visibility-moderation-1.md) and [feature-history-cutoff-moderation-1.md](./feature-history-cutoff-moderation-1.md)) — serve-time filtering that must remain correct over keyframed history

## 5. Risks & Assumptions

- **RISK-001**: **Moderation interaction** — a keyframe that aggregates pre-horizon strokes must retain `UserId`/`Timestamp` per stroke or serve-time HiddenRange filtering over keyframed history breaks (REQ-007). This is the principal design tension to resolve during task planning
- **RISK-002**: **Time-series delete/update limitations** on the target Atlas version could constrain optional pruning of collapsed churn (REQ-009); the supported operations must be verified before committing to an approach
- **RISK-003**: **Concurrency at the keyframe horizon** — keyframing while strokes/undo arrive risks races and lost/duplicated strokes; the horizon must lag live activity by a safe margin (REQ-012)
- **RISK-004**: **Keyframe/log divergence** — a defect could make `keyframe + tail` disagree with the true folded state; mitigated by keyframes being fully reconstructable (REQ-006) and by periodic verification against a fold of the log
- **ASSUMPTION-001**: Boards exist that accumulate enough history for load cost to matter (long-lived, continuously edited public boards); small or short-lived boards may never trigger a keyframe and are handled by board TTL
- **ASSUMPTION-002**: Most board opens want current state quickly and only a subset trigger replay, which justifies lazy/background hydration of older history rather than eager full-history loading

## 6. Related Specifications / Further Reading

- [Replay & History View feature plan](./feature-replay-history-1.md) — introduces the append-only event log this plan keyframes; its RISK-001/RISK-003 anticipate this compaction work
- [Visibility moderation feature plan](./feature-visibility-moderation-1.md) and [History cut-off moderation feature plan](./feature-history-cutoff-moderation-1.md) — serve-time HiddenRange filtering that keyframes must preserve (REQ-007)
- [Main whiteboard implementation plan](./feature-collaborative-whiteboard-1.md) — the MVP whose `Board.ActiveStrokes` snapshot remains the authoritative current state; see its ALT-006 on why history artifacts stay vector, not raster
- [Board management feature plan](./feature-board-management-1.md) — owns whole-board lifecycle/expiry, which is distinct from history keyframing
- [MongoDB time-series collections](https://www.mongodb.com/docs/manual/core/timeseries-collections/)

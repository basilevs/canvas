---
goal: Implement abuse controls (IP-range and per-user rate limiting) for the collaborative whiteboard
version: 1.0
date_created: 2026-06-13
last_updated: 2026-06-13
owner: basilevs
status: 'On Hold'
tags: [feature, security, rate-limiting, abuse]
---

# Introduction

![Status: On Hold](https://img.shields.io/badge/status-On_Hold-orange)

Implement server-side abuse controls that protect the whiteboard from flooding and denial-of-service: configurable rate limiting keyed by client IP subnet and by authenticated user, applied across both the SignalR hub and the REST API.

> **Scope: post-MVP enhancement.** This feature is intentionally excluded from the MVP ([feature-collaborative-whiteboard-1.md](./feature-collaborative-whiteboard-1.md)). The MVP is a trusted-environment demo and does not enforce rate limiting; these controls are layered on before any untrusted/public exposure.

## 1. Requirements & Constraints

- **REQ-001**: Limit request volume per client IP range (using a /24 IPv4 subnet as the key) to bound damage from a single network source
- **REQ-002**: Limit request volume per authenticated user (anonymous pseudonymous identity) to bound damage from a single client
- **SEC-001**: Rate limiting must apply to both the SignalR hub negotiate/connection path and the REST API endpoints
- **SEC-002**: Rate-limit thresholds and windows must be configuration-driven (`appsettings.json`), not hard-coded
- **CON-001**: Use the built-in ASP.NET Core rate limiting middleware (`Microsoft.AspNetCore.RateLimiting`) — no third-party rate limiter
- **CON-002**: Rate limiting middleware ordering must be correct relative to authentication so the per-user key is available
- **DEP-001**: Depends on the anonymous identity middleware from the parent plan to provide the per-user key (`HttpContext.Items["UserId"]`)

## 2. Implementation Steps

### Implementation Phase 1

- GOAL-001: Implement rate-limit key providers and policies

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-001 | Create `Middleware/RateLimitKeyProviders.cs` — helpers to derive (a) an IP-range key from `HttpContext.Connection.RemoteIpAddress` masked to a /24 IPv4 subnet, and (b) a per-user key from `HttpContext.Items["UserId"]`. | | |
| TASK-002 | In `Program.cs`, register `AddRateLimiter` with two sliding-window policies: `"per-ip"` (keyed by the /24 subnet) and `"per-user"` (keyed by userId), both reading thresholds/windows from configuration. | | |
| TASK-003 | Add a global rejection response (`429 Too Many Requests`) with a `Retry-After` header. | | |

### Implementation Phase 2

- GOAL-002: Wire configuration and apply policies

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-004 | Add a `RateLimits` configuration section to `appsettings.json` (per-ip permit limit + window, per-user permit limit + window). | | |
| TASK-005 | Add `app.UseRateLimiter()` to the middleware pipeline in the correct order — after the anonymous identity middleware (so the per-user key is populated) and before endpoint execution. | | |
| TASK-006 | Apply the `"per-ip"` and `"per-user"` policies to the SignalR hub mapping and to the REST API endpoints (e.g. `.RequireRateLimiting(...)`). | | |

### Implementation Phase 3

- GOAL-003: Testing

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-007 | Add MSTest integration test `Tests/RateLimitingTests.cs` — exceeding the per-ip or per-user limit returns `429`; requests under the limit succeed; two clients in different /24 subnets are limited independently. | | |

## 3. Alternatives

- **ALT-001**: Enforce rate limiting in the MVP — rejected; the MVP targets a trusted environment, and rate limiting adds configuration and tuning overhead without value there.
- **ALT-002**: Per-exact-IP keying instead of /24 subnet — rejected; subnet keying better bounds a single network/NAT source while remaining simple.

## 4. Dependencies

- **DEP-001**: Parent plan anonymous identity middleware — provides `HttpContext.Items["UserId"]` for the per-user policy
- **DEP-002**: `Microsoft.AspNetCore.RateLimiting` (built into ASP.NET Core)

## 5. Files

- **FILE-001**: `Middleware/RateLimitKeyProviders.cs` — IP-subnet and per-user key derivation
- **FILE-002**: `Program.cs` — `AddRateLimiter` policies and `UseRateLimiter` ordering; apply policies to hub + API
- **FILE-003**: `appsettings.json` — `RateLimits` configuration section
- **FILE-004**: `Tests/RateLimitingTests.cs` — Rate limiting integration tests

## 6. Testing

- **TEST-001**: Integration test — Requests under the configured limit succeed; exceeding the per-ip limit returns `429` with `Retry-After`
- **TEST-002**: Integration test — Exceeding the per-user limit returns `429` independently of IP
- **TEST-003**: Integration test — Clients in different /24 subnets are rate-limited independently

## 7. Risks & Assumptions

- **RISK-001**: Misconfigured limits could throttle legitimate collaboration — mitigated by configuration-driven thresholds and integration tests
- **RISK-002**: `RemoteIpAddress` may be a proxy address behind a load balancer — requires `ForwardedHeaders` configuration in such deployments (out of scope here)
- **ASSUMPTION-001**: The anonymous identity middleware runs before rate limiting so the per-user key is available

## 8. Related Specifications / Further Reading

- [Main whiteboard implementation plan](./feature-collaborative-whiteboard-1.md) — parent plan (anonymous identity middleware, hub, API)
- [Rate limiting middleware in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit)

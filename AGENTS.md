# Iris — Agent Instructions

Read the backend spec before writing any code:
**`D:\Notes\Squad Vault\squad\qree\iris\docs\backend-api-spec.md`**

When in doubt, **read the existing code and match the patterns.**

## Rules

- Do not add NuGet packages without explicit instruction
- Do not add dependencies to `Iris.Domain` — it has zero NuGet refs
- Do not violate dependency direction: `Api → Application → Domain ← Infrastructure`
- Do not skip error handling
- Do not create projectors for audit-only events (`TurnFailed`, `TurnCancelled`)
- Do not modify existing tests unless the change requires it
- Use `DateTimeOffset` (UTC), never `DateTime`
- Enums serialize as **strings**, not integers
- `CancellationToken ct = default` on all async interface methods
- Async all the way down — no sync-over-async

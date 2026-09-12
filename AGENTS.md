# DMRoute-ng contributor instructions

## Performance contract

- Treat the UDP receive loop, DMR packet parsing, voice/data routing, ping handling, and SDS block ingestion as hot paths.
- After startup and warm-up, hot paths must allocate 0 managed bytes for packets handled within configured capacities.
- Use `ReadOnlySpan<T>` for borrowed input and `Span<T>` for caller-owned output. A span must never escape its synchronous call or cross an `await` boundary.
- Use `stackalloc` only for small, statically bounded buffers. Keep individual stack allocations at or below 512 bytes; use preallocated or pooled storage for larger or variable-size data.
- Do not use LINQ, closures, boxing, `params` logging calls, endpoint/string formatting, `ToArray`, collection growth, or array-returning packet builders in hot paths.
- Preallocate bounded dictionaries and buffers during construction. Check capacity before inserting so a hot-path operation cannot trigger a resize.
- Prefer synchronous span-based callbacks for packet data. A consumer that retains data must explicitly copy it into owned storage.
- Use source-generated `LoggerMessage` methods for logging reachable from hot paths. Rate-limit repeated operational warnings outside the receive loop.

## Verification

- Build and test through the project Makefile: `make test` (Release is the default configuration).
- Add warmed allocation regression tests using `GC.GetAllocatedBytesForCurrentThread()` for any changed hot path.
- Do not describe a path as zero-allocation unless an allocation test covers it.
- Preserve protocol byte layouts and add focused tests for packet writers and parsers.

## Working state

- Keep `HANDOFF.md` current while working. Record completed changes, validation results, known limitations, and the precise next step so another agent can continue without reconstructing context.
- Do not overwrite unrelated user changes in a dirty worktree.

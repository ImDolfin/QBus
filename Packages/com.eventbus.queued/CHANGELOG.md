# Changelog

## Unreleased

- Avoid CoreCLR cast-cache allocations during warmed scheduling by storing channel references in value-type scheduler entries.
- Add a shared README architecture visual and a frame-by-frame example of queued delivery.
- Capture immutable raise-time timestamps and per-bus publication sequences in `EventContext` for every queued event.
- Add optional context callbacks and generic category handlers while preserving payload-only subscriptions.
- Include context examples, cross-assembly Unity coverage, Play-session sequence reset checks, and warmed metadata allocation tests.
- Document all public C# declarations with XML summaries and applicable parameter, return, value, exception, and behavior details.
- Expand control-flow braces, separate logical blocks, and extract focused scheduling, routing, lifecycle, and validation helpers.
- Explain snapshot boundaries, ring-buffer storage, generic dispatch, and lifecycle ownership in code comments.
- Add shared EditorConfig conventions and enable missing public documentation diagnostics in development builds.

## 0.1.0

- Automatic concrete event queues and generated base/interface routing.
- Exact callbacks and category handlers that preserve the concrete generic event type.
- Snapshot-based queued delivery, per-channel FIFO, and retained ring-buffer capacity.
- Isolated manually pumped buses and an automatically managed Unity facade.
- Source-generator diagnostics, importable examples, and correctness/performance test tooling.

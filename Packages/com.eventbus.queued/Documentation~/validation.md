# Validation record

Validated on 2026-10-08 using Unity 2022.3.62f3 on Windows and .NET SDK 9.0.314 (.NET runtime 9.0.16).

The source audit covered 33 authored C# files and 209 public/interface declarations with no missing XML contracts or brace/body-layout violations. The standalone core and generator builds enable XML documentation diagnostics, and the repository EditorConfig specifies multiline braces and member bodies. PowerShell helpers use native comment-based help and multiline control flow. Publication context is documented with the same XML contracts as the existing API.

| Check | Result |
| --- | --- |
| Core correctness and allocation harness | 42 passed |
| Source generator and analyzer harness | 14 passed |
| Unity EditMode | 13 passed |
| Unity PlayMode, domain reload enabled | 12 passed |
| Unity PlayMode, domain reload disabled | 12 passed |
| .NET benchmark matrix, payload-only callbacks | 63 cases; correct delivery counts and zero warmed allocations in every case |
| .NET benchmark matrix, context callbacks | 63 cases; correct delivery counts and zero warmed allocations in every case |
| IL2CPP player | Not run: installation/toolchain prerequisites unavailable |

The Unity EditMode run includes two real Play-mode entry/exit cycles within one editor process with domain reload disabled. It checks retained routing metadata, replacement of the default bus, subscriber/reporter reset, stale-token safety, automatic delivery, publication sequences restarting at one, and removal of the PlayerLoop callback after each session.

Both Unity PlayMode configurations measured zero managed bytes during warmed exact, category, and combined struct publication/delivery, both for payload-only listeners and listeners consuming publication metadata. The context category allocation test reads an interface property through its constrained generic event parameter together with the sequence and timestamp. The automatic-delivery test checks shared contexts, cross-assembly category routing, mixed callback order, and sequences arriving out of publication order across channels. The throughput cases use 16-, 64-, and 256-byte payloads with both payload-only and context-aware category handlers. These measurements exclude initialization, capacity growth, subscription changes, error reporting, and application allocations.

Each standalone .NET benchmark matrix covers 1, 32, and 256 event types with 16-, 64-, and 256-byte payloads and seven listener configurations. The context matrix exercises exact and category subscriptions that consume metadata; the payload-only matrix uses the existing one-parameter callbacks. Every publication captures metadata in both matrices. The harness checks delivery counts as well as allocation totals. Its timings are .NET measurements and must not be treated as Unity player performance.

## Reproduction

The repository's `Tests/run-tests.ps1` provides `Core`, `Generator`, `EditMode`, `PlayMode`, `Benchmark`, `BenchmarkContext`, and `IL2CPP` modes. Use `-Mode PlayMode -NoDomainReload` for the second PlayMode configuration. Unity XML reports/logs and benchmark CSV are written to `artifacts/`; the two benchmark modes write `benchmarks.csv` and `benchmarks-context.csv` respectively. The package includes the Unity test assemblies; the repository includes a minimal test project and generator source.

## Remaining player validation

The Windows IL2CPP module installation was attempted through Unity CLI, but the installer stopped because the editor is under `C:\Program Files` and installation requires Windows administrator rights. Visual Studio C++ build tools and the Windows SDK were not found in their standard installation locations.

Install Windows Build Support (IL2CPP) for Unity 2022.3.62f3 and the required C++ toolchain, then run `Tests/run-tests.ps1 -Mode IL2CPP`. That mode selects IL2CPP and high managed stripping before building/running the test player. IL2CPP correctness, stripping behavior, allocation counts, and throughput remain unverified until that run succeeds. No universal player-performance claim is made.

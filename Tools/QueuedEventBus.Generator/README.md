# Source generator development

The package already includes `Runtime/Analyzers/QueuedEventBus.Generator.dll` and its Unity importer settings. Integrators do not need the .NET SDK or this project.

Build the bundled binary from the repository root:

```powershell
dotnet build Tools/QueuedEventBus.Generator/QueuedEventBus.Generator.csproj -c Release
dotnet run --project Tools/QueuedEventBus.Generator.Tests -c Release
```

The generator targets .NET Standard 2.0 and references Roslyn 3.8. Release builds copy only the generator DLL into the package; Roslyn assemblies are supplied by Unity's compiler and must not be bundled. The `.meta` file excludes the DLL from runtime platforms and labels it `RoslynAnalyzer`. Its position under the runtime asmdef makes the analyzer available to assemblies that reference the runtime package.

Every supported concrete event declared in an assembly receives routes to itself, its event base classes, and its distinct event interfaces. Generated direct generic calls retain the concrete payload type. Generated registration runs during `AfterAssembliesLoaded` and through `InitializeOnLoadMethod` in the editor. The runtime catalog makes repeated registration harmless. Registration methods carry `Preserve`; the generator adds `AlwaysLinkAssembly` unless the assembly already declares it.

Diagnostics:

| ID | Meaning | Action |
| --- | --- | --- |
| QEB001 | Unsupported concrete event declaration | Make the event and containing types public/internal and non-generic; use a regular struct or class instead of a ref struct. |
| QEB002 | Abstract/interface publication, prewarming, or exact subscription | Use a concrete event type, or use `IEventHandler<TCategory>` / `IEventHandlerWithContext<TCategory>` for a category subscription. |
| QEB003 | Statically visible derived event published as a base type | Let `Raise` or `RaiseImmediate` infer the concrete event type or specify it explicitly. |

The same concrete publication rules apply to `Raise` and `RaiseImmediate` on both `EventBus` and `Events`. Immediate dispatch reuses the generated routes, so events need no additional declarations or registration. The analyzer cannot prove a runtime object's exact type when a variable already has a concrete base type. Runtime validation rejects that case. Third-party DLLs must have been compiled with the generator to provide their own event metadata.

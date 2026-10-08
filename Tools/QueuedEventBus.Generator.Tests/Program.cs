using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using QueuedEventBus.Generator;

internal static class Program
{
    private static readonly MetadataReference[] References = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Where(path => Path.GetFileNameWithoutExtension(path) != "QueuedEventBus.Core")
        .Select(path => MetadataReference.CreateFromFile(path))
        .ToArray();

    private static readonly CSharpParseOptions ParseOptions = new CSharpParseOptions(
        LanguageVersion.CSharp9, preprocessorSymbols: new[] { "UNITY_EDITOR" });

    private static int Main()
    {
        var tests = new (string Name, Action Run)[]
        {
            ("Struct, class, interface, diamond, and nested routes compile", ValidRoutes),
            ("Unsupported declarations have actionable diagnostics", UnsupportedDeclarations),
            ("Inaccessible category routes have actionable diagnostics", InaccessibleCategories),
            ("Erased publications and invalid exact subscriptions are diagnosed", UsageDiagnostics),
            ("Category subscriptions and generic forwarding are accepted", ValidUsage),
            ("Metadata exact subscriptions reject erased event types", ContextUsageDiagnostics),
            ("Metadata category subscriptions and generic forwarding are accepted", ValidContextUsage),
            ("Separate assemblies retain referenced categories", CrossAssemblyRoutes),
            ("Existing AlwaysLinkAssembly attribute is retained once", ExistingAttribute),
            ("Partial declarations generate one registration", PartialDeclaration),
            ("256 event declarations generate valid output", ManyEvents),
            ("Generated registration executes against the actual runtime", RuntimeIntegration),
            ("Generated metadata delivery works across separate assemblies", CrossAssemblyContextIntegration),
            ("Source without runtime reference produces no output", NoRuntime)
        };

        int failures = 0;
        foreach (var (name, test) in tests)
        {
            if (!RunTest(name, test))
            {
                failures++;
            }
        }

        Console.WriteLine($"{tests.Length - failures}/{tests.Length} generator tests passed.");
        return failures == 0 ? 0 : 1;
    }

    private static bool RunTest(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine("PASS " + name);
            return true;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL " + name + "\n" + exception);
            return false;
        }
    }

    #region Compilation and assertion helpers

    private static CSharpCompilation Compile(
        string source,
        string name = "TestEvents",
        bool contract = true,
        params MetadataReference[] extra)
    {
        var trees = new List<SyntaxTree>();
        if (contract)
        {
            trees.Add(CSharpSyntaxTree.ParseText(GeneratorFixtures.Contract, ParseOptions));
        }

        trees.Add(CSharpSyntaxTree.ParseText(source, ParseOptions));
        return CSharpCompilation.Create(
            name,
            trees,
            References.Concat(extra),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
    }

    private static (Compilation Output, string Source, ImmutableArray<Diagnostic> Diagnostics) Generate(CSharpCompilation input)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { new EventGenerator() }, parseOptions: ParseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(input, out var output, out var diagnostics);

        string source = string.Join("\n", driver.GetRunResult().GeneratedTrees.Select(tree => tree.GetText().ToString()));
        return (output, source, diagnostics);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertCompiles(Compilation output)
    {
        var errors = output.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        Assert(errors.Length == 0, string.Join("\n", errors.Select(diagnostic => diagnostic.ToString())));

        using (var image = new MemoryStream())
        {
            var emitted = output.Emit(image);
            Assert(emitted.Success, string.Join("\n", emitted.Diagnostics));
        }
    }

    private static ImmutableArray<Diagnostic> Analyze(string source)
    {
        var input = Compile(source);
        AssertCompiles(input);

        return input.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EventUsageAnalyzer()))
            .GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult();
    }

    private static int Count(string value, string needle)
    {
        return (value.Length - value.Replace(needle, "").Length) / needle.Length;
    }

    #endregion

    #region Generator and analyzer scenarios

    private static void ValidRoutes()
    {
        var result = Generate(Compile(GeneratorFixtures.ValidRoutes));
        AssertCompiles(result.Output);
        Assert(result.Diagnostics.Length == 0, "Unexpected generator diagnostics");

        Assert(Count(result.Source, "Create<global::Damage, global::ICommon>()") == 1, "Diamond route must appear once");
        Assert(result.Source.Contains("RegisterStruct<global::Damage>"), "Struct registration missing");
        Assert(result.Source.Contains("RegisterClass<global::BaseEvent>"), "Concrete base registration missing");
        Assert(result.Source.Contains("Create<global::Child, global::AbstractEvent>()"), "Abstract event category missing");
        Assert(!result.Source.Contains("RegisterClass<global::AbstractEvent>"), "Abstract event must not own a queue");
        Assert(result.Source.Contains("Create<global::Damage, global::QueuedEventBus.IEvent>()"), "Root category missing");
        Assert(result.Source.Contains("global::Container.Nested"), "Internal nested event missing");

        Assert(result.Source.Contains("AfterAssembliesLoaded"), "Runtime startup hook missing");
        Assert(result.Source.Contains("InitializeOnLoadMethodAttribute"), "Editor startup hook missing");
        Assert(result.Source.Contains("PreserveAttribute"), "Linker preservation missing");
    }

    private static void UnsupportedDeclarations()
    {
        var result = Generate(Compile(GeneratorFixtures.UnsupportedDeclarations));
        Assert(result.Diagnostics.Count(diagnostic => diagnostic.Id == "QEB001") == 6,
            "Expected six unsupported declaration diagnostics: " + string.Join("\n", result.Diagnostics));
        Assert(result.Source.Length == 0, "Unsupported events must not generate broken routes");
    }

    private static void InaccessibleCategories()
    {
        var input = Compile(GeneratorFixtures.InaccessibleCategories);
        // These declarations are legal, but generated top-level code cannot name their categories.
        AssertCompiles(input);

        var result = Generate(input);
        Assert(result.Diagnostics.Count(diagnostic => diagnostic.Id == "QEB001") == 2,
            "Expected inaccessible route diagnostics: " + string.Join("\n", result.Diagnostics));
        Assert(result.Source.Length == 0, "Inaccessible categories must not generate broken routes");
    }

    private static void UsageDiagnostics()
    {
        var diagnostics = Analyze(GeneratorFixtures.InvalidUsage);
        Assert(diagnostics.Count(diagnostic => diagnostic.Id == "QEB002") == 4,
            "Expected four concrete-type diagnostics: " + string.Join("\n", diagnostics));
        Assert(diagnostics.Count(diagnostic => diagnostic.Id == "QEB003") == 2,
            "Expected two erased-publication diagnostics: " + string.Join("\n", diagnostics));
    }

    private static void ValidUsage()
    {
        var diagnostics = Analyze(GeneratorFixtures.ValidUsage);
        Assert(diagnostics.Length == 0, string.Join("\n", diagnostics));
    }

    private static void ContextUsageDiagnostics()
    {
        var diagnostics = Analyze(MetadataFixtures.InvalidUsage);
        Assert(diagnostics.Length == 4 && diagnostics.All(diagnostic => diagnostic.Id == "QEB002"),
            "Expected four concrete-type diagnostics for metadata callbacks: " + string.Join("\n", diagnostics));
        Assert(diagnostics.All(diagnostic => diagnostic.GetMessage().Contains("IEventHandlerWithContext")),
            "Metadata diagnostics should recommend the category handler overload");
    }

    private static void ValidContextUsage()
    {
        var diagnostics = Analyze(MetadataFixtures.ValidUsage);
        Assert(diagnostics.Length == 0, string.Join("\n", diagnostics));
    }

    private static void CrossAssemblyRoutes()
    {
        var baseAssembly = Compile("""
            namespace Shared
            {
                /// <summary>Provides a category from another assembly.</summary>
                public interface ICategory : QueuedEventBus.IEvent
                {
                }

                /// <summary>Provides a base class from another assembly.</summary>
                public abstract class Base : ICategory
                {
                }
            }
            """, "SharedEvents");

        using (var stream = new MemoryStream())
        {
            Assert(baseAssembly.Emit(stream).Success, "Shared contract must compile");
            var reference = MetadataReference.CreateFromImage(stream.ToArray());
            var result = Generate(Compile("""
                /// <summary>Inherits its categories from the referenced assembly.</summary>
                public sealed class LocalEvent : Shared.Base
                {
                }
                """, "Consumer", false, reference));

            AssertCompiles(result.Output);
            Assert(result.Source.Contains("Create<global::LocalEvent, global::Shared.ICategory>()"),
                "Cross-assembly interface route missing");
            Assert(result.Source.Contains("Create<global::LocalEvent, global::Shared.Base>()"),
                "Cross-assembly base route missing");
        }
    }

    private static void ExistingAttribute()
    {
        var result = Generate(Compile("""
            [assembly: UnityEngine.Scripting.AlwaysLinkAssembly]

            /// <summary>Belongs to an assembly already marked for preservation.</summary>
            public struct Ping : QueuedEventBus.IEvent
            {
            }
            """));

        AssertCompiles(result.Output);
        Assert(!result.Source.Contains("[assembly:"), "Duplicate assembly preservation attribute");
    }

    private static void PartialDeclaration()
    {
        var result = Generate(Compile("""
            /// <summary>Exercises deduplication of partial declarations.</summary>
            public partial class Ping : QueuedEventBus.IEvent
            {
            }

            public partial class Ping : QueuedEventBus.IEvent
            {
            }
            """));

        AssertCompiles(result.Output);
        Assert(Count(result.Source, "RegisterClass<global::Ping>") == 1, "Partial event registered twice");
    }

    private static void ManyEvents()
    {
        var declarations = string.Join("\n", Enumerable.Range(0, 256).Select(index => $$"""
            /// <summary>Provides event {{index}} for the large registration fixture.</summary>
            public struct Event{{index}} : QueuedEventBus.IEvent
            {
            }
            """));
        var result = Generate(Compile(declarations));

        AssertCompiles(result.Output);
        Assert(Count(result.Source, "GeneratedEventCatalog.RegisterStruct<") == 256, "Missing event registrations");
    }

    private static void NoRuntime()
    {
        var result = Generate(Compile("""
            /// <summary>Is unrelated to the event bus.</summary>
            public struct NoEvent
            {
            }
            """, contract: false));

        AssertCompiles(result.Output);
        Assert(result.Source.Length == 0 && result.Diagnostics.Length == 0,
            "Generator should be dormant without the runtime reference");
    }

    private static void RuntimeIntegration()
    {
        var core = MetadataReference.CreateFromFile(typeof(QueuedEventBus.EventBus).Assembly.Location);
        var result = Generate(Compile(GeneratorFixtures.RuntimeIntegration, "RuntimeGeneratedIntegration", false, core));
        Assert(result.Diagnostics.Length == 0, string.Join("\n", result.Diagnostics));

        var assembly = EmitAndLoad(result.Output);
        InvokeRegistration(assembly);
        InvokeRegistration(assembly); // Repeated editor/play registration must remain harmless.
        assembly.GetType("Integration")!.GetMethod("Run")!.Invoke(null, null);
    }

    private static Assembly EmitAndLoad(Compilation compilation)
    {
        return LoadImage(EmitImage(compilation));
    }

    private static void CrossAssemblyContextIntegration()
    {
        var core = MetadataReference.CreateFromFile(typeof(QueuedEventBus.EventBus).Assembly.Location);
        var shared = Compile(MetadataFixtures.Shared, "MetadataSharedIntegration", false, core);
        byte[] sharedImage = EmitImage(shared);
        var sharedReference = MetadataReference.CreateFromImage(sharedImage);
        LoadImage(sharedImage);

        var events = Generate(Compile(MetadataFixtures.Events, "MetadataEventsIntegration", false, core, sharedReference));
        Assert(events.Diagnostics.Length == 0, string.Join("\n", events.Diagnostics));
        byte[] eventsImage = EmitImage(events.Output);
        var eventsReference = MetadataReference.CreateFromImage(eventsImage);
        var eventsAssembly = LoadImage(eventsImage);
        InvokeRegistration(eventsAssembly);

        var consumer = Compile(MetadataFixtures.Consumer, "MetadataConsumerIntegration", false,
            core, sharedReference, eventsReference);
        var consumerAssembly = EmitAndLoad(consumer);
        consumerAssembly.GetType("MetadataIntegration")!.GetMethod("Run")!.Invoke(null, null);
    }

    private static byte[] EmitImage(Compilation compilation)
    {
        using (var stream = new MemoryStream())
        {
            var emit = compilation.Emit(stream);
            Assert(emit.Success, string.Join("\n", emit.Diagnostics));
            return stream.ToArray();
        }
    }

    private static Assembly LoadImage(byte[] image)
    {
        using (var stream = new MemoryStream(image))
        {
            return AssemblyLoadContext.Default.LoadFromStream(stream);
        }
    }

    private static void InvokeRegistration(Assembly assembly)
    {
        var registrar = assembly.GetTypes()
            .Single(type => type.Name.StartsWith("EventRegistration_", StringComparison.Ordinal));
        var register = registrar.GetMethod("Register", BindingFlags.NonPublic | BindingFlags.Static)!;
        register.Invoke(null, null);
    }

    #endregion
}

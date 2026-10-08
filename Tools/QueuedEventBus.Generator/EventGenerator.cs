using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace QueuedEventBus.Generator
{
    /// <summary>
    /// Generates per-assembly registration of concrete events and their strongly typed category routes.
    /// </summary>
    /// <remarks>
    /// Supports accessible, non-generic event classes and structs, including nested declarations.
    /// Emits Unity startup and linker-preservation attributes when the corresponding Unity APIs exist.
    /// Unsupported concrete event declarations are reported as QEB001 errors.
    /// </remarks>
    [Generator]
    public sealed class EventGenerator : ISourceGenerator
    {
        private static readonly DiagnosticDescriptor UnsupportedEvent = new DiagnosticDescriptor(
            "QEB001", "Event cannot have generated routing",
            "Event '{0}' cannot have generated routing: {1}. Use a public or internal, non-generic class or struct in a non-generic containing type.",
            "QueuedEventBus", DiagnosticSeverity.Error, true);

        /// <summary>
        /// Registers the syntax receiver that collects candidate event declarations.
        /// </summary>
        /// <param name="context">The Roslyn initialization context for this generator.</param>
        public void Initialize(GeneratorInitializationContext context)
        {
            context.RegisterForSyntaxNotifications(() => new EventDeclarations());
        }

        /// <summary>
        /// Validates event declarations and emits registration code for the current compilation.
        /// </summary>
        /// <param name="context">The compilation context used to resolve symbols, report diagnostics, and add source.</param>
        /// <remarks>No source is emitted when the event marker is unavailable or no supported events are declared.</remarks>
        /// <exception cref="OperationCanceledException">The compilation cancellation token was canceled.</exception>
        public void Execute(GeneratorExecutionContext context)
        {
            var marker = context.Compilation.GetTypeByMetadataName(EventSymbols.MarkerName);
            if (marker == null || !(context.SyntaxReceiver is EventDeclarations receiver))
            {
                return;
            }

            var events = DiscoverEvents(context, receiver, marker);
            if (events.Count == 0)
            {
                return;
            }

            events.Sort((left, right) => StringComparer.Ordinal.Compare(EventSymbols.Name(left), EventSymbols.Name(right)));

            var source = EmitRegistration(context.Compilation, events, marker);
            context.AddSource("QueuedEventBus.Registration.g.cs", SourceText.From(source, Encoding.UTF8));
        }

        #region Event discovery and validation

        private static List<INamedTypeSymbol> DiscoverEvents(
            GeneratorExecutionContext context,
            EventDeclarations receiver,
            INamedTypeSymbol marker)
        {
            var events = new List<INamedTypeSymbol>();
#pragma warning disable RS1024 // Roslyn 3.3.2 incorrectly flags this explicit symbol comparer.
            var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
#pragma warning restore RS1024

            foreach (var declaration in receiver.Declarations)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var model = context.Compilation.GetSemanticModel(declaration.SyntaxTree);
                var type = model.GetDeclaredSymbol(declaration, context.CancellationToken) as INamedTypeSymbol;
                if (type == null || type.IsAbstract || type.TypeKind == TypeKind.Interface ||
                    !EventSymbols.IsEvent(type, marker) || !seen.Add(type))
                {
                    continue;
                }

                var reason = GetUnsupportedReason(type, marker, context.Compilation);
                if (reason != null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        UnsupportedEvent, declaration.Identifier.GetLocation(), type.Name, reason));
                    continue;
                }

                events.Add(type);
            }

            return events;
        }

        private static string? GetUnsupportedReason(
            INamedTypeSymbol type,
            INamedTypeSymbol marker,
            Compilation compilation)
        {
            if (EventSymbols.HasTypeParameters(type))
            {
                return "generic event declarations or containing types are not supported";
            }

            if (type.IsRefLikeType)
            {
                return "ref-like events cannot be stored in a queue";
            }

            if (!EventSymbols.IsAccessible(type, compilation))
            {
                return "the generated assembly registrar cannot access the declaration";
            }

            var hiddenCategory = Categories(type, marker)
                .FirstOrDefault(category => !EventSymbols.IsAccessible(category, compilation));
            if (hiddenCategory != null)
            {
                return "the generated assembly registrar cannot access category '" + hiddenCategory.ToDisplayString() + "'";
            }

            return null;
        }

        private static List<INamedTypeSymbol> Categories(INamedTypeSymbol type, INamedTypeSymbol marker)
        {
            var result = new List<INamedTypeSymbol> { type };
#pragma warning disable RS1024 // Roslyn 3.3.2 incorrectly flags this explicit symbol comparer.
            var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default) { type };
#pragma warning restore RS1024

            for (var parent = type.BaseType; parent != null; parent = parent.BaseType)
            {
                if (EventSymbols.IsEvent(parent, marker) && seen.Add(parent))
                {
                    result.Add(parent);
                }
            }

            // Symbol identity collapses diamond inheritance; sorting stabilizes generated output.
            foreach (var iface in type.AllInterfaces.OrderBy(EventSymbols.Name, StringComparer.Ordinal))
            {
                if (EventSymbols.IsEvent(iface, marker) && seen.Add(iface))
                {
                    result.Add(iface);
                }
            }

            return result;
        }

        #endregion

        #region Registration source emission

        private static string EmitRegistration(
            Compilation compilation,
            IReadOnlyList<INamedTypeSymbol> events,
            INamedTypeSymbol marker)
        {
            var source = new StringBuilder("// <auto-generated/>\n#nullable disable\n");
            bool preserve = compilation.GetTypeByMetadataName("UnityEngine.Scripting.PreserveAttribute") != null;
            EmitAssemblyPreservation(source, compilation);

            source.AppendLine("namespace QueuedEventBus.Generated");
            source.AppendLine("{");
            AppendPreservationAttribute(source, preserve, "    ");

            // The suffix avoids ambiguous registrar names when multiple event assemblies are loaded.
            source.Append("    internal static class EventRegistration_")
                .Append(StableSuffix(compilation.AssemblyName ?? "Assembly")).AppendLine();
            source.AppendLine("    {");
            EmitRegistrationEntryPoint(source, compilation, events.Count, preserve);

            for (int i = 0; i < events.Count; i++)
            {
                source.AppendLine();
                EmitEventRegistration(source, events[i], marker, i, preserve);
            }

            source.AppendLine("    }");
            source.AppendLine("}");
            return source.ToString();
        }

        private static void EmitAssemblyPreservation(StringBuilder source, Compilation compilation)
        {
            var alwaysLink = compilation.GetTypeByMetadataName("UnityEngine.Scripting.AlwaysLinkAssemblyAttribute");
            if (alwaysLink == null || compilation.Assembly.GetAttributes()
                .Any(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, alwaysLink)))
            {
                return;
            }

            // Unity must examine this assembly for startup hooks even when no event is referenced directly.
            source.AppendLine("[assembly: global::UnityEngine.Scripting.AlwaysLinkAssemblyAttribute]");
            source.AppendLine();
        }

        private static void EmitRegistrationEntryPoint(
            StringBuilder source,
            Compilation compilation,
            int eventCount,
            bool preserve)
        {
            AppendPreservationAttribute(source, preserve, "        ");
            if (compilation.GetTypeByMetadataName("UnityEngine.RuntimeInitializeOnLoadMethodAttribute") != null)
            {
                source.AppendLine("        [global::UnityEngine.RuntimeInitializeOnLoadMethodAttribute(global::UnityEngine.RuntimeInitializeLoadType.AfterAssembliesLoaded)]");
            }

            if (compilation.GetTypeByMetadataName("UnityEditor.InitializeOnLoadMethodAttribute") != null)
            {
                source.AppendLine("#if UNITY_EDITOR");
                source.AppendLine("        [global::UnityEditor.InitializeOnLoadMethodAttribute]");
                source.AppendLine("#endif");
            }

            source.AppendLine("        internal static void Register()");
            source.AppendLine("        {");
            for (int i = 0; i < eventCount; i++)
            {
                source.Append("            Register").Append(i).AppendLine("();");
            }

            source.AppendLine("        }");
        }

        private static void EmitEventRegistration(
            StringBuilder source,
            INamedTypeSymbol type,
            INamedTypeSymbol marker,
            int index,
            bool preserve)
        {
            var typeName = EventSymbols.Name(type);
            AppendPreservationAttribute(source, preserve, "        ");
            source.Append("        private static void Register").Append(index).AppendLine("()");
            source.AppendLine("        {");
            source.Append("            global::QueuedEventBus.Generated.GeneratedEventCatalog.Register")
                .Append(type.IsValueType ? "Struct" : "Class").Append('<').Append(typeName).AppendLine(">(");

            // Explicit closed generic calls give IL2CPP the concrete event/category pairs to preserve.
            var categories = Categories(type, marker);
            for (int route = 0; route < categories.Count; route++)
            {
                source.Append("                global::QueuedEventBus.Generated.GeneratedRoutes.Create<")
                    .Append(typeName).Append(", ").Append(EventSymbols.Name(categories[route])).Append(">()")
                    .AppendLine(route == categories.Count - 1 ? ");" : ",");
            }

            source.AppendLine("        }");
        }

        private static void AppendPreservationAttribute(StringBuilder source, bool preserve, string indentation)
        {
            if (preserve)
            {
                source.Append(indentation).AppendLine("[global::UnityEngine.Scripting.PreserveAttribute]");
            }
        }

        private static string StableSuffix(string assemblyName)
        {
            // FNV-1a is deterministic across processes, unlike the platform string hash.
            uint hash = 2166136261;
            foreach (char character in assemblyName)
            {
                hash = unchecked((hash ^ character) * 16777619);
            }

            return hash.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
        }

        #endregion

        private sealed class EventDeclarations : ISyntaxReceiver
        {
            internal readonly List<TypeDeclarationSyntax> Declarations = new List<TypeDeclarationSyntax>();

            /// <inheritdoc />
            public void OnVisitSyntaxNode(SyntaxNode syntaxNode)
            {
                if (syntaxNode is TypeDeclarationSyntax declaration && declaration.BaseList != null)
                {
                    Declarations.Add(declaration);
                }
            }
        }
    }
}

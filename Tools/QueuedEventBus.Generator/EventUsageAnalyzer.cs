using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace QueuedEventBus.Generator
{
    /// <summary>
    /// Reports event bus calls that require a concrete event type or erase a known publication type.
    /// </summary>
    /// <remarks>
    /// Category-handler subscriptions are valid for base types and interfaces. Generic forwarding
    /// is validated at concrete call sites and by the runtime publication checks.
    /// </remarks>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class EventUsageAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor ConcreteRequired = new DiagnosticDescriptor(
            "QEB002", "Concrete event type required",
            "'{0}' requires a concrete event type; '{1}' is abstract or interface-typed. Publish the concrete event or use IEventHandler<TCategory> or IEventHandlerWithContext<TCategory> for category subscriptions.",
            "QueuedEventBus", DiagnosticSeverity.Error, true);

        private static readonly DiagnosticDescriptor ErasedPublication = new DiagnosticDescriptor(
            "QEB003", "Publication erases the concrete event type",
            "{0}<{1}> receives '{2}'; publish using the concrete event type so generated routing stays correct",
            "QueuedEventBus", DiagnosticSeverity.Error, true);

        /// <summary>
        /// Gets the concrete-type and erased-publication diagnostics reported by this analyzer.
        /// </summary>
        /// <value>The QEB002 and QEB003 diagnostic descriptors.</value>
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        {
            get
            {
                return ImmutableArray.Create(ConcreteRequired, ErasedPublication);
            }
        }

        /// <summary>
        /// Registers concurrent analysis of event bus invocations in user-written source.
        /// </summary>
        /// <param name="context">The Roslyn context used to configure and register analysis actions.</param>
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        }

        private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;
            var method = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol as IMethodSymbol;
            if (method == null || !RequiresConcreteType(method))
            {
                return;
            }

            var type = method.TypeArguments[0];
            if (type is ITypeParameterSymbol)
            {
                // Generic forwarding cannot be resolved here; retain the concrete call-site checks.
                return;
            }

            if (type.TypeKind == TypeKind.Interface || (type is INamedTypeSymbol named && named.IsAbstract))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    ConcreteRequired, invocation.GetLocation(), method.Name, type.ToDisplayString()));
                return;
            }

            AnalyzePublication(context, invocation, method, type);
        }

        private static bool RequiresConcreteType(IMethodSymbol method)
        {
            if (method.TypeArguments.Length != 1 ||
                method.ContainingNamespace.ToDisplayString() != "QueuedEventBus" ||
                (method.ContainingType.Name != "EventBus" && method.ContainingType.Name != "Events"))
            {
                return false;
            }

            if (IsPublication(method) || method.Name == "Prewarm")
            {
                return true;
            }

            // Both callback delegates are exact-only; both handler interfaces accept categories.
            return method.Name == "Subscribe" &&
                method.Parameters.Length == 1 &&
                method.Parameters[0].Type.TypeKind == TypeKind.Delegate;
        }

        private static void AnalyzePublication(
            SyntaxNodeAnalysisContext context,
            InvocationExpressionSyntax invocation,
            IMethodSymbol method,
            ITypeSymbol publishedType)
        {
            if (!IsPublication(method) || invocation.ArgumentList.Arguments.Count != 1)
            {
                return;
            }

            var argument = UnwrapPublicationArgument(invocation.ArgumentList.Arguments[0].Expression);
            var actualType = context.SemanticModel.GetTypeInfo(argument, context.CancellationToken).Type;
            if (actualType != null &&
                actualType.TypeKind == TypeKind.Class &&
                !SymbolEqualityComparer.Default.Equals(publishedType, actualType) &&
                context.Compilation.ClassifyConversion(actualType, publishedType).IsImplicit)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    ErasedPublication, invocation.GetLocation(), method.Name,
                    publishedType.ToDisplayString(), actualType.ToDisplayString()));
            }
        }

        private static bool IsPublication(IMethodSymbol method)
        {
            return method.Name == "Raise" || method.Name == "RaiseImmediate";
        }

        private static ExpressionSyntax UnwrapPublicationArgument(ExpressionSyntax argument)
        {
            while (argument is ParenthesizedExpressionSyntax parenthesized)
            {
                argument = parenthesized.Expression;
            }

            // An explicit upcast does not make the underlying class instance safe to publish.
            while (argument is CastExpressionSyntax cast)
            {
                argument = cast.Expression;
            }

            return argument;
        }
    }
}

using Microsoft.CodeAnalysis;

namespace QueuedEventBus.Generator
{
    internal static class EventSymbols
    {
        internal const string MarkerName = "QueuedEventBus.IEvent";

        internal static bool IsEvent(ITypeSymbol type, INamedTypeSymbol marker)
        {
            if (SymbolEqualityComparer.Default.Equals(type, marker))
            {
                return true;
            }

            foreach (var iface in type.AllInterfaces)
            {
                if (SymbolEqualityComparer.Default.Equals(iface, marker))
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool IsAccessible(ITypeSymbol type, Compilation compilation)
        {
            if (type is IArrayTypeSymbol array)
            {
                return IsAccessible(array.ElementType, compilation);
            }

            if (!(type is INamedTypeSymbol named))
            {
                return true;
            }

            if (!compilation.IsSymbolAccessibleWithin(named, compilation.Assembly))
            {
                return false;
            }

            // A visible constructed category can still contain a private generic argument.
            // Check containing types too, because their arguments appear in the generated name.
            for (var current = named; current != null; current = current.ContainingType)
            {
                foreach (var argument in current.TypeArguments)
                {
                    if (!IsAccessible(argument, compilation))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        internal static bool HasTypeParameters(INamedTypeSymbol type)
        {
            for (var current = type; current != null; current = current.ContainingType)
            {
                if (current.Arity != 0)
                {
                    return true;
                }
            }

            return false;
        }

        internal static string Name(ITypeSymbol symbol)
        {
            return symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }
    }
}

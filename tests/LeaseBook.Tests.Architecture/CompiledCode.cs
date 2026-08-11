using Mono.Cecil;
using ReflectionAssembly = System.Reflection.Assembly;

namespace LeaseBook.Tests.Architecture;

/// <summary>
/// Reads observable references from compiled method bodies. This is the architecture-test seam for
/// questions reflection cannot answer from type declarations alone: direct method calls, member
/// references, and string-backed lookups. It naturally ignores comments and formatting and follows
/// async/lambda compiler-generated methods because those are ordinary methods in the assembly.
/// </summary>
internal static class CompiledCode
{
    public static IReadOnlyList<CompiledReference> In(IEnumerable<ReflectionAssembly> assemblies) =>
        [.. assemblies.SelectMany(ReadAssembly)];

    public static IReadOnlyList<CompiledReference> In(params ReflectionAssembly[] assemblies) =>
        In((IEnumerable<ReflectionAssembly>)assemblies);

    public static IReadOnlyList<CompiledReference> In(Type rootType)
    {
        var rootName = (rootType.FullName
                ?? throw new InvalidOperationException($"{rootType.Name} has no full name."))
            .Replace('+', '/');

        return
        [
            .. ReadAssembly(rootType.Assembly)
                .Where(reference =>
                    reference.CallerType == rootName ||
                    reference.CallerType.StartsWith(rootName + "/", StringComparison.Ordinal)),
        ];
    }

    private static IEnumerable<CompiledReference> ReadAssembly(ReflectionAssembly assembly)
    {
        if (string.IsNullOrWhiteSpace(assembly.Location))
        {
            throw new InvalidOperationException(
                $"Cannot inspect {assembly.GetName().Name}: the loaded assembly has no file location.");
        }

        using var definition = AssemblyDefinition.ReadAssembly(
            assembly.Location,
            new ReaderParameters { InMemory = true, ReadSymbols = false });

        foreach (var type in definition.Modules.SelectMany(module => Descendants(module.Types)))
        {
            foreach (var method in type.Methods.Where(method => method.HasBody))
            {
                foreach (var instruction in method.Body.Instructions)
                {
                    var (kind, target) = instruction.Operand switch
                    {
                        MethodReference reference => (CompiledReferenceKind.Method, reference.FullName),
                        FieldReference reference => (CompiledReferenceKind.Field, reference.FullName),
                        TypeReference reference => (CompiledReferenceKind.Type, reference.FullName),
                        string literal => (CompiledReferenceKind.String, literal),
                        _ => (CompiledReferenceKind.None, null),
                    };

                    if (target is not null)
                    {
                        yield return new CompiledReference(type.FullName, method.FullName, kind, target);
                    }
                }
            }
        }
    }

    private static IEnumerable<TypeDefinition> Descendants(IEnumerable<TypeDefinition> roots)
    {
        foreach (var type in roots)
        {
            yield return type;

            foreach (var nested in Descendants(type.NestedTypes))
            {
                yield return nested;
            }
        }
    }
}

internal enum CompiledReferenceKind
{
    None,
    Method,
    Field,
    Type,
    String,
}

internal sealed record CompiledReference(
    string CallerType,
    string CallerMethod,
    CompiledReferenceKind Kind,
    string Target)
{
    public override string ToString() => $"{CallerMethod} -> {Kind}: {Target}";
}

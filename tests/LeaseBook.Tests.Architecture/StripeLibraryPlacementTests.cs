using Mono.Cecil;
using Mono.Cecil.Cil;
using Shouldly;
using ReflectionAssembly = System.Reflection.Assembly;

namespace LeaseBook.Tests.Architecture;

/// <summary>
/// The Stripe library is the one thing in the application that can ask a processor to move money, so
/// where it can be reached from is a placement rule (ADR-054): no module sees it at all, and in the
/// host only the adapter folder does. Read from compiled metadata and IL, since a type can be used
/// with no <c>using</c> to find, and a package reference alone already puts the library within reach.
/// </summary>
public sealed partial class StripeLibraryPlacementTests
{
    private const string Library = "Stripe.net";
    private const string AdapterNamespace = "LeaseBook.Web.Payments.Stripe";
    private const string AdapterFolder = "src/LeaseBook.Web/Payments/Stripe/";

    [Fact]
    public void No_module_references_the_stripe_library()
    {
        var modules = ArchitectureAssemblies.Application.Where(assembly => assembly != ArchitectureAssemblies.Web).ToArray();
        modules.Length.ShouldBe(ArchitectureAssemblies.Modules.Count + 2, "SharedKernel, Migrator and every module");

        // Vacuity: the host does reference the library under this name. Were the assembly renamed,
        // no module could match it and the checks below would pass while guarding nothing.
        using (var host = Read(ArchitectureAssemblies.Web))
        {
            host.MainModule.AssemblyReferences.Select(reference => reference.Name)
                .ShouldContain(Library, $"the host no longer references {Library}; update this guard to the library's new name");
            host.MainModule.GetTypeReferences().Where(IsStripe).ShouldNotBeEmpty();
        }

        foreach (var assembly in modules)
        {
            using var definition = Read(assembly);
            var name = definition.Name.Name;
            definition.MainModule.AssemblyReferences.Select(reference => reference.Name)
                .ShouldNotContain(Library, $"{name} must not reference the Stripe library; it belongs to the host's adapter");
            definition.MainModule.GetTypeReferences().Where(IsStripe).Select(type => type.FullName)
                .ShouldBeEmpty($"{name} must not use a Stripe type");
        }
    }

    [Fact]
    public void In_the_host_only_the_adapter_folder_uses_a_stripe_type()
    {
        using var definition = Read(ArchitectureAssemblies.Web);
        var users = Descendants(definition.MainModule.Types)
            .SelectMany(type => StripeUses(type).Select(use => (Namespace: Outermost(type).Namespace, Use: use)))
            .ToArray();

        // Vacuity: were the library renamed, or the adapter moved out of the host, nothing would match
        // and the offender check below would pass while guarding nothing.
        users.Where(user => InAdapter(user.Namespace)).ShouldNotBeEmpty(
            $"nothing in {AdapterNamespace} uses a {Library} type; update this guard to the new shape");

        users.Where(user => !InAdapter(user.Namespace)).Select(user => user.Use).Distinct(StringComparer.Ordinal)
            .ShouldBeEmpty($"a Stripe type may be used only in {AdapterNamespace}");
    }

    /// <summary>
    /// The rule above places by namespace, and a namespace is whatever a file says it is. So the
    /// folder is held to it from the source: outside the adapter folder no host file may declare the
    /// adapter's namespace, which would put it inside the rule above, or import the library.
    /// </summary>
    [Fact]
    public void No_host_file_outside_the_adapter_folder_claims_its_namespace_or_imports_stripe()
    {
        var files = RepositorySource.Current.CodeFilesUnder("src/LeaseBook.Web");
        bool InFolder(RepositoryFile file) => file.RelativePath.Replace('\\', '/').StartsWith(AdapterFolder, StringComparison.Ordinal);

        // Vacuity: the folder holds the adapter, and the adapter is found by both patterns.
        var adapter = files.Where(InFolder).ToArray();
        adapter.SelectMany(file => file.Find(AdapterNamespaceDeclaration())).ShouldNotBeEmpty(
            $"no file under {AdapterFolder} declares {AdapterNamespace}; update this guard to the new shape");
        adapter.SelectMany(file => file.Find(StripeImport())).ShouldNotBeEmpty(
            $"no file under {AdapterFolder} imports Stripe; update this guard to the new shape");

        files.Where(file => !InFolder(file))
            .SelectMany(file => file.Find(AdapterNamespaceDeclaration()).Concat(file.Find(StripeImport())))
            .Select(match => match.ToString())
            .ShouldBeEmpty($"only files under {AdapterFolder} may declare {AdapterNamespace} or import Stripe");
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*namespace\s+LeaseBook\.Web\.Payments\.Stripe\b")]
    private static partial System.Text.RegularExpressions.Regex AdapterNamespaceDeclaration();

    // `using Stripe;`, `using Stripe.X;`, `global using`, `using static` and an alias onto the library.
    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*(global\s+)?using\s+(static\s+)?(\w+\s*=\s*)?(global::)?Stripe\s*[.;]")]
    private static partial System.Text.RegularExpressions.Regex StripeImport();

    private static bool InAdapter(string @namespace) =>
        @namespace == AdapterNamespace || @namespace.StartsWith(AdapterNamespace + ".", StringComparison.Ordinal);

    // By the assembly a type comes from, not its name: the adapter's own namespace ends in "Stripe".
    private static bool IsStripe(TypeReference type) =>
        type.Scope is AssemblyNameReference { Name: Library } || type.Scope is ModuleDefinition { Assembly.Name.Name: Library };

    // Every place a member can name a type: what it declares, and what its body touches.
    private static IEnumerable<string> StripeUses(TypeDefinition type)
    {
        var declared = new TypeReference?[] { type.BaseType }.Concat(type.Interfaces.Select(x => x.InterfaceType))
            .Concat(type.Fields.Select(x => x.FieldType)).Concat(type.Properties.Select(x => x.PropertyType))
            .Concat(type.CustomAttributes.Select(x => x.AttributeType));
        foreach (var use in declared.SelectMany(Flatten).Where(IsStripe))
        { yield return $"{type.FullName} -> {use.FullName}"; }

        foreach (var method in type.Methods)
        {
            var named = new TypeReference?[] { method.ReturnType }.Concat(method.Parameters.Select(x => x.ParameterType))
                .Concat(method.CustomAttributes.Select(x => x.AttributeType));
            if (method.HasBody)
            {
                named = named.Concat(method.Body.Variables.Select(x => x.VariableType))
                    .Concat(method.Body.ExceptionHandlers.Select(x => x.CatchType))
                    .Concat(method.Body.Instructions.SelectMany(Operands));
            }
            foreach (var use in named.SelectMany(Flatten).Where(IsStripe))
            { yield return $"{method.FullName} -> {use.FullName}"; }
        }
    }

    private static IEnumerable<TypeReference?> Operands(Instruction instruction) => instruction.Operand switch
    {
        MethodReference method => new TypeReference?[] { method.DeclaringType, method.ReturnType }
            .Concat(method.Parameters.Select(x => x.ParameterType))
            .Concat(method is GenericInstanceMethod generic ? generic.GenericArguments : []),
        FieldReference field => [field.DeclaringType, field.FieldType],
        TypeReference type => [type],
        _ => [],
    };

    // A type and everything it is built from: List<Stripe.X>, Stripe.X[] and Task<Stripe.X> all use Stripe.X.
    private static IEnumerable<TypeReference> Flatten(TypeReference? type)
    {
        if (type is null) { yield break; }
        yield return type;
        if (type is GenericInstanceType generic)
        {
            foreach (var argument in generic.GenericArguments.SelectMany(Flatten)) { yield return argument; }
        }
        if (type is TypeSpecification specification)
        {
            foreach (var element in Flatten(specification.ElementType)) { yield return element; }
        }
    }

    private static TypeDefinition Outermost(TypeDefinition type) => type.DeclaringType is null ? type : Outermost(type.DeclaringType);

    private static IEnumerable<TypeDefinition> Descendants(IEnumerable<TypeDefinition> roots) =>
        roots.SelectMany(type => new[] { type }.Concat(Descendants(type.NestedTypes)));

    private static AssemblyDefinition Read(ReflectionAssembly assembly) =>
        AssemblyDefinition.ReadAssembly(assembly.Location, new ReaderParameters { InMemory = true, ReadSymbols = false });
}

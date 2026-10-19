using LeaseBook.SharedKernel.Tenancy;
using Shouldly;

namespace LeaseBook.Tests.Tenancy;

/// <summary>
/// The persisted vocabulary of <see cref="Persona"/>: the exact strings the row-level security gate
/// compares against, and which personas are granted rows through a portal link.
/// </summary>
public sealed class PersonaValuesTests
{
    [Theory]
    [InlineData(Persona.None, "none")]
    [InlineData(Persona.System, "system")]
    [InlineData(Persona.Staff, "staff")]
    [InlineData(Persona.Tenant, "tenant")]
    [InlineData(Persona.Owner, "owner")]
    public void Each_persona_has_the_text_the_gate_compares_against(Persona persona, string text)
    {
        persona.ToDbValue().ShouldBe(text);
    }

    [Fact]
    public void Every_defined_persona_has_a_text_and_no_two_share_one()
    {
        var texts = Enum.GetValues<Persona>().Select(persona => persona.ToDbValue()).ToArray();

        texts.Length.ShouldBe(5);
        texts.Distinct(StringComparer.Ordinal).Count().ShouldBe(texts.Length);
    }

    [Fact]
    public void None_is_the_default()
    {
        default(Persona).ShouldBe(Persona.None);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(42)]
    public void A_value_no_persona_names_is_refused_rather_than_rendered(int value)
    {
        var persona = (Persona)value;

        var ex = Should.Throw<ArgumentOutOfRangeException>(() => persona.ToDbValue());

        ex.ParamName.ShouldBe("persona");
        ex.ActualValue.ShouldBe(persona);
        ex.Message.ShouldStartWith("Not a defined persona. (Parameter 'persona')");
    }

    [Theory]
    [InlineData(Persona.None, false)]
    [InlineData(Persona.System, false)]
    [InlineData(Persona.Staff, false)]
    [InlineData(Persona.Tenant, true)]
    [InlineData(Persona.Owner, true)]
    public void Only_the_tenant_and_owner_personas_are_portal_personas(Persona persona, bool portal)
    {
        persona.IsPortal().ShouldBe(portal);
    }
}

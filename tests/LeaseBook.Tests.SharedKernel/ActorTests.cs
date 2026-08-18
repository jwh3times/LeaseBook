using LeaseBook.SharedKernel.Tenancy;
using Shouldly;

namespace LeaseBook.Tests.SharedKernel;

/// <summary>
/// The type exists to make "nobody is accountable for this" impossible to say by accident, so the
/// cases worth pinning are the ways a caller could otherwise say it without meaning to: an empty user
/// id, and a blank process name.
/// <para>
/// ADR-039 added a second job — the process name is now persisted, so it also has to be a value an
/// audit read can group by. That moves a whole class of input from "accepted, and useless later" to
/// rejected here, which is what the shape tests below pin.
/// </para>
/// </summary>
public sealed class ActorTests
{
    [Fact]
    public void A_user_actor_carries_the_id_and_no_process()
    {
        var id = Guid.NewGuid();
        var actor = Actor.User(id);

        actor.UserId.ShouldBe(id);
        actor.Process.ShouldBeNull();
        actor.IsSystem.ShouldBeFalse();
        actor.Kind.ShouldBe(Actor.UserKind);
    }

    [Fact]
    public void A_system_actor_carries_the_process_and_no_id()
    {
        var actor = Actor.System("invariant-sweep");

        actor.UserId.ShouldBeNull();
        actor.Process.ShouldBe("invariant-sweep");
        actor.IsSystem.ShouldBeTrue();
        actor.Kind.ShouldBe(Actor.SystemKind);
    }

    // An empty id would stamp created_by with all-zeros rather than null — neither a real user nor an
    // honest "no user", and indistinguishable from a real id at every layer below this one.
    [Fact]
    public void An_empty_user_id_is_refused_and_points_at_the_system_case()
    {
        var ex = Should.Throw<ArgumentException>(() => Actor.User(Guid.Empty));
        ex.Message.ShouldContain("Actor.System");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_system_process_is_refused(string process)
    {
        Should.Throw<ArgumentException>(() => Actor.System(process));
    }

    /// <summary>
    /// Every process name in the repository, so the shape rule is pinned against the real vocabulary
    /// rather than invented examples — if the rule ever rejects one of these, a production call site
    /// breaks.
    /// </summary>
    [Theory]
    [InlineData("seed:demo")]
    [InlineData("seed:cutover")]
    [InlineData("seed:load")]
    [InlineData("invariant-sweep")]
    [InlineData("principal-without-user-id")]
    [InlineData("test-harness")]
    [InlineData("nightly-job")]
    public void The_process_names_in_use_are_accepted(string process)
    {
        Actor.System(process).Process.ShouldBe(process);
    }

    /// <summary>
    /// A durable column that accepts prose stops being groupable: "nightly sweep" and "Nightly sweep"
    /// are two processes as far as any later audit read is concerned. That is the whole reason the
    /// value is constrained rather than merely non-empty.
    /// </summary>
    [Theory]
    [InlineData("Nightly sweep")]                 // spaces and capitals
    [InlineData("ran during maintenance")]        // a sentence, not an identifier
    [InlineData("seed:")]                         // trailing separator, no segment
    [InlineData(":demo")]                         // leading separator
    [InlineData("seed::demo")]                    // empty segment
    [InlineData("seed/demo")]                     // separator outside the allowed set
    public void A_process_name_that_is_not_a_stable_identifier_is_refused(string process)
    {
        var ex = Should.Throw<ArgumentException>(() => Actor.System(process));
        ex.Message.ShouldContain("stable process identifier");
    }

    [Fact]
    public void A_process_name_longer_than_the_column_is_refused()
    {
        var tooLong = new string('a', Actor.MaxProcessLength + 1);

        Should.Throw<ArgumentException>(() => Actor.System(tooLong))
            .Message.ShouldContain("actor_process");
    }

    /// <summary>
    /// The reference is the durable form. It is what an auditor reads back, so both halves must be
    /// recoverable from it — which is exactly what a null <c>created_by</c> alone could not do.
    /// </summary>
    [Fact]
    public void The_reference_says_which_case_it_is()
    {
        var id = Guid.NewGuid();

        Actor.User(id).Reference.ShouldBe($"user:{id}");
        Actor.System("seed:demo").Reference.ShouldBe("system:seed:demo");
        Actor.User(id).ToString().ShouldBe(Actor.User(id).Reference);
    }
}

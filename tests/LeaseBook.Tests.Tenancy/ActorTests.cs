using LeaseBook.SharedKernel.Tenancy;
using Shouldly;

namespace LeaseBook.Tests.Tenancy;

/// <summary>
/// The type exists to make "nobody is accountable for this" impossible to say by accident, so the
/// cases worth pinning are the ways a caller could otherwise say it without meaning to: an empty user
/// id, and a blank process name.
/// <para>
/// ADR-039 added a second job — the process name is now persisted, so it also has to be a value an
/// audit read can group by. That moves a whole class of input from "accepted, and useless later" to
/// rejected here, which is what the shape tests below pin.
/// </para>
/// <para>
/// Moved here from <c>LeaseBook.Tests.SharedKernel</c> so that mutation testing, which reruns only
/// this project, measures <see cref="Actor"/> against them.
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
        actor.Kind.ShouldBe("user");
        Actor.UserKind.ShouldBe("user");
    }

    [Fact]
    public void A_system_actor_carries_the_process_and_no_id()
    {
        var actor = Actor.System("invariant-sweep");

        actor.UserId.ShouldBeNull();
        actor.Process.ShouldBe("invariant-sweep");
        actor.IsSystem.ShouldBeTrue();
        actor.Kind.ShouldBe("system");
        Actor.SystemKind.ShouldBe("system");
    }

    // An empty id would stamp created_by with all-zeros rather than null — neither a real user nor an
    // honest "no user", and indistinguishable from a real id at every layer below this one.
    [Fact]
    public void An_empty_user_id_is_refused_and_points_at_the_system_case()
    {
        var ex = Should.Throw<ArgumentException>(() => Actor.User(Guid.Empty));

        ex.ParamName.ShouldBe("userId");
        ex.Message.ShouldBe(
            "A user actor needs a real user id. If no user is accountable for this work, say so with " +
            "Actor.System(process) rather than passing an empty id. (Parameter 'userId')");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void A_blank_system_process_is_refused(string? process)
    {
        var ex = Should.Throw<ArgumentException>(() => Actor.System(process!));

        ex.ParamName.ShouldBe("process");
        ex.Message.ShouldBe(
            "A system actor needs a process name — the point of this type is that an unattributed write " +
            "cannot be written by accident. (Parameter 'process')");
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
    [InlineData("a")]
    [InlineData("v2.import_run")]
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
    [InlineData("Seed")]                          // a capital
    [InlineData(" seed")]                         // surrounding space
    public void A_process_name_that_is_not_a_stable_identifier_is_refused(string process)
    {
        var ex = Should.Throw<ArgumentException>(() => Actor.System(process));

        ex.ParamName.ShouldBe("process");
        ex.Message.ShouldBe(
            $"'{process}' is not a stable process identifier. Use lower-case segments joined by . : _ or - " +
            "(for example 'seed:demo' or 'invariant-sweep'). This value is persisted, so a free-text reason " +
            "would leave the audit trail unable to group two writes by the same process. (Parameter 'process')");
    }

    [Fact]
    public void A_process_name_as_long_as_the_column_is_accepted()
    {
        var longest = new string('a', 64);

        Actor.MaxProcessLength.ShouldBe(64);
        Actor.System(longest).Process.ShouldBe(longest);
    }

    [Fact]
    public void A_process_name_longer_than_the_column_is_refused()
    {
        var tooLong = new string('a', 65);

        var ex = Should.Throw<ArgumentException>(() => Actor.System(tooLong));

        ex.ParamName.ShouldBe("process");
        ex.Message.ShouldBe(
            $"A system process name must fit the actor_process column (64 chars); '{tooLong}' is 65. " +
            "(Parameter 'process')");
    }

    /// <summary>
    /// The reference is the durable form. It is what an auditor reads back, so both halves must be
    /// recoverable from it — which is exactly what a null <c>created_by</c> alone could not do.
    /// </summary>
    [Fact]
    public void The_reference_says_which_case_it_is()
    {
        var id = Guid.Parse("0199f0aa-1111-7222-8333-944455556666");

        Actor.User(id).Reference.ShouldBe("user:0199f0aa-1111-7222-8333-944455556666");
        Actor.System("seed:demo").Reference.ShouldBe("system:seed:demo");
        Actor.User(id).ToString().ShouldBe("user:0199f0aa-1111-7222-8333-944455556666");
        Actor.System("seed:demo").ToString().ShouldBe("system:seed:demo");
    }

    [Fact]
    public void Two_actors_are_equal_when_they_name_the_same_user_or_process()
    {
        var id = Guid.NewGuid();

        Actor.User(id).ShouldBe(Actor.User(id));
        Actor.System("seed:demo").ShouldBe(Actor.System("seed:demo"));
        Actor.System("seed:demo").ShouldNotBe(Actor.System("seed:load"));
        Actor.User(id).ShouldNotBe(Actor.User(Guid.NewGuid()));
    }
}

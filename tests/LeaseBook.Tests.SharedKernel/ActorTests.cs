using LeaseBook.SharedKernel.Tenancy;
using Shouldly;

namespace LeaseBook.Tests.SharedKernel;

/// <summary>
/// The type exists to make "nobody is accountable for this" impossible to say by accident, so the
/// cases worth pinning are the two ways a caller could otherwise say it without meaning to: an empty
/// user id, and a blank reason.
/// </summary>
public sealed class ActorTests
{
    [Fact]
    public void A_user_actor_carries_the_id_and_no_reason()
    {
        var id = Guid.NewGuid();
        var actor = Actor.User(id);

        actor.UserId.ShouldBe(id);
        actor.Reason.ShouldBeNull();
        actor.IsSystem.ShouldBeFalse();
    }

    [Fact]
    public void A_system_actor_carries_the_reason_and_no_id()
    {
        var actor = Actor.System("invariant-sweep");

        actor.UserId.ShouldBeNull();
        actor.Reason.ShouldBe("invariant-sweep");
        actor.IsSystem.ShouldBeTrue();
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
    public void A_blank_system_reason_is_refused(string reason)
    {
        Should.Throw<ArgumentException>(() => Actor.System(reason));
    }

    [Fact]
    public void The_string_form_says_which_case_it_is()
    {
        var id = Guid.NewGuid();

        Actor.User(id).ToString().ShouldBe($"user:{id}");
        Actor.System("seed:demo").ToString().ShouldBe("system:seed:demo");
    }
}

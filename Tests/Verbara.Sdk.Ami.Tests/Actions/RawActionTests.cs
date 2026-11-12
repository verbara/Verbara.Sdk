using FluentAssertions;
using Verbara.Sdk.Ami.Actions;

namespace Verbara.Sdk.Ami.Tests.Actions;

/// <summary>
/// A raw action is validated where it is built: its name is ASCII letters and digits, a key is ASCII letters, digits,
/// <c>-</c>, <c>_</c>, <c>(</c> or <c>)</c> and never <c>Action</c> or <c>ActionID</c>, and a value holds no line break.
/// A refused <see cref="RawAction.Add"/> leaves the fields as they were and never echoes the value.
/// </summary>
public sealed class RawActionTests
{
    // Rejected values are built from these runs so a fragment of them in a message is detectable.
    private const string Head = "7qz9xw";
    private const string Tail = "3jkvb5";

    [Theory]
    [InlineData("Ping: x")]
    [InlineData("Pi ng")]
    [InlineData("Ping\r\nAction: Logoff")]
    [InlineData("Ping\n")]
    [InlineData("Ping\r")]
    [InlineData("")]
    [InlineData("Ping-x")]
    [InlineData("Ping_x")]
    [InlineData("Pïng")]
    [InlineData("Ping\t")]
    public void Constructor_ShouldThrowArgumentExceptionNamingTheName_WhenNameIsNotAsciiLettersAndDigits(string name)
    {
        var act = () => new RawAction(name);

        act.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("name");
    }

    [Fact]
    public void Constructor_ShouldThrowArgumentException_WhenNameIsNull()
    {
        var act = () => new RawAction(null!);

        act.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("name");
    }

    [Theory]
    [InlineData("Ping")]
    [InlineData("pInG")]
    [InlineData("devicestatelist")]
    [InlineData("DAHDIDNDon")]
    [InlineData("LoggerRotate")]
    public void Constructor_ShouldKeepTheNameExactlyAsGiven_WhenNameIsAsciiLettersAndDigits(string name)
    {
        var action = new RawAction(name);

        action.Name.Should().Be(name);
        action.Fields.Should().BeEmpty();
        action.ActionId.Should().BeNull();
    }

    [Theory]
    [InlineData("Channel: x")]
    [InlineData("")]
    [InlineData("Action")]
    [InlineData("action")]
    [InlineData("ACTION")]
    [InlineData("ActionID")]
    [InlineData("actionid")]
    [InlineData("ACTIONID")]
    [InlineData("Act ion")]
    [InlineData("Channel\r\nAction: Logoff")]
    [InlineData("Channel\n")]
    [InlineData("Var.1")]
    [InlineData("Kéy")]
    public void Add_ShouldThrowArgumentExceptionAndLeaveFieldsUnchanged_WhenKeyIsRefused(string key)
    {
        var action = new RawAction("ListCategories").Add("Filename", "manager.conf");

        var act = () => action.Add(key, Head + Tail);

        var thrown = act.Should().Throw<ArgumentException>().Which;
        thrown.ParamName.Should().Be("key");
        thrown.Message.Should().NotContain(Head).And.NotContain(Tail);
        action.Fields.Should().Equal(new[] { new KeyValuePair<string, string>("Filename", "manager.conf") });
    }

    [Fact]
    public void Add_ShouldThrowArgumentExceptionAndLeaveFieldsUnchanged_WhenKeyIsNull()
    {
        var action = new RawAction("ListCategories").Add("Filename", "manager.conf");

        var act = () => action.Add(null!, "x");

        act.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("key");
        action.Fields.Should().ContainSingle();
    }

    [Theory]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Add_ShouldThrowArgumentExceptionNamingTheKeyWithoutTheValue_WhenValueHoldsALineBreak(string lineBreak)
    {
        var action = new RawAction("DAHDIDNDon").Add("DAHDIChannel", "1");

        var act = () => action.Add("Secret", Head + lineBreak + Tail);

        var thrown = act.Should().Throw<ArgumentException>().Which;
        thrown.ParamName.Should().Be("value");
        thrown.Message.Should().Contain("Secret").And.NotContain(Head).And.NotContain(Tail);
        action.Fields.Should().Equal(new[] { new KeyValuePair<string, string>("DAHDIChannel", "1") });
    }

    [Fact]
    public void Add_ShouldThrowArgumentExceptionAndLeaveFieldsUnchanged_WhenValueIsNull()
    {
        var action = new RawAction("DAHDIDNDon").Add("DAHDIChannel", "1");

        var act = () => action.Add("Secret", null!);

        act.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("value");
        action.Fields.Should().ContainSingle();
    }

    [Theory]
    [InlineData("Action-000000")]
    [InlineData("Cat-000000")]
    [InlineData("DAHDIChannel")]
    [InlineData("Var_1")]
    [InlineData("UnitAmount(0)")]
    [InlineData("UnitType(0)")]
    [InlineData("ActionIDs")]
    public void Add_ShouldAcceptTheKey_WhenKeyIsInTheAllowedClassAndNotReserved(string key)
    {
        var action = new RawAction("UpdateConfig");

        action.Add(key, "x").Should().BeSameAs(action);

        action.Fields.Should().Equal(new[] { new KeyValuePair<string, string>(key, "x") });
    }

    [Fact]
    public void Add_ShouldKeepFieldsInOrderWithRepeatedKeys_WhenAddedInSequence()
    {
        var action = new RawAction("Originate")
            .Add("Channel", "PJSIP/2000")
            .Add("Variable", "a=1")
            .Add("Variable", "b=2")
            .Add("Empty", "");

        action.Fields.Should().Equal(
            new KeyValuePair<string, string>("Channel", "PJSIP/2000"),
            new KeyValuePair<string, string>("Variable", "a=1"),
            new KeyValuePair<string, string>("Variable", "b=2"),
            new KeyValuePair<string, string>("Empty", ""));
    }

    [Fact]
    public void Fields_ShouldNotBeCastableToAMutableList_WhenReadByACaller()
    {
        var action = new RawAction("LoggerRotate").Add("A", "1");

        (action.Fields is IList<KeyValuePair<string, string>> list && !list.IsReadOnly)
            .Should().BeFalse("the fields change only through Add, which validates");
    }
}

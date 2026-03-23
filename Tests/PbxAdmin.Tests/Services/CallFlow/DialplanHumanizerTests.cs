using FluentAssertions;
using PbxAdmin.Services.CallFlow;

namespace PbxAdmin.Tests.Services.CallFlow;

public class DialplanHumanizerTests
{
    [Fact]
    public void Humanize_ShouldTranslate_GotoExtension()
    {
        DialplanHumanizer.Humanize("Goto", "default,1001,1")
            .Should().Be("Forward to ext 1001");
    }

    [Fact]
    public void Humanize_ShouldTranslate_GotoQueue()
    {
        DialplanHumanizer.Humanize("Goto", "queues,sales,1")
            .Should().Be("Send to queue 'sales'");
    }

    [Fact]
    public void Humanize_ShouldTranslate_GotoTimeCondition()
    {
        DialplanHumanizer.Humanize("Goto", "tc-business-hours,s,1")
            .Should().Be("Check time condition 'business-hours'");
    }

    [Fact]
    public void Humanize_ShouldTranslate_GotoIvr()
    {
        DialplanHumanizer.Humanize("Goto", "ivr-main,s,1")
            .Should().Be("Go to IVR 'main'");
    }

    [Fact]
    public void Humanize_ShouldTranslate_DialTrunk()
    {
        DialplanHumanizer.Humanize("Dial", "PJSIP/${EXTEN}@trunk-primary,60")
            .Should().Be("Dial via trunk-primary (60s)");
    }

    [Fact]
    public void Humanize_ShouldTranslate_QueueApp()
    {
        DialplanHumanizer.Humanize("Queue", "sales,,,,300")
            .Should().Be("Queue 'sales' (300s timeout)");
    }

    [Fact]
    public void Humanize_ShouldTranslate_GotoIfTime()
    {
        DialplanHumanizer.Humanize("GotoIfTime", "09:00-18:00,mon,*,*?tc-bh-open,s,1")
            .Should().Be("If Mon 09:00-18:00 \u2192 open");
    }

    [Fact]
    public void Humanize_ShouldTranslate_SetRoute()
    {
        DialplanHumanizer.Humanize("Set", "__ROUTE=To-PSTN")
            .Should().Be("Set route: To-PSTN");
    }

    [Fact]
    public void Humanize_ShouldTranslate_GotoIfOverride()
    {
        DialplanHumanizer.Humanize("GotoIf", "$[\"${OVERRIDE}\"=\"OPEN\"]?tc-bh-open,s,1")
            .Should().Be("If override=OPEN \u2192 go to open");
    }

    [Fact]
    public void Humanize_ShouldTranslate_GotoIfRetriesIvr()
    {
        DialplanHumanizer.Humanize("GotoIf", "$[${IVR_RETRIES}<3]?s,2")
            .Should().Be("If retries < 3 \u2192 replay menu");
    }

    [Fact]
    public void Humanize_ShouldTranslate_Hangup()
    {
        DialplanHumanizer.Humanize("Hangup", "")
            .Should().Be("Hang up");
    }

    [Fact]
    public void Humanize_ShouldTranslate_Answer()
    {
        DialplanHumanizer.Humanize("Answer", "")
            .Should().Be("Answer call");
    }

    [Fact]
    public void Humanize_ShouldTranslate_VoiceMail()
    {
        DialplanHumanizer.Humanize("VoiceMail", "1005@default,u")
            .Should().Be("Voicemail for 1005 (unavailable)");
    }

    [Fact]
    public void Humanize_ShouldTranslate_Background()
    {
        DialplanHumanizer.Humanize("Background", "greeting")
            .Should().Be("Play 'greeting' (wait for input)");
    }

    [Fact]
    public void Humanize_ShouldTranslate_WaitExten()
    {
        DialplanHumanizer.Humanize("WaitExten", "5")
            .Should().Be("Wait 5s for digit");
    }

    [Fact]
    public void Humanize_ShouldTranslate_Playback()
    {
        DialplanHumanizer.Humanize("Playback", "option-is-invalid")
            .Should().Be("Play 'option-is-invalid'");
    }

    [Fact]
    public void Humanize_ShouldTranslate_SetIvrRetries()
    {
        DialplanHumanizer.Humanize("Set", "IVR_RETRIES=$[${IVR_RETRIES}+1]")
            .Should().Be("Increment retry counter");
    }

    [Fact]
    public void Humanize_ShouldTranslate_SetOutnum()
    {
        DialplanHumanizer.Humanize("Set", "OUTNUM=+1${EXTEN:1}")
            .Should().Be("Transform dialed number");
    }

    [Fact]
    public void Humanize_ShouldFallback_UnknownApp()
    {
        DialplanHumanizer.Humanize("AGI", "custom-script.agi")
            .Should().Be("AGI(custom-script.agi)");
    }

    [Fact]
    public void Humanize_ShouldFallback_EmptyAppData()
    {
        DialplanHumanizer.Humanize("NoOp", "")
            .Should().Be("NoOp");
    }
}

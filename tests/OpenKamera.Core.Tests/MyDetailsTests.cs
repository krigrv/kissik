using OpenKamera.Core;
using Xunit;

namespace OpenKamera.Core.Tests;

public class MyDetailsTests
{
    [Fact]
    public void BeforeAnyDetailsPlaceholdersStayAsTheyAre()
    {
        var me = new MyDetails();
        Assert.Equal("Your Name", me.Fill("Your Name"));
        Assert.Equal("you@example.com", me.Fill("you@example.com"));
    }

    [Fact]
    public void PlaceholdersBecomeTheUsersDetailsAndEmptyOnesHide()
    {
        var me = new MyDetails { Name = "Asha Rao", Handle = "@asharao" };
        Assert.Equal("Asha Rao", me.Fill("Your Name"));
        Assert.Equal("AR", me.Fill("YN"));
        Assert.Equal("@asharao", me.Fill("@yourhandle"));
        Assert.Equal("ASHA RAO", me.Fill("YOUR NAME"));
        Assert.Null(me.Fill("you@example.com"));
        Assert.Null(me.Fill("City, Country"));
        Assert.Equal("Golden hour with @asharao", me.Fill("Golden hour with @yourname"));
    }

    [Fact]
    public void TextTheUserTypedIsLeftAlone()
    {
        var me = new MyDetails { Name = "Asha Rao" };
        Assert.Equal("Team standup", me.Fill("Team standup"));
    }

    [Fact]
    public void PronounsAndYourOwnFieldsFillScenes()
    {
        var me = new MyDetails { Name = "Asha Rao", Pronouns = "she/her" };
        me.SetCustom("Company = Acme\n{Show} = Friday Live\nnot a field\n");
        Assert.Equal("she/her", me.Fill("they/them"));
        Assert.Equal("Asha Rao at Acme · Friday Live", me.Fill("Your Name at {company} · {Show}"));
        Assert.Equal("Company = Acme\nShow = Friday Live", me.CustomText);
        Assert.False(me.HasLogo);
    }

    [Fact]
    public void NameCardIsCompactAndInTheLowerLeft()
    {
        var card = SceneDocument.Templates.First(s => s.Id == "name-card");
        Assert.True(MyDetails.HasPlaceholders(card));
        foreach (var l in card.Overlays)
        {
            Assert.True(l.X + l.W <= 0.4, $"{l.Name} reaches too far right");
            Assert.True(l.Y >= 0.75, $"{l.Name} sits too high (over the face)");
        }
    }
}

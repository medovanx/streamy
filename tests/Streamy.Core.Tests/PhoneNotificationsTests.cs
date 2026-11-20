using Streamy.Core.Adb;

namespace Streamy.Core.Tests;

/// <summary>Rounds in the shape the watch loop prints (made-up notifications).</summary>
public class PhoneNotificationsTests
{
    private const string Round1 = """
            NotificationRecord(0x0ab37f35: pkg=com.example.chat user=UserHandle{0} id=7 tag=null importance=4 key=0|com.example.chat|7|null|10200: Notification(channel=msgs shortcut=null flags=0x0))
              flags=16
                    when=1795150532699/1795150532699
                        android.title=String (Sam)
                        android.text=SpannableString (See you at 8)
                        android.bigText=null
            NotificationRecord(0x1: pkg=com.example.music user=UserHandle{0} id=1 tag=null importance=2 key=0|com.example.music|1|null|10300: Notification(channel=play))
              flags=98
                    when=1795150000000/1795150000000
                        android.title=String (Song)
                        android.text=String (Artist)
            NotificationRecord(0x2: pkg=com.example.mail user=UserHandle{0} id=3 tag=x importance=3 key=0|com.example.mail|3|x|10400: Notification(channel=m))
              flags=0
                    when=1795150100000/1795150100000
                        android.title=String (Invoice (May))
                        android.text=String (short)
                        android.bigText=String (The long version)
            ---STREAMY-END
            """;

    [Fact]
    public void ParsesRecords()
    {
        var list = PhoneNotifications.Parse(Round1);
        Assert.Equal(3, list.Count);
        var chat = list[0];
        Assert.Equal("com.example.chat", chat.Package);
        Assert.Equal("0|com.example.chat|7|null|10200", chat.Key);
        Assert.Equal(16, chat.Flags);
        Assert.Equal(1795150532699, chat.When);
        Assert.Equal("Sam", chat.Title);
        Assert.Equal("See you at 8", chat.Text);
        Assert.True(chat.IsWorthShowing);
        Assert.False(list[1].IsWorthShowing); // ongoing + foreground service: a music player
        Assert.Equal("Invoice (May)", list[2].Title);
        Assert.Equal("The long version", list[2].Text);
    }

    [Fact]
    public void FirstRoundIsOnlyLearnedThenNewOnesShow()
    {
        var tracker = new NotificationTracker();
        var first = PhoneNotifications.Parse(Round1);
        Assert.Empty(tracker.Next(first));
        Assert.Empty(tracker.Next(first));
        var again = Round1.Replace("1795150532699/1795150532699", "1795150599999/1795150599999"); // a new message in the chat
        var fresh = tracker.Next(PhoneNotifications.Parse(again));
        Assert.Equal(["com.example.chat"], fresh.Select(n => n.Package));
    }

    [Fact]
    public void WatchCommandEndsEachRound() =>
        Assert.Contains("echo ---STREAMY-END; sleep 5; done", PhoneNotifications.WatchCommand(5));
}

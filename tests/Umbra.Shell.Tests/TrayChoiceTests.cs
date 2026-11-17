using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using Umbra.Shell.Rendering;
using Umbra.Shell.Settings;
using Umbra.Shell.Shell;

namespace Umbra.Shell.Tests;

/// <summary>
/// Which notification icons sit on the bar: the stored list, the drop geometry that changes it,
/// and the settings page that lists it.
/// </summary>
public class TrayChoiceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"umbra-tray-{Guid.NewGuid():N}.txt");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    /// <summary>Exact, case-blind match, standing in for <c>NotifyIcon.IsEqualByIdentifier</c>.</summary>
    private static Func<string, bool> Is(string id) => entry => string.Equals(entry, id, StringComparison.OrdinalIgnoreCase);

    private TrayChoice Seeded(params string[] ids)
    {
        TrayChoice choice = new(_path);
        foreach (string id in ids) choice.Add(id, Is(id));
        return choice;
    }

    // ------------------------------------------------------------------ the file

    [Fact]
    public void Nothing_Is_On_The_Bar_Until_Chosen()
    {
        TrayChoice choice = new(_path);

        Assert.Empty(choice.OnBar);
        Assert.False(choice.ShowAll);
        Assert.False(choice.ShowsOnBar(Is("a")));
    }

    [Fact]
    public void The_Choice_And_Show_All_Survive_A_Restart()
    {
        TrayChoice first = Seeded("a", "b");
        first.ShowAllOnBar();

        TrayChoice second = new(_path);

        Assert.Equal(["a", "b"], second.OnBar);
        Assert.True(second.ShowAll);
    }

    /// <summary>
    /// "Put icons back in the menu" means every icon, including a chosen one whose application is
    /// not running, which would otherwise come back to the bar when it starts.
    /// </summary>
    [Fact]
    public void Putting_Icons_Back_Empties_The_Bar()
    {
        TrayChoice choice = Seeded("a", "asleep");
        choice.ShowAllOnBar();
        Assert.True(choice.ShowsOnBar(Is("b")));

        Assert.True(choice.PutAllBack());

        Assert.False(choice.ShowAll);
        Assert.Empty(choice.OnBar);
        Assert.False(choice.ShowsOnBar(Is("a")));
        Assert.False(choice.ShowsOnBar(Is("asleep")));

        TrayChoice reread = new(_path);
        Assert.False(reread.ShowAll);
        Assert.Empty(reread.OnBar);
    }

    /// <summary>Without "show all" too: icons dragged onto the bar one by one all go back.</summary>
    [Fact]
    public void Putting_Icons_Back_Without_Show_All_Takes_The_Chosen_Ones_Too()
    {
        TrayChoice choice = Seeded("a", "b");

        Assert.True(choice.PutAllBack());
        Assert.Empty(choice.OnBar);
    }

    /// <summary>
    /// The owner's report, 15 Nov: icons dragged along the bar under "show all" were silently
    /// chosen, and "Put icons back in the menu" then left them on the bar.
    /// </summary>
    [Fact]
    public void Icons_Moved_Under_Show_All_Still_All_Go_Back()
    {
        TrayChoice choice = new(_path);
        choice.ShowAllOnBar();

        choice.PlaceOnBar("c", Is("c"), Is("a"), Row("a", "b", "c"));
        choice.PlaceOnBar("b", Is("b"), before: null, Row("c", "a", "b"));
        Assert.NotEmpty(choice.OnBar);

        choice.PutAllBack();

        Assert.Empty(choice.OnBar);
        Assert.False(choice.ShowsOnBar(Is("a")));
        Assert.False(choice.ShowsOnBar(Is("b")));
        Assert.False(choice.ShowsOnBar(Is("c")));
    }

    /// <summary>
    /// Identifiers are <c>path:uid:title</c> with any title at all, so only the exact setting line is
    /// read as the setting; an icon titled after it is still an icon.
    /// </summary>
    [Fact]
    public void Only_The_Exact_Setting_Line_Is_Read_As_The_Setting()
    {
        File.WriteAllLines(_path,
        [
            "showAll = true",
            @"C:\Apps\Odd.exe:1:showAll = maybe",
            @"C:\Apps\Tool.exe:3:Tool = ready",
        ]);

        TrayChoice choice = new(_path);

        Assert.True(choice.ShowAll);
        Assert.Equal([@"C:\Apps\Odd.exe:1:showAll = maybe", @"C:\Apps\Tool.exe:3:Tool = ready"], choice.OnBar);
    }

    [Fact]
    public void A_Line_Named_Twice_In_A_Hand_Edited_File_Is_Kept_Once()
    {
        File.WriteAllLines(_path, ["a", "A", "b"]);

        Assert.Equal(["a", "b"], new TrayChoice(_path).OnBar);
    }

    private const string TwoLineTitle = @"C:\Program Files\Certum\SimplySign Desktop\SimplySignDesktop.exe:1:SimplySign Desktop" + "\nStatus: not connected";

    /// <summary>
    /// A title of two lines was written over two lines of the file, and the second came back as an
    /// icon of its own, which Settings listed as "Status: not connected".
    /// </summary>
    [Fact]
    public void A_Two_Line_Title_Is_One_Line_In_The_File_And_Comes_Back_Whole()
    {
        const string guid = "6da68f06-00f1-4e6e-8158-7f1ffd4f9db9";
        const string tool = @"C:\Apps\Tool.exe:3:Tool";
        Seeded(guid, TwoLineTitle, tool);

        Assert.Equal(4, File.ReadAllLines(_path).Length);
        Assert.Equal([guid, TwoLineTitle, tool], new TrayChoice(_path).OnBar);
    }

    [Fact]
    public void A_Windows_Line_Break_In_A_Title_Comes_Back_Too()
    {
        string id = @"C:\Apps\Sync.exe:3:Sync" + "\r\nUp to date";
        Seeded(id);

        Assert.Equal(2, File.ReadAllLines(_path).Length);
        Assert.Equal([id], new TrayChoice(_path).OnBar);
    }

    /// <summary>A title holding a percent sign, or the very text an escape is written as, survives.</summary>
    [Theory]
    [InlineData(@"C:\Apps\Battery.exe:2:Battery 80%")]
    [InlineData(@"C:\Apps\Odd.exe:2:Literally %0A and %25 and %0D")]
    [InlineData(@"C:\Apps\Odd.exe:2:Ends in %")]
    [InlineData(@"C:\Apps\Odd.exe:2:%2")]
    public void Percent_Signs_In_A_Title_Survive(string id)
    {
        Seeded(id);
        Assert.Equal([id], new TrayChoice(_path).OnBar);
    }

    /// <summary>
    /// A file written before entries were escaped (the owner's, 15 Nov) holds each second line on
    /// a line of its own. It is joined back onto the entry above, so nothing is lost and nothing
    /// phantom is listed.
    /// </summary>
    [Fact]
    public void An_Old_File_With_Split_Titles_Is_Read_Back_Whole()
    {
        File.WriteAllLines(_path,
        [
            "showAll = false",
            "6da68f06-00f1-4e6e-8158-7f1ffd4f9db9",
            @"C:\Program Files\Certum\SimplySign Desktop\SimplySignDesktop.exe:1:SimplySign Desktop",
            "Status: not connected",
            "ba82e2dc-f405-47ad-b032-cf0faa0e3933",
            @"C:\Users\Dev\AppData\Local\Microsoft\OneDrive\OneDrive.exe:501:OneDrive - Personal",
            "Not signed in",
        ]);

        TrayChoice choice = new(_path);

        Assert.Equal(
        [
            "6da68f06-00f1-4e6e-8158-7f1ffd4f9db9",
            TwoLineTitle,
            "ba82e2dc-f405-47ad-b032-cf0faa0e3933",
            @"C:\Users\Dev\AppData\Local\Microsoft\OneDrive\OneDrive.exe:501:OneDrive - Personal" + "\nNot signed in",
        ], choice.OnBar);
        Assert.Equal(4, TrayIconRow.For(choice, []).Count);
    }

    /// <summary>Only a line that cannot stand alone is joined; a GUID or an identifier never is.</summary>
    [Theory]
    [InlineData("6da68f06-00f1-4e6e-8158-7f1ffd4f9db9", true)]
    [InlineData(@"C:\Apps\Tool.exe:3:Tool", true)]
    [InlineData(@":7:An icon with no path", true)]
    [InlineData("Status: not connected", false)]
    [InlineData("Not signed in", false)]
    public void Only_A_Line_That_Cannot_Stand_Alone_Is_A_Second_Line(string line, bool standsAlone) =>
        Assert.Equal(standsAlone, TrayChoice.CouldBeIdentifier(line));

    /// <summary>A GUID entry has no title, so the line after it is never its second line.</summary>
    [Fact]
    public void A_Line_After_A_Guid_Entry_Is_Never_Joined_To_It()
    {
        File.WriteAllLines(_path, ["6da68f06-00f1-4e6e-8158-7f1ffd4f9db9", "Status: stray"]);

        Assert.Equal(["6da68f06-00f1-4e6e-8158-7f1ffd4f9db9", "Status: stray"], new TrayChoice(_path).OnBar);
    }

    /// <summary>A hand-edited file may have the setting line between entries; it ends a title.</summary>
    [Fact]
    public void A_Line_After_The_Setting_Line_Is_Never_Joined_To_It()
    {
        File.WriteAllLines(_path, [@"C:\Apps\Tool.exe:3:Tool", "showAll = true", "Status: stray"]);

        Assert.Equal([@"C:\Apps\Tool.exe:3:Tool", "Status: stray"], new TrayChoice(_path).OnBar);
    }

    /// <summary>A file written before escaping holds percent signs as they are, even one at the very end.</summary>
    [Theory]
    [InlineData(@"C:\Apps\Odd.exe:2:Old %2")]
    [InlineData(@"C:\Apps\Odd.exe:2:Old %")]
    [InlineData(@"C:\Apps\Battery.exe:2:Battery 80% left")]
    public void An_Old_File_Reads_Percent_Signs_As_Written(string id)
    {
        File.WriteAllLines(_path, [id]);

        Assert.Equal([id], new TrayChoice(_path).OnBar);
    }

    [Fact]
    public void Changed_Is_Raised_Only_When_Something_Changed()
    {
        TrayChoice empty = new(_path);
        int emptyRaised = 0;
        empty.Changed += () => emptyRaised++;
        Assert.False(empty.PutAllBack());
        Assert.Equal(0, emptyRaised);

        TrayChoice choice = Seeded("a");
        int raised = 0;
        choice.Changed += () => raised++;

        choice.Add("a", Is("a"));
        choice.Remove(Is("missing"));
        Assert.Equal(0, raised);

        choice.Add("b", Is("b"));
        choice.Remove(Is("a"));
        Assert.True(choice.ShowAllOnBar());
        Assert.False(choice.ShowAllOnBar());
        Assert.Equal(3, raised);

        Assert.True(choice.PutAllBack());
        Assert.Equal(4, raised);
    }

    [Fact]
    public void The_System_Indicators_Come_First_In_ManagedShells_List() =>
        Assert.Equal(["vol", "net", "a", "b"], Seeded("a", "b").PinnedFor(["vol", "net"]));

    // ------------------------------------------------------------------ placing by drag

    [Theory]
    [InlineData(0, "x,a,b,c")]
    [InlineData(2, "a,b,x,c")]
    [InlineData(3, "a,b,c,x")]
    [InlineData(99, "a,b,c,x")]
    public void A_New_Icon_Goes_Into_The_Gap_It_Was_Dropped_In(int index, string expected)
    {
        TrayChoice choice = Seeded("a", "b", "c");

        choice.Place("x", Is("x"), index);

        Assert.Equal(expected.Split(','), choice.OnBar);
    }

    /// <summary>
    /// A drop position counts the gaps as the row shows them, with the dragged icon still in it.
    /// Moving right past one neighbour is gap 2 for an icon at 0, and lands it at 1.
    /// </summary>
    [Theory]
    [InlineData("a", 2, "b,a,c")]
    [InlineData("a", 3, "b,c,a")]
    [InlineData("c", 0, "c,a,b")]
    [InlineData("c", 1, "a,c,b")]
    [InlineData("b", 0, "b,a,c")]
    public void A_Chosen_Icon_Moves_To_The_Gap_It_Was_Dropped_In(string moved, int gap, string expected)
    {
        TrayChoice choice = Seeded("a", "b", "c");

        Assert.True(choice.Place(moved, Is(moved), gap));
        Assert.Equal(expected.Split(','), choice.OnBar);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Dropping_An_Icon_Beside_Itself_Changes_Nothing(int gap)
    {
        TrayChoice choice = Seeded("a", "b", "c");
        int raised = 0;
        choice.Changed += () => raised++;

        Assert.False(choice.Place("b", Is("b"), gap));
        Assert.Equal(["a", "b", "c"], choice.OnBar);
        Assert.Equal(0, raised);
    }

    /// <summary>
    /// The row draws only chosen icons that are running, so a gap among drawn icons is turned into
    /// a list position by the icon after it: whatever hidden entries lie between stay put.
    /// </summary>
    [Fact]
    public void A_Drop_In_Front_Of_An_Icon_Lands_In_Front_Of_It_Past_Icons_That_Are_Not_Running()
    {
        // "gone" is chosen but not running, so the bar draws a, c.
        TrayChoice choice = Seeded("a", "gone", "c");

        int index = choice.IndexBefore(Is("c"));
        choice.Place("x", Is("x"), index);

        Assert.Equal(["a", "gone", "x", "c"], choice.OnBar);
        Assert.Equal(4, choice.IndexBefore(null));
    }

    /// <summary>With "show all", an unchosen icon can be the one after the gap; that means the end.</summary>
    [Fact]
    public void A_Drop_In_Front_Of_An_Unchosen_Icon_Joins_The_End_Of_The_Chosen_Ones() =>
        Assert.Equal(2, Seeded("a", "b").IndexBefore(Is("unchosen")));

    [Fact]
    public void Removing_Takes_Every_Copy()
    {
        File.WriteAllLines(_path, ["a", "b"]);
        TrayChoice choice = new(_path);

        Assert.True(choice.Remove(entry => entry is "a" or "b"));
        Assert.Empty(choice.OnBar);
        Assert.Empty(new TrayChoice(_path).OnBar);
    }

    // ------------------------------------------------------------------ dropping while "show all" is on

    private static TrayEntry[] Row(params string[] ids) => [.. ids.Select(id => new TrayEntry(id, Is(id)))];

    /// <summary>
    /// The owner's report, 15 Nov: with every icon on the bar, an icon dropped on the chevron did
    /// nothing and said nothing. It now goes behind the chevron and "show all" ends, with every
    /// other icon kept on the bar in the order it was drawn.
    /// </summary>
    [Fact]
    public void A_Drop_In_The_Menu_Under_Show_All_Takes_That_Icon_Off_And_Keeps_The_Rest()
    {
        TrayChoice choice = Seeded("b");
        choice.ShowAllOnBar();
        int changes = 0;
        choice.Changed += () => changes++;

        // Drawn: the chosen "b", then the rest in arrival order.
        Assert.True(choice.PutBehindChevron(Is("c"), Row("b", "a", "c", "d")));

        Assert.False(choice.ShowAll);
        Assert.Equal(["b", "a", "d"], choice.OnBar);
        Assert.False(choice.ShowsOnBar(Is("c")));
        Assert.Equal(1, changes);

        TrayChoice reread = new(_path);
        Assert.False(reread.ShowAll);
        Assert.Equal(["b", "a", "d"], reread.OnBar);
    }

    /// <summary>A chosen icon that is not running keeps its place for when it comes back.</summary>
    [Fact]
    public void Ending_Show_All_Keeps_A_Chosen_Icon_That_Is_Not_Running()
    {
        TrayChoice choice = Seeded("asleep", "b");
        choice.ShowAllOnBar();

        choice.PutBehindChevron(Is("b"), Row("a"));

        Assert.Equal(["asleep", "a"], choice.OnBar);
    }

    [Fact]
    public void Without_Show_All_A_Drop_In_The_Menu_Only_Removes()
    {
        TrayChoice choice = Seeded("a", "b");

        Assert.True(choice.PutBehindChevron(Is("a"), Row("a", "b", "extra")));

        Assert.Equal(["b"], choice.OnBar);
        Assert.False(choice.ShowAll);
    }

    /// <summary>
    /// Under "show all", dragging an unchosen icon to the end of the row left it where it was: the
    /// end of the chosen list is still in front of the unchosen ones.
    /// </summary>
    [Fact]
    public void Under_Show_All_A_Drop_At_The_End_Moves_An_Unchosen_Icon_There()
    {
        TrayChoice choice = Seeded("a");
        choice.ShowAllOnBar();

        Assert.True(choice.PlaceOnBar("b", Is("b"), before: null, Row("a", "b", "c")));

        Assert.Equal(["a", "c", "b"], choice.OnBar);
        Assert.True(choice.ShowAll);
    }

    [Fact]
    public void Without_Show_All_A_Bar_Drop_Leaves_Unchosen_Icons_Alone()
    {
        TrayChoice choice = Seeded("a", "b");

        Assert.True(choice.PlaceOnBar("b", Is("b"), Is("a"), Row("a", "b")));

        Assert.Equal(["b", "a"], choice.OnBar);
    }

    // ------------------------------------------------------------------ drop geometry

    [Theory]
    [InlineData(3, 0, false)]
    [InlineData(4, 0, true)]
    [InlineData(0, -4, true)]
    [InlineData(-3, 3, false)]
    public void A_Drag_Starts_Past_The_Threshold_On_Either_Axis(double dx, double dy, bool drag) =>
        Assert.Equal(drag, TrayDrop.IsDrag(new Point(100, 100), new Point(100 + dx, 100 + dy), 4, 4));

    /// <summary>
    /// The menu follows where the icons are, not the switch. The case that went wrong: after a drop
    /// on the chevron ended "show all", icons sat in both places and only "Show all" was offered.
    /// </summary>
    [Theory]
    [InlineData(false, 0, 3, "ShowAll")]
    [InlineData(true, 5, 0, "PutAllBack")]
    [InlineData(true, 0, 0, "PutAllBack")]
    [InlineData(false, 4, 1, "ShowAll,PutAllBack")]
    [InlineData(false, 2, 0, "PutAllBack")]
    [InlineData(false, 0, 0, "")]
    public void The_Chevron_Menu_Offers_What_Fits_Where_The_Icons_Are(bool showAll, int onBar, int inMenu, string expected) =>
        Assert.Equal(expected, string.Join(",", TrayChevronMenu.Offers(showAll, onBar, inMenu)));

    [Fact]
    public void The_Chevron_Menu_Says_What_Each_Item_Does()
    {
        Assert.Equal("Show all icons on the bar", TrayChevronMenu.LabelOf(TrayChevronMenu.Item.ShowAll));
        Assert.Equal("Put icons back in the menu", TrayChevronMenu.LabelOf(TrayChevronMenu.Item.PutAllBack));
    }

    // ------------------------------------------------------------------ the settings list

    private static TrayIconRow.Running Running(string id, string name) => new(id, name, Is(id), null);

    [Fact]
    public void The_List_Shows_Chosen_Icons_In_Bar_Order_Then_The_Rest_By_Name()
    {
        TrayChoice choice = Seeded("c", "a");

        IReadOnlyList<TrayIconRow> rows = TrayIconRow.For(choice,
            [Running("a", "Alpha"), Running("z", "Zulu"), Running("c", "Charlie"), Running("b", "Bravo")]);

        Assert.Equal(["Charlie", "Alpha", "Bravo", "Zulu"], rows.Select(r => r.Name));
        Assert.Equal([true, true, false, false], rows.Select(r => r.OnBar));
    }

    /// <summary>Otherwise an icon chosen once could not be taken off the bar without starting its app.</summary>
    [Fact]
    public void A_Chosen_Icon_That_Is_Not_Running_Is_Still_Listed()
    {
        TrayChoice choice = Seeded(@"C:\Apps\Sync.exe:7:Sync — up to date");

        TrayIconRow row = Assert.Single(TrayIconRow.For(choice, []));

        Assert.False(row.IsRunning);
        Assert.True(row.OnBar);
        Assert.Equal("Sync — up to date (not running)", row.Name);
        Assert.True(row.Matches(@"C:\Apps\Sync.exe:7:Sync — up to date"));
    }

    [Theory]
    [InlineData(@"C:\Apps\Sync.exe:7:", "Sync (not running)")]
    [InlineData(@"C:\Apps\Sync.exe:7:Title: with colons", "Title: with colons (not running)")]
    [InlineData("7820ae73-23e3-4229-82c1-e41cb67d5b9c", "An icon that is not running")]
    public void An_Absent_Icon_Is_Named_From_Its_Identifier(string identifier, string name) =>
        Assert.Equal(name, TrayIconRow.NameOfAbsent(identifier));

    [Fact]
    public void Ticking_A_Box_Puts_The_Icon_On_The_Bar_And_Unticking_Takes_It_Off() => OnSta(() =>
    {
        TrayChoice choice = new(_path);
        SettingsWindow window = Shown(choice, () => TrayIconRow.For(choice, [Running("a", "Alpha")]));
        try
        {
            CheckBox box = Assert.IsType<CheckBox>(Assert.Single(window.TrayIconList.Children));

            box.IsChecked = true;
            Assert.Equal(["a"], choice.OnBar);

            box.IsChecked = false;
            Assert.Empty(choice.OnBar);

            // The page's own change does not rebuild it, so the box keeps its place and focus.
            Assert.Same(box, window.TrayIconList.Children[0]);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void A_Drag_On_The_Bar_Shows_In_An_Open_Window() => OnSta(() =>
    {
        TrayChoice choice = new(_path);
        SettingsWindow window = Shown(choice, () => TrayIconRow.For(choice, [Running("a", "Alpha")]));
        try
        {
            choice.Add("a", Is("a"));
            choice.ShowAllOnBar();

            CheckBox box = Assert.IsType<CheckBox>(Assert.Single(window.TrayIconList.Children));
            Assert.True(box.IsChecked);
            Assert.True(window.ShowAllTrayBox.IsChecked);
            Assert.True(window.TrayIconList.IsEnabled);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Under_Show_All_Every_Running_Icon_Is_Listed_As_On_The_Bar()
    {
        TrayChoice choice = Seeded("a");
        choice.ShowAllOnBar();

        IReadOnlyList<TrayIconRow> rows = TrayIconRow.For(choice, [Running("a", "Alpha"), Running("b", "Bravo")]);

        Assert.All(rows, row => Assert.True(row.OnBar));
    }

    /// <summary>
    /// Under "show all" the list used to be greyed out, so no single icon could be taken off.
    /// Unticking one now does what a drop on the chevron does.
    /// </summary>
    [Fact]
    public void Under_Show_All_Unticking_An_Icon_Takes_Only_That_One_Off() => OnSta(() =>
    {
        TrayChoice choice = new(_path);
        choice.ShowAllOnBar();
        TrayIconRow.Running[] running = [Running("a", "Alpha"), Running("b", "Bravo"), Running("c", "Charlie")];
        SettingsWindow window = Shown(choice, () => TrayIconRow.For(choice, running), () => Row("c", "a", "b"));
        try
        {
            Assert.True(window.TrayIconList.IsEnabled);
            CheckBox[] boxes = [.. window.TrayIconList.Children.Cast<CheckBox>()];
            Assert.All(boxes, box => Assert.True(box.IsChecked));

            // Alpha, Bravo, Charlie by name; Bravo is the second.
            boxes[1].IsChecked = false;

            Assert.False(choice.ShowAll);
            Assert.Equal(["c", "a"], choice.OnBar);
            Assert.False(window.ShowAllTrayBox.IsChecked);
            Assert.Same(boxes[1], window.TrayIconList.Children[1]);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Unticking_Show_Every_Icon_Puts_Every_Icon_Back() => OnSta(() =>
    {
        TrayChoice choice = new(_path);
        choice.ShowAllOnBar();
        choice.PlaceOnBar("b", Is("b"), Is("a"), Row("a", "b"));
        SettingsWindow window = Shown(choice, () => TrayIconRow.For(choice, [Running("a", "Alpha"), Running("b", "Bravo")]));
        try
        {
            window.ShowAllTrayBox.IsChecked = false;

            Assert.False(choice.ShowAll);
            Assert.Empty(choice.OnBar);
            Assert.All(window.TrayIconList.Children.Cast<CheckBox>(), box => Assert.False(box.IsChecked));

            window.ShowAllTrayBox.IsChecked = true;

            Assert.True(choice.ShowAll);
            Assert.All(window.TrayIconList.Children.Cast<CheckBox>(), box => Assert.True(box.IsChecked));
        }
        finally { window.Close(); }
    });

    /// <summary>
    /// The open window follows a change made on the bar by setting its own "show every icon" box.
    /// Unticking that box puts every icon back, so the window must not take its own setting for a
    /// click: a drop on the chevron that ends "show all" would then empty the bar a moment later.
    /// </summary>
    [Fact]
    public void A_Drop_That_Ends_Show_All_Is_Not_Undone_By_An_Open_Window() => OnSta(() =>
    {
        TrayChoice choice = new(_path);
        choice.ShowAllOnBar();
        SettingsWindow window = Shown(choice, () => TrayIconRow.For(choice, [Running("a", "Alpha"), Running("b", "Bravo")]));
        try
        {
            Assert.True(window.ShowAllTrayBox.IsChecked);

            // As the bar does on a drop on the chevron.
            choice.PutBehindChevron(Is("a"), Row("a", "b"));

            Assert.False(window.ShowAllTrayBox.IsChecked);
            Assert.Equal(["b"], choice.OnBar);
            Assert.Equal(["b"], new TrayChoice(_path).OnBar);
        }
        finally { window.Close(); }
    });

    /// <summary>
    /// The pages share one cell, so a long list on this page would make the whole window taller.
    /// It scrolls in its own box instead.
    /// </summary>
    [Fact]
    public void A_Long_List_Scrolls_Rather_Than_Growing_The_Window() => OnSta(() =>
    {
        TrayChoice choice = new(_path);
        SettingsWindow few = Shown(choice, () => []);
        SettingsWindow many = Shown(choice, () => TrayIconRow.For(choice,
            [.. Enumerable.Range(0, 40).Select(i => Running($"id{i}", $"Icon {i}"))]));
        try
        {
            few.MaxHeight = many.MaxHeight = double.PositiveInfinity;
            few.UpdateLayout();
            many.UpdateLayout();

            Assert.Equal(few.ActualHeight, many.ActualHeight, 3);

            many.ShowPage(many.PageElements.Count - 1);
            many.UpdateLayout();
            Assert.True(many.TrayIconScroller.ScrollableHeight > 0);
        }
        finally
        {
            few.Close();
            many.Close();
        }
    });

    private static SettingsWindow Shown(
        TrayChoice choice,
        Func<IReadOnlyList<TrayIconRow>> rows,
        Func<IReadOnlyList<TrayEntry>>? drawn = null)
    {
        var window = new SettingsWindow(new UmbraSettings(), _ => { }, () => { })
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30000,
            Top = -30000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        window.AttachTrayChoice(choice, rows, drawn);
        window.Show();
        return window;
    }

    private static void OnSta(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception e) { failure = ExceptionDispatchInfo.Capture(e); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}

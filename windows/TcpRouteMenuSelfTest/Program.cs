using System.Drawing;
using Forms = System.Windows.Forms;
using TcpRouteTray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        VerifyOriginalFailure();
        foreach (var paginated in new[] { false, true })
        {
            VerifyRepeatedSelection(paginated);
            VerifyAsyncSelection(paginated);
            foreach (var reason in new[] {
                Forms.ToolStripDropDownCloseReason.Keyboard,
                Forms.ToolStripDropDownCloseReason.AppClicked,
                Forms.ToolStripDropDownCloseReason.AppFocusChange,
                Forms.ToolStripDropDownCloseReason.CloseCalled
            }) VerifyDismissal(paginated, reason);
            VerifyOutsideClick(paginated, rightButton: false);
            VerifyOutsideClick(paginated, rightButton: true);
            VerifyCommand(paginated);
        }
        Console.WriteLine("All Windows tray menu interaction tests passed.");
    }

    private static void VerifyOriginalFailure()
    {
        using var menus = new Menus(paginated: false, fixedMenu: false);
        menus.First.PerformClick();
        Expect(!menus.Root.Visible, "Control case did not reproduce the original WinForms dropdown closure.");
    }

    private static void VerifyRepeatedSelection(bool paginated)
    {
        using var menus = new Menus(paginated);
        var clicks = 0;
        menus.First.Click += (_, _) => { menus.First.Checked = !menus.First.Checked; clicks++; };
        menus.Second.Click += (_, _) => { menus.Second.Checked = !menus.Second.Checked; clicks++; };
        for (var index = 0; index < 4; index++)
        {
            menus.First.PerformClick();
            Expect(menus.First.Checked == (index % 2 == 0), "Repeated selection did not toggle the current check state.");
            menus.ExpectOpen();
        }
        menus.Second.PerformClick();
        menus.ExpectOpen();
        Expect(clicks == 5 && menus.Second.Checked, "Selecting another node failed.");
        Console.WriteLine($"Repeated selection passed (paginated={paginated}).");
    }

    private static void VerifyAsyncSelection(bool paginated)
    {
        using var menus = new Menus(paginated);
        var save = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        menus.First.Click += async (_, _) => { await save.Task; menus.First.Checked = !menus.First.Checked; };
        menus.First.PerformClick();
        menus.ExpectOpen();
        Expect(!menus.First.Checked, "Async selection changed before saving completed.");
        save.SetResult();
        PumpUntil(() => menus.First.Checked);
        menus.ExpectOpen();
        menus.First.PerformClick();
        PumpUntil(() => !menus.First.Checked);
        menus.ExpectOpen();
    }

    private static void VerifyDismissal(bool paginated, Forms.ToolStripDropDownCloseReason reason)
    {
        using var menus = new Menus(paginated);
        menus.First.PerformClick();
        menus.ExpectOpen();
        if (reason == Forms.ToolStripDropDownCloseReason.Keyboard)
        {
            var key = Forms.Message.Create(menus.Leaf.Handle, 0x0100, (IntPtr)Forms.Keys.Escape, IntPtr.Zero);
            Expect(menus.Leaf.PreProcessMessage(ref key) && !menus.Leaf.Visible, "Esc dismissal was blocked after node selection.");
        }
        else
        {
            menus.Root.Close(reason);
            Expect(!menus.Root.Visible && !menus.Leaf.Visible, $"Dismissal was blocked: {reason}.");
        }
    }

    private static void VerifyCommand(bool paginated)
    {
        using var menus = new Menus(paginated);
        var invoked = false;
        menus.Command.Click += (_, _) => invoked = true;
        menus.First.PerformClick();
        menus.ExpectOpen();
        menus.Command.PerformClick();
        Expect(invoked && !menus.Root.Visible, "Normal commands no longer close the menu chain.");
    }

    private static void VerifyOutsideClick(bool paginated, bool rightButton)
    {
        using var menus = new Menus(paginated);
        menus.First.PerformClick();
        menus.ExpectOpen();
        menus.ClickOutside(rightButton);
        Expect(!menus.Root.Visible && !menus.Leaf.Visible, $"Outside mouse click did not dismiss editing (rightButton={rightButton}).");
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Forms.Application.DoEvents();
            Thread.Sleep(10);
        }
        Expect(condition(), "Async selection did not complete.");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Menus : IDisposable
    {
        private readonly Forms.Form _owner = new() { ShowInTaskbar = false, StartPosition = Forms.FormStartPosition.Manual, Location = new Point(50, 50), Size = new Size(320, 240) };
        public Forms.ContextMenuStrip Root { get; } = new();
        private readonly Forms.ToolStripMenuItem _subscriptions = new("Subscriptions");
        private readonly Forms.ToolStripMenuItem _source = new("Source");
        private readonly Forms.ToolStripMenuItem? _page;
        public Forms.ToolStripDropDown Leaf { get; }
        public SubscriptionNodeMenuItem First { get; } = new("Node A");
        public SubscriptionNodeMenuItem Second { get; } = new("Node B");
        public Forms.ToolStripMenuItem Command { get; } = new("Update now");

        public Menus(bool paginated, bool fixedMenu = true)
        {
            _source.DropDown = fixedMenu ? new SubscriptionNodeMenu() : new Forms.ToolStripDropDownMenu();
            Root.Items.Add(_subscriptions);
            _subscriptions.DropDownItems.Add(_source);
            if (paginated)
            {
                _page = new Forms.ToolStripMenuItem("1-50") { DropDown = new SubscriptionNodeMenu() };
                _source.DropDownItems.Add(_page);
            }
            Leaf = _page?.DropDown ?? _source.DropDown;
            Leaf.Items.AddRange([First, Second, Command]);
            _owner.Show();
            Forms.Application.DoEvents();
            Root.Show(_owner, new Point(10, 10));
            _subscriptions.ShowDropDown();
            _source.ShowDropDown();
            _page?.ShowDropDown();
            Forms.Application.DoEvents();
            ExpectOpen();
        }

        public void ExpectOpen() => Expect(Root.Visible && _subscriptions.DropDown.Visible && _source.DropDown.Visible && Leaf.Visible,
            "Node click closed part of the menu chain.");

        public void ClickOutside(bool rightButton)
        {
            var click = Forms.Message.Create(_owner.Handle, rightButton ? 0x0204 : 0x0201, IntPtr.Zero, (IntPtr)((200 << 16) | 5));
            Forms.Application.FilterMessage(ref click);
        }

        public void Dispose()
        {
            Root.Close();
            Root.Dispose();
            _owner.Dispose();
        }
    }
}

using Forms = System.Windows.Forms;

namespace TcpRouteTray;

internal sealed class SubscriptionNodeMenuItem : Forms.ToolStripMenuItem
{
    public SubscriptionNodeMenuItem(string label) : base(label) { }
    protected override bool DismissWhenClicked => false;
}

internal sealed class SubscriptionNodeMenu : Forms.ToolStripDropDownMenu
{
    private bool _handlingNodeClick;

    protected override void OnItemClicked(Forms.ToolStripItemClickedEventArgs e)
    {
        var previous = _handlingNodeClick;
        _handlingNodeClick = e.ClickedItem is SubscriptionNodeMenuItem;
        try { base.OnItemClicked(e); }
        finally { _handlingNodeClick = previous; }
    }

    protected override void OnClosing(Forms.ToolStripDropDownClosingEventArgs e)
    {
        // WinForms closes the dropdown independently of DismissWhenClicked.
        if (_handlingNodeClick && e.CloseReason == Forms.ToolStripDropDownCloseReason.ItemClicked)
            e.Cancel = true;
        base.OnClosing(e);
    }
}

using System.ComponentModel;

namespace HusayniaSMS.WinForms.Forms;

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class RecipientDataGridView : DataGridView
{
    private int _selectionNotificationSuppressionDepth;

    public RecipientDataGridView()
    {
    }

    protected override void OnMouseDown(MouseEventArgs e) =>
        PreserveSelectionForCheckboxInput(e.Button == MouseButtons.Left, e.Location, () =>
            base.OnMouseDown(e));

    protected override void OnMouseUp(MouseEventArgs e) =>
        PreserveSelectionForCheckboxInput(e.Button == MouseButtons.Left, e.Location, () =>
            base.OnMouseUp(e));

    protected override bool ProcessDataGridViewKey(KeyEventArgs e)
    {
        var result = false;
        PreserveSelectionForCheckboxInput(
            e.KeyCode == Keys.Space && CurrentCell is DataGridViewCheckBoxCell,
            null,
            () => result = base.ProcessDataGridViewKey(e));
        return result;
    }

    protected override void OnSelectionChanged(EventArgs e)
    {
        if (_selectionNotificationSuppressionDepth == 0)
        {
            base.OnSelectionChanged(e);
        }
    }

    private void PreserveSelectionForCheckboxInput(
        bool possibleCheckboxInput,
        Point? location,
        Action action)
    {
        if (!possibleCheckboxInput || !IsCheckboxCell(location))
        {
            action();
            return;
        }

        var selectedRowIndexes = SelectedRows.Cast<DataGridViewRow>()
            .Select(row => row.Index)
            .ToArray();
        _selectionNotificationSuppressionDepth++;
        try
        {
            action();
        }
        finally
        {
            try
            {
                ClearSelection();
                foreach (var rowIndex in selectedRowIndexes)
                {
                    if (rowIndex >= 0 && rowIndex < Rows.Count)
                    {
                        Rows[rowIndex].Selected = true;
                    }
                }
            }
            finally
            {
                _selectionNotificationSuppressionDepth--;
            }
        }
    }

    private bool IsCheckboxCell(Point? location)
    {
        if (location is null)
        {
            return CurrentCell is DataGridViewCheckBoxCell;
        }

        var hit = HitTest(location.Value.X, location.Value.Y);
        return hit.RowIndex >= 0 &&
            hit.ColumnIndex >= 0 &&
            Columns[hit.ColumnIndex] is DataGridViewCheckBoxColumn;
    }
}

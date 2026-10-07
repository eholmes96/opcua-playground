using System.Windows;
using OpcUaPlayground.Services;

namespace OpcUaPlayground.Dialogs;

/// <summary>What a three-way confirmation came back with.</summary>
public enum DarkDialogResult
{
    Yes,
    No,
    Cancel,
}

/// <summary>
/// Dark-styled replacement for MessageBox.Show, which is a native dialog that
/// ignores WPF styling. The only dialog used anywhere in the app.
/// </summary>
public partial class DarkMessageBox : Window
{
    private DarkDialogResult _result = DarkDialogResult.Cancel;

    public DarkMessageBox()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
    }

    public static void Show(Window? owner, string message)
    {
        var dialog = Create(owner, message);
        dialog.ShowDialog();
    }

    /// <summary>
    /// Asks a question with two or three answers. Three when the two alternatives are both things
    /// to do and backing out is a separate choice, as in overwriting a recording versus saving it
    /// under another name. Two when <paramref name="no"/> IS backing out; passing a cancel label
    /// then would put two ways to say no side by side.
    ///
    /// Closing the window with the X always counts as Cancel, so the destructive path needs a
    /// deliberate click.
    /// </summary>
    public static DarkDialogResult Confirm(
        Window? owner, string message, string yes, string no, string? cancel = "Cancel")
    {
        var dialog = Create(owner, message);
        dialog.OkButton.Visibility = Visibility.Collapsed;
        dialog.OkButton.IsDefault = false;
        dialog.OkButton.IsCancel = false;

        dialog.YesButton.Content = yes;
        dialog.YesButton.Visibility = Visibility.Visible;
        dialog.YesButton.IsDefault = true;

        dialog.NoButton.Content = no;
        dialog.NoButton.Visibility = Visibility.Visible;

        if (cancel is null)
        {
            // Escape and the X have to land somewhere, and here that somewhere is No.
            dialog.CancelButton.IsCancel = false;
            dialog.NoButton.IsCancel = true;
        }
        else
        {
            dialog.CancelButton.Content = cancel;
            dialog.CancelButton.Visibility = Visibility.Visible;
        }

        dialog.ShowDialog();
        return dialog._result;
    }

    private static DarkMessageBox Create(Window? owner, string message)
    {
        var dialog = new DarkMessageBox();
        if (owner is not null)
            dialog.Owner = owner;
        else
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.MessageTextBlock.Text = message;
        return dialog;
    }

    private void YesButton_Click(object sender, RoutedEventArgs e) => Finish(DarkDialogResult.Yes);

    private void NoButton_Click(object sender, RoutedEventArgs e) => Finish(DarkDialogResult.No);

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Finish(DarkDialogResult.Cancel);

    private void Finish(DarkDialogResult result)
    {
        _result = result;
        Close();
    }
}

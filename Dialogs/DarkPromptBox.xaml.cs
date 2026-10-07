using System.Windows;
using OpcUaPlayground.Services;

namespace OpcUaPlayground.Dialogs;

/// <summary>
/// Asks for one line of text. Used for renaming a recording, which is the only thing in the app
/// that needs the user to type into a dialog rather than into the window.
///
/// The caller supplies the validation, because what makes a name acceptable belongs with the thing
/// being named rather than in here. A rejected name keeps the dialog open with the reason shown,
/// instead of closing and making the user start again.
/// </summary>
public partial class DarkPromptBox : Window
{
    private Func<string, string?>? _validate;

    public DarkPromptBox()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
    }

    /// <summary>
    /// Returns the accepted text, or null when the user cancelled.
    /// <paramref name="validate"/> returns null to accept, or the reason to refuse.
    /// </summary>
    public static string? Ask(
        Window? owner, string message, string initial, Func<string, string?>? validate = null)
    {
        var dialog = new DarkPromptBox { _validate = validate };
        if (owner is not null)
            dialog.Owner = owner;
        else
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        dialog.MessageTextBlock.Text = message;
        dialog.InputTextBox.Text = initial;
        dialog.Loaded += (_, _) =>
        {
            dialog.InputTextBox.Focus();
            dialog.InputTextBox.SelectAll();
        };

        return dialog.ShowDialog() == true ? dialog.InputTextBox.Text.Trim() : null;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        string typed = InputTextBox.Text.Trim();

        if (_validate?.Invoke(typed) is { } problem)
        {
            ProblemTextBlock.Text = problem;
            ProblemTextBlock.Visibility = Visibility.Visible;
            InputTextBox.Focus();
            InputTextBox.SelectAll();
            return;
        }

        DialogResult = true;
    }
}

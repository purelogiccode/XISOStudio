using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Media;
using XISOStudio.Models;

namespace XISOStudio.Dialogs;

/// <summary>
/// A cross-platform modal message box dialog replacing <c>System.Windows.MessageBox</c>,
/// with an icon, a title, a message, and a configurable set of buttons.
/// </summary>
public partial class MessageBoxWindow : Window
{
    private readonly TaskCompletionSource<UiMessageBoxResult> _result = new();

    /// <summary>Result returned when the dialog is closed without clicking a button.</summary>
    private readonly UiMessageBoxResult _defaultResult = UiMessageBoxResult.Ok;

    /// <summary>Completion source used when the dialog is shown without an owner window.</summary>
    public Task<UiMessageBoxResult> Result => _result.Task;

    /// <summary>
    /// Initializes a new instance of the <see cref="MessageBoxWindow"/> class with no content;
    /// use the content constructor to configure the dialog.
    /// </summary>
    // Kept public: Avalonia's XAML previewer instantiates previewed windows through
    // their parameterless constructor.
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global",
        Justification = "Avalonia's XAML previewer instantiates the window via its parameterless constructor.")]
    public MessageBoxWindow()
    {
        InitializeComponent();

        // Closing via the window chrome must not silently choose the affirmative action:
        // complete with the default result for the offered buttons instead.
        Closed += (_, _) => _result.TrySetResult(_defaultResult);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MessageBoxWindow"/> class with the given
    /// content and button layout.
    /// </summary>
    /// <param name="message">The message text to display.</param>
    /// <param name="title">The window title.</param>
    /// <param name="buttons">The buttons offered to the user.</param>
    /// <param name="icon">The icon shown in the message box.</param>
    public MessageBoxWindow(string message, string title, UiMessageBoxButton buttons, UiMessageBoxImage icon)
        : this()
    {
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;

        (IconText.Text, IconText.Foreground) = icon switch
        {
            UiMessageBoxImage.Warning => ("⚠", new SolidColorBrush(Color.Parse("#ffa726"))),
            UiMessageBoxImage.Error => ("⛔", new SolidColorBrush(Color.Parse("#f85149"))),
            _ => ("ℹ", new SolidColorBrush(Color.Parse("#00bcd4")))
        };

        YesButton.IsVisible = buttons is UiMessageBoxButton.YesNo or UiMessageBoxButton.YesNoCancel;
        NoButton.IsVisible = buttons is UiMessageBoxButton.YesNo or UiMessageBoxButton.YesNoCancel;
        CancelButton.IsVisible = buttons is UiMessageBoxButton.YesNoCancel;
        OkButton.IsVisible = buttons is UiMessageBoxButton.Ok;

        // Closing the dialog must never imply consent: fall back to the safest offered answer.
        _defaultResult = buttons switch
        {
            UiMessageBoxButton.YesNo => UiMessageBoxResult.No,
            UiMessageBoxButton.YesNoCancel => UiMessageBoxResult.Cancel,
            _ => UiMessageBoxResult.Ok
        };

        if (buttons == UiMessageBoxButton.Ok)
        {
            OkButton.Focus();
        }
        else
        {
            YesButton.Focus();
        }
    }

    private void YesButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Complete(UiMessageBoxResult.Yes);
    }

    private void NoButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Complete(UiMessageBoxResult.No);
    }

    private void CancelButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Complete(UiMessageBoxResult.Cancel);
    }

    private void OkButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Complete(UiMessageBoxResult.Ok);
    }

    private void Complete(UiMessageBoxResult result)
    {
        _result.TrySetResult(result);
        Close(result);
    }
}
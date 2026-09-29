using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace MsgBox;

internal class MessageBox : Window
{
    public enum MessageBoxButtons
    {
        Ok,
        OkCancel,
        YesNo,
        YesNoCancel
    }

    public enum MessageBoxResult
    {
        Ok,
        Cancel,
        Yes,
        No
    }

    private readonly TextBlock _text;
    private readonly StackPanel _buttons;

    public MessageBox()
    {
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var outer = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };

        _text = new TextBlock
        {
            Name = "Text",
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(10),
            TextWrapping = TextWrapping.Wrap
        };
        outer.Children.Add(_text);

        _buttons = new StackPanel
        {
            Name = "Buttons",
            HorizontalAlignment = HorizontalAlignment.Center,
            Orientation = Orientation.Horizontal
        };
        outer.Children.Add(_buttons);

        Content = outer;
    }

    public static Task<MessageBoxResult> Show(
        Window? parent, string text, string title, MessageBoxButtons buttons)
    {
        return Dispatcher.UIThread.InvokeAsync(async () =>
        {
            Window owner = parent ?? GetOwnerWindowOrThrow();
            var msgbox = new MessageBox { Title = title };
            msgbox._text.Text = text;

            MessageBoxResult res = MessageBoxResult.Ok;

            if (buttons is MessageBoxButtons.Ok or MessageBoxButtons.OkCancel)
                AddButton("Ok", MessageBoxResult.Ok, def: true);

            if (buttons is MessageBoxButtons.YesNo or MessageBoxButtons.YesNoCancel)
            {
                AddButton("Yes", MessageBoxResult.Yes);
                AddButton("No", MessageBoxResult.No, def: true);
            }

            if (buttons is MessageBoxButtons.OkCancel or MessageBoxButtons.YesNoCancel)
                AddButton("Cancel", MessageBoxResult.Cancel, def: true);

            await msgbox.ShowDialog(owner);
            return res;

            void AddButton(string caption, MessageBoxResult r, bool def = false)
            {
                var btn = new Button
                {
                    Content = caption,
                    Margin = new Thickness(5),
                    Padding = new Thickness(5)
                };
                btn.Click += (_, _) =>
                {
                    res = r;
                    msgbox.Close();
                };
                msgbox._buttons.Children.Add(btn);
                if (def) res = r;
            }
        });
    }

    public static Task<MessageBoxResult> Show(
        Window? parent, string text, string? note, string title, MessageBoxButtons buttons)
    {
        return Dispatcher.UIThread.InvokeAsync(async () =>
        {
            Window owner = parent ?? GetOwnerWindowOrThrow();
            var msgbox = new MessageBox { Title = title };

            msgbox._text.Inlines?.Clear();
            msgbox._text.Inlines!.Add(new Run(text));
            if (!string.IsNullOrWhiteSpace(note))
            {
                msgbox._text.Inlines.Add(new LineBreak());
                msgbox._text.Inlines.Add(new LineBreak());
                msgbox._text.Inlines.Add(new Run(note.Trim())
                {
                    FontStyle = FontStyle.Italic,
                    FontSize = Math.Max(10.0, msgbox._text.FontSize - 2.0)
                });
            }

            MessageBoxResult res = MessageBoxResult.Ok;

            if (buttons is MessageBoxButtons.Ok or MessageBoxButtons.OkCancel)
                AddButton("Ok", MessageBoxResult.Ok, def: true);

            if (buttons is MessageBoxButtons.YesNo or MessageBoxButtons.YesNoCancel)
            {
                AddButton("Yes", MessageBoxResult.Yes);
                AddButton("No", MessageBoxResult.No, def: true);
            }

            if (buttons is MessageBoxButtons.OkCancel or MessageBoxButtons.YesNoCancel)
                AddButton("Cancel", MessageBoxResult.Cancel, def: true);

            await msgbox.ShowDialog(owner);
            return res;

            void AddButton(string caption, MessageBoxResult r, bool def = false)
            {
                var btn = new Button
                {
                    Content = caption,
                    Margin = new Thickness(5),
                    Padding = new Thickness(5)
                };
                btn.Click += (_, _) =>
                {
                    res = r;
                    msgbox.Close();
                };
                msgbox._buttons.Children.Add(btn);
                if (def) res = r;
            }
        });
    }

    /// <summary>A dialog with custom buttons. Returns the index of the button
    /// pressed; <paramref name="defaultIndex"/> is focused and answers Enter,
    /// <paramref name="cancelIndex"/> answers Escape and closing the window.</summary>
    public static Task<int> ShowChoice(
        Window? parent, string text, string title, string[] buttons, int defaultIndex, int cancelIndex)
    {
        return Dispatcher.UIThread.InvokeAsync(async () =>
        {
            Window owner = parent ?? GetOwnerWindowOrThrow();
            var msgbox = new MessageBox { Title = title };
            msgbox._text.Text = text;
            msgbox._text.MaxWidth = 560;

            int result = cancelIndex;
            Button? focus = null;
            for (int i = 0; i < buttons.Length; i++)
            {
                int index = i;
                var btn = new Button
                {
                    Content = buttons[i],
                    Margin = new Thickness(5),
                    Padding = new Thickness(5),
                    IsDefault = i == defaultIndex,
                    IsCancel = i == cancelIndex,
                };
                if (i == defaultIndex)
                {
                    btn.FontWeight = FontWeight.SemiBold;
                    focus = btn;
                }
                btn.Click += (_, _) =>
                {
                    result = index;
                    msgbox.Close();
                };
                msgbox._buttons.Children.Add(btn);
            }
            msgbox.Opened += (_, _) => focus?.Focus();

            await msgbox.ShowDialog(owner);
            return result;
        });
    }

    private static Window GetOwnerWindowOrThrow()
    {
        if (Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime { MainWindow: not null } desktop)
        {
            return desktop.MainWindow;
        }
        throw new InvalidOperationException(
            "MessageBox.Show requires an owner window (parent).");
    }
}

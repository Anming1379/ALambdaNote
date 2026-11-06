using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ALambdaNote.Views;

public enum DialogChoice
{
    None,
    Save,
    DontSave,
    Cancel,
    Ok,
    Yes,
    No
}

/// <summary>直角、平面的确认与提示弹窗。</summary>
public partial class FlatDialog : Window
{
    private DialogChoice _result = DialogChoice.None;

    private FlatDialog()
    {
        InitializeComponent();
    }

    public static DialogChoice AskSave(Window? owner)
    {
        return Present(
            owner,
            "保存修改",
            "这份笔记还有未保存的修改，要保存吗？",
            new[]
            {
                ("保存", DialogChoice.Save, true),
                ("不保存", DialogChoice.DontSave, false),
                ("取消", DialogChoice.Cancel, false)
            });
    }

    public static void Info(Window? owner, string title, string message)
    {
        Present(owner, title, message, new[] { ("知道了", DialogChoice.Ok, true) });
    }

    public static DialogChoice Present(
        Window? owner,
        string title,
        string message,
        IReadOnlyList<(string Text, DialogChoice Choice, bool Primary)> buttons)
    {
        var dialog = new FlatDialog();
        if (owner is not null && owner.IsVisible)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dialog.Title = title;
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;

        Button? primaryButton = null;

        foreach (var (text, choice, primary) in buttons)
        {
            var button = new Button
            {
                Content = text,
                Style = (Style)dialog.FindResource(primary ? "PrimaryButton" : "FlatButton"),
                Margin = new Thickness(8, 0, 0, 0)
            };

            button.Click += (_, _) =>
            {
                dialog._result = choice;
                dialog.Close();
            };

            if (primary)
            {
                primaryButton = button;
            }

            dialog.ButtonsPanel.Children.Add(button);
        }

        dialog.Loaded += (_, _) => primaryButton?.Focus();

        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                dialog._result = DialogChoice.Cancel;
                dialog.Close();
            }
        };

        dialog.ShowDialog();
        return dialog._result;
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch
            {
                // 忽略拖动异常。
            }
        }
    }
}

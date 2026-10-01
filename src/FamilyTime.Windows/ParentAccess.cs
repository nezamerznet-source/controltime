using System.Windows;
using System.Windows.Controls;

namespace FamilyTime.Windows;

public sealed class ParentAccess(ActivityStore store, Func<AppSettings> settings)
{
    private PasswordPrompt? prompt;
    public bool Required => settings().ParentPasswordHash.Length > 0;
    public bool Request(Window? owner, string action)
    {
        if (!Required) return true;
        if (prompt is not null) { prompt.Activate(); return false; }
        prompt = new PasswordPrompt(action, Check);
        if (owner?.IsVisible == true) prompt.Owner = owner;
        try { return prompt.ShowDialog() == true; }
        finally { prompt = null; }
    }
    internal string? Check(string password)
    {
        long.TryParse(store.GetMeta("parent-lock-until"), out long until);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (until > now) return $"Слишком много попыток. Подождите {until - now} сек.";
        if (ParentPassword.Verify(password, settings().ParentPasswordHash))
        {
            store.DeleteMeta("parent-failures"); store.DeleteMeta("parent-lock-until"); return null;
        }
        int.TryParse(store.GetMeta("parent-failures"), out int failures);
        failures = Math.Clamp(failures, 0, 4) + 1;
        if (failures >= 5)
        {
            store.SetMeta("parent-lock-until", (now + 30).ToString()); store.SetMeta("parent-failures", "0");
            return "Пять неверных попыток. Повторите через 30 секунд.";
        }
        store.SetMeta("parent-failures", failures.ToString());
        return "Неверный пароль. Учёт времени продолжает работать.";
    }
}

internal sealed class PasswordPrompt : Window
{
    public PasswordPrompt(string action, Func<string, string?> check)
    {
        Title = "Родительский пароль — Family Time"; Width = 460; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = action, FontSize = 20, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        panel.Children.Add(new HelpTip { Text = "Введите родительский пароль. Отмена ничего не изменит: учёт останется включённым. Пароль не отправляется в Telegram." });
        var password = new PasswordBox { MaxLength = 128, Margin = new Thickness(0, 12, 0, 12) };
        panel.Children.Add(password);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Firebrick };
        panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true };
        var submit = new Button { Content = "Подтвердить", IsDefault = true };
        submit.Click += (_, _) =>
        {
            try
            {
                string? message = check(password.Password); password.Clear();
                if (message is null) DialogResult = true;
                else { error.Text = message; password.Focus(); }
            }
            catch { error.Text = "Не удалось проверить пароль. Настройки не изменены."; }
        };
        buttons.Children.Add(cancel); buttons.Children.Add(submit); panel.Children.Add(buttons); Content = panel;
        Loaded += (_, _) => password.Focus();
    }
}

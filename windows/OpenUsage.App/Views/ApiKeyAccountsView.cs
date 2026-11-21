using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using OpenUsage.Core;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace OpenUsage.App.Views;

internal sealed class ApiKeyAccountsView : StackPanel
{
    private readonly AppContainer _container;
    private readonly string _providerId;
    private readonly StackPanel _list = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
    private readonly TextBox _name = new() { MaxLength = 80, Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(6) };
    private readonly PasswordBox _password = new() { Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(6) };

    public ApiKeyAccountsView(AppContainer container, string providerId)
    {
        _container = container;
        _providerId = providerId;
        Children.Add(_list);
        AddField("Account Name", _name);
        AddField("New API Key", _password);
        var add = ActionButton("Connect Account");
        var status = _status;
        add.Click += async (_, _) =>
        {
            add.IsEnabled = false;
            _name.IsEnabled = false;
            _password.IsEnabled = false;
            status.Text = "Checking the API key…";
            try
            {
                await _container.ConnectAccountAsync(_providerId, _name.Text, _password.Password);
                _name.Clear();
                _password.Clear();
                RefreshAccounts();
                status.Text = "Account connected.";
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
            finally
            {
                add.IsEnabled = true;
                _name.IsEnabled = true;
                _password.IsEnabled = true;
            }
        };
        Children.Add(add);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        Children.Add(_status);
        RefreshAccounts();
    }

    private void AddField(string label, FrameworkElement field)
    {
        Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 3) });
        AutomationProperties.SetName(field, label);
        Children.Add(field);
    }

    private Button ActionButton(string label) => new()
    {
        Content = label,
        HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
        Style = (Style)FindResource("GhostButton")
    };

    private void RefreshAccounts()
    {
        _list.Children.Clear();
        try
        {
            foreach (var account in _container.ApiKeyAccountStore.List().Where(a => a.ProviderId == _providerId))
                _list.Children.Add(AccountRow(account));
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private StackPanel AccountRow(ApiKeyAccount account)
    {
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var name = new TextBox { Text = account.Label, MaxLength = 80, Padding = new Thickness(6) };
        AutomationProperties.SetName(name, $"Name for {account.Label}");
        row.Children.Add(name);
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        var rename = ActionButton("Rename");
        var remove = ActionButton("Remove");
        rename.Click += (_, _) => Update(() => _container.RenameAccount(account, name.Text), "Account renamed.");
        remove.Click += (_, _) =>
        {
            if (remove.Content is "Remove")
            {
                remove.Content = "Confirm Remove";
                return;
            }
            Update(() => _container.RemoveAccount(account), "Account removed.");
        };
        controls.Children.Add(rename);
        controls.Children.Add(remove);
        row.Children.Add(controls);
        return row;
    }

    private void Update(Action change, string message)
    {
        try
        {
            change();
            RefreshAccounts();
            _status.Text = message;
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ShowError(Exception error)
    {
        _status.Text = error is ArgumentException or ProviderRequestException or InvalidOperationException
            ? error.Message
            : "Could not update accounts. Check the OpenUsage log.";
        AppLog.Warn("accounts", $"account action failed ({error.GetType().Name})");
    }
}

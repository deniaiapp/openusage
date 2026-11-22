using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using OpenUsage.Core;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace OpenUsage.App.Views;

internal sealed class CliProxyHubsView : StackPanel
{
    private readonly AppContainer _container;
    private readonly Style _buttonStyle;
    private readonly StackPanel _hubs = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _addressLabel = new() { Text = "Hub Address" };
    private readonly TextBox _address = new() { Margin = new Thickness(0, 3, 0, 6) };
    private readonly PasswordBox _key = new() { Margin = new Thickness(0, 3, 0, 6), Padding = new Thickness(6) };
    private readonly Button _save;
    private readonly Button _cancel;
    private CliProxyHub? _replace;
    private CancellationTokenSource? _attempt;

    public CliProxyHubsView(AppContainer container, Style buttonStyle)
    {
        _container = container;
        _buttonStyle = buttonStyle;
        Children.Add(new TextBlock
        {
            Text = "Connect a hub to save its management key. Hub accounts don’t appear on the dashboard yet.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
        Children.Add(_hubs);
        AutomationProperties.SetName(_address, "Hub address");
        AutomationProperties.SetName(_key, "Management key");
        Children.Add(_addressLabel);
        Children.Add(_address);
        Children.Add(new TextBlock { Text = "Management Key" });
        Children.Add(_key);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        _save = Action("Connect");
        _cancel = Action("Cancel");
        _save.Click += async (_, _) => await SaveAsync();
        _cancel.Click += (_, _) =>
        {
            if (_attempt is { } attempt) attempt.Cancel();
            else ResetEditor();
        };
        actions.Children.Add(_save);
        actions.Children.Add(_cancel);
        Children.Add(actions);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        Children.Add(_status);
        IsVisibleChanged += (_, _) => { if (!IsVisible) ClearAttempt(); };
        Unloaded += (_, _) => ClearAttempt();
        ResetEditor();
        RefreshHubs();
    }

    private void ClearAttempt()
    {
        _attempt?.Cancel();
        _key.Clear();
    }

    private Button Action(string title) => new()
    {
        Content = title, Style = _buttonStyle, HorizontalAlignment = HorizontalAlignment.Left
    };

    private void RefreshHubs()
    {
        _hubs.Children.Clear();
        try
        {
            foreach (var hub in _container.CliProxyHubs())
            {
                var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
                var address = hub.Address.AbsoluteUri;
                row.Children.Add(new TextBlock { Text = address, TextWrapping = TextWrapping.Wrap });
                var actions = new StackPanel { Orientation = Orientation.Horizontal };
                var replace = Action("Replace Key");
                var remove = Action("Disconnect");
                AutomationProperties.SetName(replace, $"Replace management key for {address}");
                AutomationProperties.SetName(remove, $"Disconnect {address}");
                replace.Click += (_, _) =>
                {
                    _replace = hub;
                    _key.Clear();
                    _addressLabel.Visibility = _address.Visibility = Visibility.Collapsed;
                    _save.Content = "Save Key";
                    _cancel.Visibility = Visibility.Visible;
                    _status.Text = $"Replacing the management key for {address}.";
                    _key.Focus();
                };
                remove.Click += (_, _) =>
                {
                    if (remove.Content is "Disconnect")
                    {
                        remove.Content = "Confirm Disconnect";
                        AutomationProperties.SetName(remove, $"Confirm disconnecting {address}");
                        _status.Text = "Disconnecting removes the saved key from this PC only.";
                        return;
                    }
                    try
                    {
                        var removed = _container.RemoveCliProxyHub(hub.Id);
                        ResetEditor();
                        _status.Text = removed ? "Hub disconnected." : "This hub is no longer connected.";
                        RefreshHubs();
                    }
                    catch (Exception ex) { ShowError(ex); }
                };
                actions.Children.Add(replace);
                actions.Children.Add(remove);
                row.Children.Add(actions);
                _hubs.Children.Add(row);
            }
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ResetEditor()
    {
        _replace = null;
        _key.Clear();
        _status.Text = "";
        _addressLabel.Visibility = _address.Visibility = Visibility.Visible;
        _save.Content = "Connect";
        _cancel.Visibility = Visibility.Collapsed;
    }

    private async Task SaveAsync()
    {
        if (_attempt is not null) return;
        using var attempt = new CancellationTokenSource();
        _attempt = attempt;
        _hubs.IsEnabled = _address.IsEnabled = _key.IsEnabled = _save.IsEnabled = false;
        _cancel.Visibility = Visibility.Visible;
        _status.Text = "Checking the management key…";
        try
        {
            if (_replace is { } hub)
            {
                await _container.ReplaceCliProxyHubKeyAsync(hub.Id, _key.Password, attempt.Token);
                ResetEditor();
                _status.Text = "Management key replaced.";
            }
            else
            {
                await _container.ConnectCliProxyHubAsync(_address.Text, _key.Password, attempt.Token);
                _address.Clear();
                ResetEditor();
                _status.Text = "Hub connected.";
                RefreshHubs();
            }
        }
        catch (OperationCanceledException) { ResetEditor(); }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            _key.Clear();
            _attempt = null;
            _hubs.IsEnabled = _address.IsEnabled = _key.IsEnabled = _save.IsEnabled = true;
            _cancel.Visibility = _replace is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void ShowError(Exception error)
    {
        AppLog.Warn("cliproxyapi", $"hub action failed ({error.GetType().Name})");
        _status.Text = error is ProviderRequestException ? error.Message : "Could not update saved CLIProxyAPI hubs. Check the OpenUsage log.";
    }
}

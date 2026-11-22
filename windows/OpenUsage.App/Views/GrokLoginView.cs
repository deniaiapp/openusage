using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using OpenUsage.Core;
using OpenUsage.Core.Providers.Grok;
using OpenUsage.Core.Support;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace OpenUsage.App.Views;

internal sealed class GrokLoginView : StackPanel
{
    private readonly AppContainer _container;
    private readonly GrokProvider _grok;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) };
    private readonly TextBox _code = new() { IsReadOnly = true, Visibility = Visibility.Collapsed, Padding = new Thickness(6) };
    private readonly Button _signIn;
    private readonly Button _openGrok;
    private readonly Button _cancel;
    private CancellationTokenSource? _attempt;
    private Uri? _verificationPage;
    private bool _returnFromBrowser;

    public GrokLoginView(AppContainer container, GrokProvider grok, Style buttonStyle)
    {
        _container = container;
        _grok = grok;
        Margin = new Thickness(0, 0, 0, 10);
        _signIn = new Button { Content = SignInLabel, Style = buttonStyle };
        _openGrok = new Button { Content = "Open xAI", Style = buttonStyle, Visibility = Visibility.Collapsed };
        _cancel = new Button { Content = "Cancel", Style = buttonStyle, Visibility = Visibility.Collapsed };
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(_code, "Grok device code");
        Children.Add(_status);
        Children.Add(_code);
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(_signIn);
        controls.Children.Add(_openGrok);
        controls.Children.Add(_cancel);
        Children.Add(controls);

        _status.Text = _grok.HasAppCredential ? "Grok is connected to OpenUsage." : "Connect your Grok account to read its usage.";
        _signIn.Click += async (_, _) => await SignInAsync();
        _openGrok.Click += (_, _) =>
        {
            if (_verificationPage is not { } page) return;
            _returnFromBrowser = true;
            DashboardWindow.OpenUrl(page.AbsoluteUri);
        };
        _cancel.Click += (_, _) => _attempt?.Cancel();
        Unloaded += (_, _) => _attempt?.Cancel();
    }

    private string SignInLabel => _grok.HasAppCredential ? "Sign In Again" : "Sign In with Grok";

    public bool ResumeIfPending()
    {
        var resume = _attempt is not null || _returnFromBrowser;
        _returnFromBrowser = false;
        return resume;
    }

    private async Task SignInAsync()
    {
        using var attempt = new CancellationTokenSource();
        _attempt = attempt;
        _signIn.Visibility = Visibility.Collapsed;
        _cancel.Visibility = Visibility.Visible;
        _status.Text = "Requesting a sign-in code…";
        try
        {
            await _container.ConnectGrokAsync((code, page) =>
            {
                _status.Text = "Open xAI and confirm this code.";
                _verificationPage = page;
                _code.Text = code;
                _code.Visibility = Visibility.Visible;
                _openGrok.Visibility = Visibility.Visible;
                _code.Focus();
                _code.SelectAll();
            }, attempt.Token);
            _status.Text = _container.Layout.IsProviderEnabled("grok")
                ? "Grok is connected to OpenUsage."
                : "Grok is connected. Turn on Grok in Customize to show its usage.";
        }
        catch (OperationCanceledException) { _status.Text = "Sign-in canceled."; }
        catch (ProviderRequestException ex)
        {
            AppLog.Warn("grok", ex.Message);
            _status.Text = ex.Message;
        }
        catch (Exception ex)
        {
            AppLog.Error("grok", "could not complete Grok sign-in", ex);
            _status.Text = "OpenUsage couldn't complete your Grok sign-in. Try again.";
        }
        finally
        {
            _attempt = null;
            _verificationPage = null;
            _code.Clear();
            _code.Visibility = Visibility.Collapsed;
            _openGrok.Visibility = Visibility.Collapsed;
            _cancel.Visibility = Visibility.Collapsed;
            _signIn.Content = SignInLabel;
            _signIn.Visibility = Visibility.Visible;
        }
    }
}

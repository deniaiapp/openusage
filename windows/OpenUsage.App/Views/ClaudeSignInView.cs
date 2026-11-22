using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using OpenUsage.Core;
using OpenUsage.Core.Providers.Claude;
using OpenUsage.Core.Support;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace OpenUsage.App.Views;

internal sealed class ClaudeSignInView : StackPanel
{
    private CancellationTokenSource? _attempt;
    private bool _returnFromBrowser;

    public ClaudeSignInView(AppContainer container, ClaudeProvider claude, Style buttonStyle)
    {
        Margin = new Thickness(0, 0, 0, 10);
        string ButtonTitle() => claude.IsSignedIn ? "Sign In Again" : "Sign In with Claude";
        var status = new TextBlock
        {
            Text = claude.IsSignedIn ? "Claude is connected to OpenUsage." : "Connect Claude in your browser.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 6)
        };
        var code = new TextBox { Visibility = Visibility.Collapsed, Padding = new Thickness(6) };
        var signIn = new Button { Style = buttonStyle, Content = ButtonTitle() };
        var submit = new Button { Style = buttonStyle, Content = "Submit Code", Visibility = Visibility.Collapsed };
        var cancel = new Button { Style = buttonStyle, Content = "Cancel", Visibility = Visibility.Collapsed };
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(signIn);
        controls.Children.Add(submit);
        controls.Children.Add(cancel);
        Children.Add(status);
        Children.Add(code);
        Children.Add(controls);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(code, "Claude sign-in code or callback URL");

        TaskCompletionSource<string>? input = null;
        submit.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(code.Text) || input?.TrySetResult(code.Text) != true) return;
            code.Clear();
            submit.IsEnabled = false;
        };
        cancel.Click += (_, _) => _attempt?.Cancel();
        Unloaded += (_, _) => _attempt?.Cancel();
        signIn.Click += async (_, _) =>
        {
            using var attempt = new CancellationTokenSource();
            _attempt = attempt;
            input = new(TaskCreationOptions.RunContinuationsAsynchronously);
            signIn.Visibility = Visibility.Collapsed;
            submit.Visibility = cancel.Visibility = code.Visibility = Visibility.Visible;
            submit.IsEnabled = true;
            status.Text = "Finish signing in in your browser. If it shows a code instead, paste it below.";
            try
            {
                await container.ConnectClaudeAsync(url =>
                {
                    try { Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }); }
                    catch (Exception ex) { throw new ProviderRequestException("Couldn't open your browser.", ex); }
                    _returnFromBrowser = true;
                }, input.Task, attempt.Token);
                status.Text = container.Layout.IsProviderEnabled("claude")
                    ? "Claude is connected to OpenUsage."
                    : "Claude is connected. Turn on Claude in Customize to show its usage.";
            }
            catch (Exception) when (attempt.IsCancellationRequested)
            {
                status.Text = "Sign-in canceled.";
            }
            catch (Exception ex)
            {
                AppLog.Warn("claude", $"Sign-in failed ({ex.GetType().Name}).");
                status.Text = ex is ProviderRequestException ? ex.Message : "Couldn't sign in to Claude. Try again.";
            }
            finally
            {
                _attempt = null;
                input?.TrySetCanceled();
                input = null;
                code.Clear();
                submit.Visibility = cancel.Visibility = code.Visibility = Visibility.Collapsed;
                signIn.Content = ButtonTitle();
                signIn.Visibility = Visibility.Visible;
            }
        };
    }

    public bool ResumeIfPending()
    {
        var resume = _attempt is not null || _returnFromBrowser;
        _returnFromBrowser = false;
        return resume;
    }
}

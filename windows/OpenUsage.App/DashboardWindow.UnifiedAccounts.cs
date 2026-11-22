using System.Windows;
using System.Windows.Controls;
using OpenUsage.App.Views;
using OpenUsage.Core.Models;
using OpenUsage.Core.Stores;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Orientation = System.Windows.Controls.Orientation;

namespace OpenUsage.App;

public partial class DashboardWindow
{
    private void AddUnifiedAccountsSetting(string providerId)
    {
        if (!LayoutStore.CanUnifyAccounts(providerId) ||
            _container.Layout.ProviderGroups().FirstOrDefault(group => group.Key == providerId)?.Count() is not >= 2)
            return;

        var toggle = new CheckBox
        {
            Content = "Unify Multiple Accounts",
            IsChecked = _container.Layout.UnifiesAccounts(providerId),
            Margin = new Thickness(0, 0, 0, 8)
        };
        toggle.Checked += (_, _) => SetUnified(true);
        toggle.Unchecked += (_, _) => SetUnified(false);
        ContentHost.Children.Add(toggle);

        void SetUnified(bool enable)
        {
            _container.Layout.SetUnifiesAccounts(providerId, enable);
            _container.Data.NotifyPresentationChanged();
        }
    }

    private UIElement UnifiedProviderCard(string kind, UnifiedAccountView combined)
    {
        var provider = combined.Provider;
        var open = _container.Layout.IsCardOpen(provider.Id);
        var card = Card();
        var stack = new StackPanel();
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = ProviderIcon.Create(provider.IconId, 16);
        icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 1, 8, 0));
        title.Children.Add(icon);
        title.Children.Add(new TextBlock { Text = provider.DisplayName, FontWeight = FontWeights.SemiBold });
        if (combined.Plan is { } plan)
            title.Children.Add(new TextBlock
            {
                Text = plan, Style = (Style)FindResource("MutedText"), Margin = new Thickness(8, 0, 0, 0)
            });
        var header = new Button
        {
            Content = title, Style = (Style)FindResource("GhostButton"), Margin = new Thickness(12, 10, 12, 4)
        };
        header.Click += (_, _) =>
        {
            _detailProviderId = kind;
            ShowScreen(DashboardScreen.CustomizeDetail);
        };
        stack.Children.Add(header);
        var unavailable = combined.Accounts.Count(account => _container.Data.ProviderError(account.Id) is not null);
        var waiting = combined.Accounts.Count(account =>
            _container.Data.Snapshot(account.Id) is null && _container.Data.ProviderError(account.Id) is null);
        if (!open && (unavailable != 0 || waiting != 0))
        {
            var status = new[]
            {
                unavailable == 0 ? null : $"{unavailable} could not refresh",
                waiting == 0 ? null : $"{waiting} waiting for usage"
            };
            stack.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", status.OfType<string>()),
                Style = (Style)FindResource("MutedText"),
                Margin = new Thickness(12, 0, 12, 4)
            });
        }
        foreach (var metric in combined.Metrics.Where(row => open || !row.OnDemand))
            stack.Children.Add(MetricRow(metric.Descriptor, metric.Data, unified: metric));
        if (open)
        {
            stack.Children.Add(SectionTitle("Accounts"));
            var accounts = combined.Accounts.Select(account => account.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var runtime in _container.Layout.VisibleProviders.Where(runtime => accounts.Contains(runtime.Provider.Id)))
                stack.Children.Add(ProviderCard(runtime));
        }
        stack.Children.Add(CardCaret(provider.Id, open));
        card.Child = stack;
        card.Margin = new Thickness(0, 0, 0, 10);
        return card;
    }

    private ContextMenu UnifiedMetricMenu(UnifiedAccountMetric metric)
    {
        var context = new ContextMenu();
        context.Items.Add(Item("Hide for All Accounts", () =>
        {
            _container.Layout.HideUnifiedMetric(metric);
            _container.Data.NotifyPresentationChanged();
        }));
        if (metric.Descriptor.Pinnable)
        {
            var pinned = _container.Layout.IsUnifiedMetricPinned(metric);
            context.Items.Add(Item(pinned ? "Unstar for All Accounts" : "Star for All Accounts", () =>
            {
                if (_container.Layout.ToggleUnifiedMetricPin(metric))
                    _container.Data.NotifyPresentationChanged();
            }));
        }
        return context;
    }
}

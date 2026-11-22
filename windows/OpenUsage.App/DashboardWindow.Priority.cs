using System.Windows;
using System.Windows.Controls;
using OpenUsage.Core.Services;

namespace OpenUsage.App;

public partial class DashboardWindow
{
    private void OnDashboardTabChanged(object sender, RoutedEventArgs args)
    {
        Render();
        Scroller.ScrollToTop();
    }

    private void RenderPriorityBoard()
    {
        var now = DateTimeOffset.Now;
        var board = PriorityBoard.Make(_container.Layout, _container.Data, now,
            _container.Settings.ShowUnusedQuotaInPriority);
        AddPrioritySection("Pinned", board.Pinned, now);
        AddPrioritySection("Running Out", board.RunningOut, now);
        AddPrioritySection("Use It or Lose It", board.UseItOrLoseIt, now);
        AddPrioritySection("Used Up", board.UsedUp, now);

        if (board.WaitingForFirstRead.Count > 0)
        {
            ContentHost.Children.Add(SectionTitle("Waiting for Usage"));
            foreach (var provider in board.WaitingForFirstRead)
            {
                var card = Card();
                card.Margin = new Thickness(0, 0, 0, 8);
                card.Child = new TextBlock
                {
                    Text = provider.DisplayName, Style = (Style)FindResource("MutedText"),
                    TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(12, 8, 12, 8)
                };
                ContentHost.Children.Add(card);
            }
        }

        var summary = board.HasRefreshErrors
            ? "Some accounts couldn't refresh. Check All Usage for details."
            : board.WaitingForFirstRead.Count > 0 ? null
            : !board.HasUsage ? "No usage data reported."
            : board.RunningOut.Count == 0 && board.UsedUp.Count == 0 && board.UseItOrLoseIt.Count == 0
                ? "No unpinned limits need attention." : null;
        if (summary is not null)
            ContentHost.Children.Add(new TextBlock
            {
                Text = summary, TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("MutedText"),
                Margin = new Thickness(8)
            });
    }

    private void AddPrioritySection(string label, IReadOnlyList<PriorityMetric> metrics, DateTimeOffset now)
    {
        if (metrics.Count == 0) return;
        ContentHost.Children.Add(SectionTitle(label));
        foreach (var metric in metrics)
        {
            var card = Card();
            card.Margin = new Thickness(0, 0, 0, 8);
            card.Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = metric.Provider.DisplayName, Style = (Style)FindResource("MutedText"),
                        TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(12, 8, 12, 0)
                    },
                    MetricRow(metric.Descriptor, metric.Data, now)
                }
            };
            ContentHost.Children.Add(card);
        }
    }
}

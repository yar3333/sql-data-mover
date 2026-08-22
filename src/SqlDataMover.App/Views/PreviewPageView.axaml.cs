using Avalonia.Controls;
using SqlDataMover.App.ViewModels;

namespace SqlDataMover.App.Views;

public partial class PreviewPageView : UserControl
{
    private PreviewPageViewModel? _viewModel;

    public PreviewPageView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.LogAppended -= OnLogAppended;

        _viewModel = DataContext as PreviewPageViewModel;
        if (_viewModel is not null)
            _viewModel.LogAppended += OnLogAppended;
    }

    private void OnLogAppended(object? sender, System.EventArgs e)
    {
        if (LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[^1]!);
    }
}

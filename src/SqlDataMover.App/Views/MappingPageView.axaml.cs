using Avalonia.Controls;
using SqlDataMover.App.ViewModels;

namespace SqlDataMover.App.Views;

public partial class MappingPageView : UserControl
{
    private MappingPageViewModel? _viewModel;

    public MappingPageView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.LogAppended -= OnLogAppended;

        _viewModel = DataContext as MappingPageViewModel;
        if (_viewModel is not null)
            _viewModel.LogAppended += OnLogAppended;
    }

    private void OnLogAppended(object? sender, System.EventArgs e)
    {
        if (LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[^1]!);
    }
}

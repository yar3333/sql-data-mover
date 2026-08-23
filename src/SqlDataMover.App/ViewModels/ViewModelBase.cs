using CommunityToolkit.Mvvm.ComponentModel;
using SqlDataMover.App.Localization;

namespace SqlDataMover.App.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    protected ViewModelBase()
    {
        // При смене языка пересчитываются все привязанные строки (включая вычисляемые).
        AppStrings.Current.PropertyChanged += (_, _) => OnPropertyChanged(string.Empty);
    }
}

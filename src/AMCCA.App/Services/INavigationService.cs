using System;
using AMCCA.App.Common;

namespace AMCCA.App.Services;

public interface INavigationService
{
    ViewModelBase? CurrentViewModel { get; }
    void NavigateTo<TViewModel>() where TViewModel : ViewModelBase;
    event Action<ViewModelBase>? Navigated;
}

public class NavigationService : INavigationService
{
    private readonly Func<Type, ViewModelBase> _viewModelFactory;
    public ViewModelBase? CurrentViewModel { get; private set; }
    public event Action<ViewModelBase>? Navigated;

    public NavigationService(Func<Type, ViewModelBase> viewModelFactory)
    {
        _viewModelFactory = viewModelFactory;
    }

    public void NavigateTo<TViewModel>() where TViewModel : ViewModelBase
    {
        var vm = _viewModelFactory(typeof(TViewModel));
        CurrentViewModel = vm;
        Navigated?.Invoke(vm);
    }
}

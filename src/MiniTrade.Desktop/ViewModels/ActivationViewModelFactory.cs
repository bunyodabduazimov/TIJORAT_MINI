using Microsoft.Extensions.DependencyInjection;

namespace MiniTrade.Desktop.ViewModels;

/// <summary>Создаёт новый экземпляр ViewModel активации (окно открывается из настроек).</summary>
public sealed class ActivationViewModelFactory(IServiceProvider services)
{
    public ActivationViewModel Create() => services.GetRequiredService<ActivationViewModel>();
}

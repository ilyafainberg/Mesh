using Mesh.App.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using BlazorDispatcher = Microsoft.AspNetCore.Components.Dispatcher;

namespace Mesh.App.Components.Mobile;

public interface ITopicSendObserverDispatcherFactory
{
    ITopicSendObserverDispatcher Create(BlazorDispatcher dispatcher);
}

public sealed class RendererTopicSendObserverDispatcher(BlazorDispatcher dispatcher)
    : ITopicSendObserverDispatcher
{
    public Task InvokeAsync(Func<Task> workItem)
        => dispatcher.InvokeAsync(workItem);
}

public sealed class RendererDispatcherCapture : IComponent
{
    private ITopicSendObserverDispatcher? dispatcher;

    [Inject]
    private IServiceProvider Services { get; set; } = null!;

    [Parameter]
    public Action<ITopicSendObserverDispatcher>? DispatcherCaptured { get; set; }

    public void Attach(RenderHandle renderHandle)
    {
        var factory = Services.GetService<ITopicSendObserverDispatcherFactory>();
        dispatcher = factory?.Create(renderHandle.Dispatcher)
                     ?? new RendererTopicSendObserverDispatcher(renderHandle.Dispatcher);
    }

    public Task SetParametersAsync(ParameterView parameters)
    {
        parameters.SetParameterProperties(this);
        DispatcherCaptured?.Invoke(
            dispatcher
            ?? throw new InvalidOperationException("The renderer dispatcher was not attached."));
        return Task.CompletedTask;
    }
}

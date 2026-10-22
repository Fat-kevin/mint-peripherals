using Microsoft.Extensions.DependencyInjection;
using YuandaoTws.Domain.Abstractions;
using YuandaoTws.Infrastructure.Bluetooth;
using YuandaoTws.Infrastructure.Protocols;

namespace YuandaoTws.Infrastructure;

/// <summary>Infrastructure 层 DI 注册入口。</summary>
public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IBluetoothDeviceScanner, BluetoothDeviceScanner>();
        services.AddSingleton<IGattConnectionFactory, GattConnectionFactory>();
        services.AddSingleton<IDeviceProtocol, YuandaoProtocol>();
        services.AddSingleton<IRfcommServiceEnumerator, RfcommServiceEnumerator>();
        services.AddSingleton<ISppConnectionFactory, SppConnectionFactory>();
        return services;
    }
}

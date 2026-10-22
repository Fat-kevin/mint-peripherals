namespace YuandaoTws.Domain.Exceptions;

/// <summary>领域异常基类：各层抛出的领域化异常均继承此类。</summary>
public abstract class DomainException : Exception
{
    protected DomainException(string message)
        : base(message)
    {
    }

    protected DomainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>蓝牙连接失败（设备不可达、连接被拒、超时等）。</summary>
public sealed class BluetoothConnectionException : DomainException
{
    public BluetoothConnectionException(string message)
        : base(message)
    {
    }

    public BluetoothConnectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>协议解析失败（帧格式异常、校验失败等）。</summary>
public class ProtocolException : DomainException
{
    public ProtocolException(string message)
        : base(message)
    {
    }

    public ProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>目标设备的私有协议尚未逆向/实现。</summary>
public sealed class ProtocolNotResolvedException : ProtocolException
{
    public ProtocolNotResolvedException(string message)
        : base(message)
    {
    }
}

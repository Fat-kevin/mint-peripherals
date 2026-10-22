namespace DeviceCenterPreview.Core.Services;

/// <summary>写入结果：失败必须带用户可读原因。</summary>
public sealed record WriteResult(bool Ok, string? Message = null)
{
    public static WriteResult Success { get; } = new(true);

    public static WriteResult Fail(string message) => new(false, message);
}

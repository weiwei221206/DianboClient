namespace Dianbo.Core.Models;

public sealed record UserProfile
{
    public required long Uid { get; init; }
    public string? Nickname { get; init; }
    public string? AvatarUrl { get; init; }
    public bool IsVip { get; init; }
    public int VipType { get; init; }
    public DateTimeOffset? VipExpireDate { get; init; }

    public string VipStatusText
    {
        get
        {
            if (!IsVip)
            {
                return "普通用户";
            }

            if (VipExpireDate.HasValue)
            {
                if (VipExpireDate.Value < DateTimeOffset.Now)
                {
                    return "VIP 已过期";
                }

                return $"VIP 会员 · {VipExpireDate.Value:yyyy-MM-dd} 到期";
            }

            return "VIP 会员";
        }
    }
}

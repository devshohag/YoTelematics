using YoTelematics.Domain.Common;

namespace YoTelematics.Domain.Entities;

public class Organization : ITenantScopedEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;

    public ICollection<Branch> Branches { get; set; } = new List<Branch>();
}

public class Branch : ITenantScopedEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = string.Empty;

    public Organization? Organization { get; set; }
}

public class User : ITenantScopedEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? ParentDealerId { get; set; }
    public Guid? BranchId { get; set; }
    public string Phone { get; set; } = string.Empty;
    public string? PasswordHash { get; set; }
    public string? SecurityPinHash { get; set; }
    public short FailedPinAttempts { get; set; }
    public DateTimeOffset? PinLockedUntilUtc { get; set; }
    public string Role { get; set; } = string.Empty;
    public string Locale { get; set; } = "bn-BD";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public User? ParentDealer { get; set; }
    public Branch? Branch { get; set; }
    public ICollection<UserPushToken> PushTokens { get; set; } = new List<UserPushToken>();
}

public class UserPushToken : ITenantScopedEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string FcmToken { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public User? User { get; set; }
}

public class OtpChallenge : ITenantScopedEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Phone { get; set; } = string.Empty;
    public string? RequesterIp { get; set; }
    public string OtpCodeHash { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public short AttemptCount { get; set; }
    public bool IsVerified { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

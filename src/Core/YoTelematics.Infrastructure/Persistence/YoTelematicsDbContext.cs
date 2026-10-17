using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using YoTelematics.Application.Common;
using YoTelematics.Domain.Entities;

namespace YoTelematics.Infrastructure.Persistence;

public class YoTelematicsDbContext : DbContext, IDataProtectionKeyContext
{
    private readonly ITenantContextAccessor? _tenantContext;

    public YoTelematicsDbContext(DbContextOptions<YoTelematicsDbContext> options)
        : base(options)
    {
    }

    public YoTelematicsDbContext(
        DbContextOptions<YoTelematicsDbContext> options,
        ITenantContextAccessor tenantContext)
        : base(options)
    {
        _tenantContext = tenantContext;
    }

    public Guid? CurrentBranchFilter => _tenantContext?.CurrentBranchId;

    // 1. Data Protection Key Ring
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    // 2. Tenants & Hierarchy
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantHoliday> TenantHolidays => Set<TenantHoliday>();
    public DbSet<TenantProviderConfig> TenantProviderConfigs => Set<TenantProviderConfig>();

    // 3. Organizations, Branches, Users, Push Tokens & OTP Challenges
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<User> Users => Set<User>();
    public DbSet<UserPushToken> UserPushTokens => Set<UserPushToken>();
    public DbSet<OtpChallenge> OtpChallenges => Set<OtpChallenge>();

    // 4. Device Models & Inventory
    public DbSet<DeviceModel> DeviceModels => Set<DeviceModel>();
    public DbSet<InventoryDevice> InventoryDevices => Set<InventoryDevice>();
    public DbSet<InventoryMovement> InventoryMovements => Set<InventoryMovement>();

    // 5. Assets & Profiles
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<AssetMember> AssetMembers => Set<AssetMember>();
    public DbSet<VehicleProfile> VehicleProfiles => Set<VehicleProfile>();
    public DbSet<DriverSchedule> DriverSchedules => Set<DriverSchedule>();
    public DbSet<PersonPetProfile> PersonPetProfiles => Set<PersonPetProfile>();
    public DbSet<WatchContact> WatchContacts => Set<WatchContact>();
    public DbSet<AssetDeviceSwap> AssetDeviceSwaps => Set<AssetDeviceSwap>();

    // 6. Geofences, Wi-Fi Fingerprints & Schedules
    public DbSet<Geofence> Geofences => Set<Geofence>();
    public DbSet<GeofenceWifiFingerprint> GeofenceWifiFingerprints => Set<GeofenceWifiFingerprint>();
    public DbSet<ChildSchedule> ChildSchedules => Set<ChildSchedule>();
    public DbSet<GeofenceHoliday> GeofenceHolidays => Set<GeofenceHoliday>();

    // 7. Commands & Security Audit Logs
    public DbSet<DeviceCommand> DeviceCommands => Set<DeviceCommand>();
    public DbSet<SecurityAuditLog> SecurityAuditLogs => Set<SecurityAuditLog>();

    // 8. TimescaleDB Hypertables
    public DbSet<TelemetryHistory> TelemetryHistory => Set<TelemetryHistory>();
    public DbSet<TelemetryPerson> TelemetryPerson => Set<TelemetryPerson>();

    // 9. Double-Entry FinTech Ledger
    public DbSet<CommissionRate> CommissionRates => Set<CommissionRate>();
    public DbSet<LedgerAccount> LedgerAccounts => Set<LedgerAccount>();
    public DbSet<LedgerTransaction> LedgerTransactions => Set<LedgerTransaction>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // 1. data_protection_keys
        modelBuilder.Entity<DataProtectionKey>(b =>
        {
            b.ToTable("data_protection_keys");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.FriendlyName).HasColumnName("friendly_name");
            b.Property(x => x.Xml).HasColumnName("xml").IsRequired();
        });

        // 2. tenants, tenant_holidays, tenant_provider_configs
        modelBuilder.Entity<Tenant>(b =>
        {
            b.ToTable("tenants");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.ParentTenantId).HasColumnName("parent_tenant_id");
            b.Property(x => x.HierarchyPath).HasColumnName("hierarchy_path").HasColumnType("ltree").IsRequired();
            b.Property(x => x.Name).HasColumnName("name").HasMaxLength(120).IsRequired();
            b.Property(x => x.Slug).HasColumnName("slug").HasMaxLength(64).IsRequired();
            b.Property(x => x.CustomDomain).HasColumnName("custom_domain").HasMaxLength(120);
            b.Property(x => x.TlsStatus).HasColumnName("tls_status").HasMaxLength(24).IsRequired();
            b.Property(x => x.DefaultLocale).HasColumnName("default_locale").HasMaxLength(10).IsRequired();
            b.Property(x => x.Timezone).HasColumnName("timezone").HasMaxLength(50).IsRequired();
            b.Property(x => x.ThemeJson).HasColumnName("theme_json").HasColumnType("jsonb").IsRequired();
            b.Property(x => x.IsActive).HasColumnName("is_active");
            b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            b.HasOne(x => x.ParentTenant).WithMany(x => x.SubTenants).HasForeignKey(x => x.ParentTenantId);
        });

        modelBuilder.Entity<TenantHoliday>(b =>
        {
            b.ToTable("tenant_holidays");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.TenantId).HasColumnName("tenant_id");
            b.Property(x => x.HolidayDate).HasColumnName("holiday_date");
            b.Property(x => x.Description).HasColumnName("description").HasMaxLength(100).IsRequired();
            b.HasOne(x => x.Tenant).WithMany(x => x.Holidays).HasForeignKey(x => x.TenantId);
        });

        modelBuilder.Entity<TenantProviderConfig>(b =>
        {
            b.ToTable("tenant_provider_configs");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.TenantId).HasColumnName("tenant_id");
            b.Property(x => x.ProviderType).HasColumnName("provider_type").HasMaxLength(24).IsRequired();
            b.Property(x => x.EncryptedConfigBytes).HasColumnName("encrypted_config_bytes").IsRequired();
            b.Property(x => x.IsActive).HasColumnName("is_active");
            b.HasOne(x => x.Tenant).WithMany(x => x.ProviderConfigs).HasForeignKey(x => x.TenantId);
        });

        // 3. organizations, branches, users, user_push_tokens, otp_challenges
        modelBuilder.Entity<Organization>(b =>
        {
            b.ToTable("organizations");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.TenantId).HasColumnName("tenant_id");
            b.Property(x => x.Name).HasColumnName("name").HasMaxLength(120).IsRequired();
        });

        modelBuilder.Entity<Branch>(b =>
        {
            b.ToTable("branches");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.TenantId).HasColumnName("tenant_id");
            b.Property(x => x.OrganizationId).HasColumnName("organization_id");
            b.Property(x => x.Name).HasColumnName("name").HasMaxLength(120).IsRequired();
            b.HasOne(x => x.Organization).WithMany(x => x.Branches).HasForeignKey(x => x.OrganizationId);
        });

        modelBuilder.Entity<User>(b =>
        {
            b.ToTable("users");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.TenantId).HasColumnName("tenant_id");
            b.Property(x => x.ParentDealerId).HasColumnName("parent_dealer_id");
            b.Property(x => x.BranchId).HasColumnName("branch_id");
            b.Property(x => x.Phone).HasColumnName("phone").HasMaxLength(24).IsRequired();
            b.Property(x => x.PasswordHash).HasColumnName("password_hash");
            b.Property(x => x.SecurityPinHash).HasColumnName("security_pin_hash");
            b.Property(x => x.FailedPinAttempts).HasColumnName("failed_pin_attempts");
            b.Property(x => x.PinLockedUntilUtc).HasColumnName("pin_locked_until_utc");
            b.Property(x => x.Role).HasColumnName("role").HasMaxLength(32).IsRequired();
            b.Property(x => x.Locale).HasColumnName("locale").HasMaxLength(10).IsRequired();
            b.Property(x => x.IsActive).HasColumnName("is_active");
            b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            b.HasOne(x => x.ParentDealer).WithMany().HasForeignKey(x => x.ParentDealerId);
            b.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId);
        });

        modelBuilder.Entity<UserPushToken>(b =>
        {
            b.ToTable("user_push_tokens");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.TenantId).HasColumnName("tenant_id");
            b.Property(x => x.UserId).HasColumnName("user_id");
            b.Property(x => x.FcmToken).HasColumnName("fcm_token").IsRequired();
            b.Property(x => x.Platform).HasColumnName("platform").HasMaxLength(16).IsRequired();
            b.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
            b.HasOne(x => x.User).WithMany(x => x.PushTokens).HasForeignKey(x => x.UserId);
        });

        modelBuilder.Entity<OtpChallenge>(b =>
        {
            b.ToTable("otp_challenges");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.TenantId).HasColumnName("tenant_id");
            b.Property(x => x.Phone).HasColumnName("phone").HasMaxLength(24).IsRequired();
            b.Property(x => x.RequesterIp).HasColumnName("requester_ip").HasMaxLength(45);
            b.Property(x => x.OtpCodeHash).HasColumnName("otp_code_hash").IsRequired();
            b.Property(x => x.ExpiresAtUtc).HasColumnName("expires_at_utc");
            b.Property(x => x.AttemptCount).HasColumnName("attempt_count");
            b.Property(x => x.IsVerified).HasColumnName("is_verified");
            b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
        });

        // 4. device_models, inventory_devices, inventory_movements
        modelBuilder.Entity<DeviceModel>(b =>
        {
            b.ToTable("device_models");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").HasMaxLength(32);
            b.Property(x => x.Protocol).HasColumnName("protocol").HasMaxLength(24).IsRequired();
            b.Property(x => x.CutCmdTemplate).HasColumnName("cut_cmd_template").HasMaxLength(64).IsRequired();
            b.Property(x => x.RestoreCmdTemplate).HasColumnName("restore_cmd_template").HasMaxLength(64).IsRequired();
            b.Property(x => x.StatusCmdTemplate).HasColumnName("status_cmd_template").HasMaxLength(64).IsRequired();
            b.Property(x => x.StatusResponseRegex).HasColumnName("status_response_regex").HasMaxLength(120).IsRequired();
            b.Property(x => x.TimerActiveCmdTemplate).HasColumnName("timer_active_cmd_template").HasMaxLength(64).IsRequired();
            b.Property(x => x.TimerNormalCmdTemplate).HasColumnName("timer_normal_cmd_template").HasMaxLength(64).IsRequired();
            b.Property(x => x.HeartbeatArmedCmdTemplate).HasColumnName("heartbeat_armed_cmd_template").HasMaxLength(64).IsRequired();
            b.Property(x => x.HeartbeatNormalCmdTemplate).HasColumnName("heartbeat_normal_cmd_template").HasMaxLength(64).IsRequired();
            b.Property(x => x.SupportsAcInput).HasColumnName("supports_ac_input");
            b.Property(x => x.SupportsAccelerometerEvents).HasColumnName("supports_accelerometer_events");
        });

        modelBuilder.Entity<InventoryDevice>(b =>
        {
            b.ToTable("inventory_devices");
            b.HasKey(x => x.Imei);
            b.Property(x => x.Imei).HasColumnName("imei").HasMaxLength(15);
            b.Property(x => x.TenantId).HasColumnName("tenant_id");
            b.Property(x => x.ModelId).HasColumnName("model_id").HasMaxLength(32).IsRequired();
            b.Property(x => x.BarcodeSerial).HasColumnName("barcode_serial").HasMaxLength(40).IsRequired();
            b.Property(x => x.SimMsisdn).HasColumnName("sim_msisdn").HasMaxLength(24);
            b.Property(x => x.SimIccid).HasColumnName("sim_iccid").HasMaxLength(24);
            b.Property(x => x.ApnName).HasColumnName("apn_name").HasMaxLength(64).IsRequired();
            b.Property(x => x.DevicePasswordEncrypted).HasColumnName("device_password_encrypted").IsRequired();
            b.Property(x => x.CurrentHolderDealerId).HasColumnName("current_holder_dealer_id");
            b.Property(x => x.ProvisioningState).HasColumnName("provisioning_state").HasMaxLength(24).IsRequired();
            b.Property(x => x.CurrentHeartbeatIntervalSec).HasColumnName("current_heartbeat_interval_sec");
            b.Property(x => x.LastConnectedAtUtc).HasColumnName("last_connected_at_utc");
            b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            b.HasOne(x => x.Model).WithMany().HasForeignKey(x => x.ModelId);
            b.HasOne(x => x.CurrentHolderDealer).WithMany().HasForeignKey(x => x.CurrentHolderDealerId);
        });

        modelBuilder.Entity<InventoryMovement>(b =>
        {
            b.ToTable("inventory_movements");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.TenantId).HasColumnName("tenant_id");
            b.Property(x => x.Imei).HasColumnName("imei").HasMaxLength(15).IsRequired();
            b.Property(x => x.FromUserId).HasColumnName("from_user_id");
            b.Property(x => x.ToUserId).HasColumnName("to_user_id");
            b.Property(x => x.MovementType).HasColumnName("movement_type").HasMaxLength(24).IsRequired();
            b.Property(x => x.MovedAtUtc).HasColumnName("moved_at_utc");
            b.HasOne(x => x.Device).WithMany().HasForeignKey(x => x.Imei);
            b.HasOne(x => x.FromUser).WithMany().HasForeignKey(x => x.FromUserId);
            b.HasOne(x => x.ToUser).WithMany().HasForeignKey(x => x.ToUserId);
        });

        // 5. assets, asset_members, vehicle_profiles, driver_schedules, person_pet_profiles, watch_contacts, asset_device_swaps
        modelBuilder.Entity<Asset>(b =>
        {
            b.ToTable("assets");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.TenantId).HasColumnName("tenant_id");
            b.Property(x => x.BranchId).HasColumnName("branch_id");
            b.Property(x => x.OriginDealerId).HasColumnName("origin_dealer_id");
            b.Property(x => x.CurrentImei).HasColumnName("current_imei").HasMaxLength(15);
            b.Property(x => x.Category).HasColumnName("category").HasMaxLength(16).IsRequired();
            b.Property(x => x.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
            b.Property(x => x.IdentifierLabel).HasColumnName("identifier_label").HasMaxLength(40);
            b.Property(x => x.StateVersion).HasColumnName("state_version");
            b.Property(x => x.ServiceModeUntilUtc).HasColumnName("service_mode_until_utc");
            b.Property(x => x.LastTelemetryTimestampUtc).HasColumnName("last_telemetry_timestamp_utc");
            b.Property(x => x.LastHeartbeatUtc).HasColumnName("last_heartbeat_utc");
            b.Property(x => x.SubscriptionExpiresAtUtc).HasColumnName("subscription_expires_at_utc");
            b.Property(x => x.IsActive).HasColumnName("is_active");
            b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            b.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId);
            b.HasOne(x => x.OriginDealer).WithMany().HasForeignKey(x => x.OriginDealerId);
            b.HasOne(x => x.CurrentDevice).WithMany().HasForeignKey(x => x.CurrentImei);
            b.HasQueryFilter(x => CurrentBranchFilter == null || x.BranchId == CurrentBranchFilter);
        });

        modelBuilder.Entity<AssetMember>(b =>
        {
            b.ToTable("asset_members");
            b.HasKey(x => new { x.AssetId, x.UserId });
            b.Property(x => x.AssetId).HasColumnName("asset_id");
            b.Property(x => x.TenantId).HasColumnName("tenant_id");
            b.Property(x => x.UserId).HasColumnName("user_id");
            b.Property(x => x.MemberRole).HasColumnName("member_role").HasMaxLength(24).IsRequired();
            b.Property(x => x.CallPriorityOrder).HasColumnName("call_priority_order");
            b.Property(x => x.CanControlEngine).HasColumnName("can_control_engine");
            b.Property(x => x.CanListenAudio).HasColumnName("can_listen_audio");
            b.HasOne(x => x.Asset).WithMany(x => x.Members).HasForeignKey(x => x.AssetId);
            b.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
        });

        modelBuilder.Entity<VehicleProfile>(b =>
        {
            b.ToTable("vehicle_profiles");
            b.HasKey(x => x.AssetId);
            b.Property(x => x.AssetId).HasColumnName("asset_id");
            b.Property(x => x.TenantId).HasColumnName("tenant_id");
            b.Property(x => x.ArmedState).HasColumnName("armed_state").HasMaxLength(24).IsRequired();
            b.Property(x => x.AutoArmEnabled).HasColumnName("auto_arm_enabled");
            b.Property(x => x.AutoArmDelaySeconds).HasColumnName("auto_arm_delay_seconds");
            b.Property(x => x.AutoArmDueAtUtc).HasColumnName("auto_arm_due_at_utc");
            b.Property(x => x.GraceExpiresAtUtc).HasColumnName("grace_expires_at_utc");
            b.Property(x => x.VibrationCountWindow).HasColumnName("vibration_count_window");
            b.Property(x => x.LastVibrationUtc).HasColumnName("last_vibration_utc");
            b.Property(x => x.RelayState).HasColumnName("relay_state").HasMaxLength(24).IsRequired();
            b.Property(x => x.ParkedLocation).HasColumnName("parked_location").HasColumnType("geography (Point, 4326)");
            b.Property(x => x.ParkedLocationQuality).HasColumnName("parked_location_quality").HasMaxLength(16).IsRequired();
            b.Property(x => x.EstimatedKmPerLiter).HasColumnName("estimated_km_per_liter");
            b.Property(x => x.OdometerMeters).HasColumnName("odometer_meters");
            b.HasOne(x => x.Asset).WithOne(x => x.VehicleProfile).HasForeignKey<VehicleProfile>(x => x.AssetId);
        });

        modelBuilder.Entity<DriverSchedule>(b =>
        {
            b.ToTable("driver_schedules");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.TenantId).HasColumnName("tenant_id");
            b.Property(x => x.AssetId).HasColumnName("asset_id");
            b.Property(x => x.DriverUserId).HasColumnName("driver_user_id");
            b.Property(x => x.ActiveDays).HasColumnName("active_days");
            b.Property(x => x.DutyStartLocal).HasColumnName("duty_start_local");
            b.Property(x => x.DutyEndLocal).HasColumnName("duty_end_local");
            b.HasOne(x => x.Asset).WithMany().HasForeignKey(x => x.AssetId);
            b.HasOne(x => x.DriverUser).WithMany().HasForeignKey(x => x.DriverUserId);
        });

        modelBuilder.Entity<PersonPetProfile>(b =>
        {
            b.ToTable("person_pet_profiles");
            b.HasKey(x => x.AssetId);
            b.Property(x => x.AssetId).HasColumnName("asset_id");
            b.Property(x => x.TenantId).HasColumnName("tenant_id");
            b.Property(x => x.SafetyState).HasColumnName("safety_state").HasMaxLength(32).IsRequired();
            b.Property(x => x.StateChangedAtUtc).HasColumnName("state_changed_at_utc");
            b.Property(x => x.QrPublicToken).HasColumnName("qr_public_token").HasMaxLength(32);
            b.Property(x => x.HomeWifiBssids).HasColumnName("home_wifi_bssids");
            b.Property(x => x.MissingWifiScanStreak).HasColumnName("missing_wifi_scan_streak");
            b.Property(x => x.IsLostModeActive).HasColumnName("is_lost_mode_active");
            b.Property(x => x.AudioListenConsentGiven).HasColumnName("audio_listen_consent_given");
            b.Property(x => x.AudioConsentTimestampUtc).HasColumnName("audio_consent_timestamp_utc");
            b.HasOne(x => x.Asset).WithOne(x => x.PersonPetProfile).HasForeignKey<PersonPetProfile>(x => x.AssetId);
        });

        modelBuilder.Entity<WatchContact>(b =>
        {
            b.ToTable("watch_contacts");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.TenantId).HasColumnName("tenant_id");
            b.Property(x => x.AssetId).HasColumnName("asset_id");
            b.Property(x => x.ContactName).HasColumnName("contact_name").HasMaxLength(64).IsRequired();
            b.Property(x => x.PhoneNumber).HasColumnName("phone_number").HasMaxLength(24).IsRequired();
            b.Property(x => x.SlotIndex).HasColumnName("slot_index");
            b.Property(x => x.IsSosNumber).HasColumnName("is_sos_number");
            b.Property(x => x.SyncedAtUtc).HasColumnName("synced_at_utc");
            b.HasOne(x => x.Asset).WithMany(x => x.WatchContacts).HasForeignKey(x => x.AssetId);
        });

        modelBuilder.Entity<AssetDeviceSwap>(b =>
        {
            b.ToTable("asset_device_swaps");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.TenantId).HasColumnName("tenant_id");
            b.Property(x => x.AssetId).HasColumnName("asset_id");
            b.Property(x => x.OldImei).HasColumnName("old_imei").HasMaxLength(15);
            b.Property(x => x.NewImei).HasColumnName("new_imei").HasMaxLength(15);
            b.Property(x => x.SwappedByUserId).HasColumnName("swapped_by_user_id");
            b.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(120);
            b.Property(x => x.SwappedAtUtc).HasColumnName("swapped_at_utc");
            b.HasOne(x => x.Asset).WithMany().HasForeignKey(x => x.AssetId);
            b.HasOne(x => x.SwappedByUser).WithMany().HasForeignKey(x => x.SwappedByUserId);
        });

        // 6. geofences, geofence_wifi_fingerprints, child_schedules, geofence_holidays
        modelBuilder.Entity<Geofence>(b =>
        {
            b.ToTable("geofences");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.TenantId).HasColumnName("tenant_id");
            b.Property(x => x.BranchId).HasColumnName("branch_id");
            b.Property(x => x.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
            b.Property(x => x.ZoneType).HasColumnName("zone_type").HasMaxLength(24).IsRequired();
            b.Property(x => x.CenterPoint).HasColumnName("center_point").HasColumnType("geography (Point, 4326)");
            b.Property(x => x.SafeRadiusM).HasColumnName("safe_radius_m");
            b.Property(x => x.DangerRadiusM).HasColumnName("danger_radius_m");
            b.Property(x => x.HysteresisBufferM).HasColumnName("hysteresis_buffer_m");
            b.Property(x => x.PolygonBoundary).HasColumnName("polygon_boundary").HasColumnType("geography (Polygon, 4326)");
            b.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId);
            b.HasQueryFilter(x => CurrentBranchFilter == null || x.BranchId == CurrentBranchFilter);
        });

        modelBuilder.Entity<GeofenceWifiFingerprint>(b =>
        {
            b.ToTable("geofence_wifi_fingerprints");
            b.HasKey(x => new { x.GeofenceId, x.Bssid });
            b.Property(x => x.GeofenceId).HasColumnName("geofence_id");
            b.Property(x => x.Bssid).HasColumnName("bssid").HasMaxLength(17);
            b.Property(x => x.SeenCount).HasColumnName("seen_count");
            b.Property(x => x.LastSeenUtc).HasColumnName("last_seen_utc");
            b.HasOne(x => x.Geofence).WithMany(x => x.WifiFingerprints).HasForeignKey(x => x.GeofenceId);
        });

        modelBuilder.Entity<ChildSchedule>(b =>
        {
            b.ToTable("child_schedules");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.TenantId).HasColumnName("tenant_id");
            b.Property(x => x.AssetId).HasColumnName("asset_id");
            b.Property(x => x.GeofenceId).HasColumnName("geofence_id");
            b.Property(x => x.ActiveDays).HasColumnName("active_days");
            b.Property(x => x.ArriveByLocal).HasColumnName("arrive_by_local");
            b.Property(x => x.LeaveFromLocal).HasColumnName("leave_from_local");
            b.Property(x => x.SkipTodayOverrideDate).HasColumnName("skip_today_override_date");
            b.HasOne(x => x.Asset).WithMany().HasForeignKey(x => x.AssetId);
            b.HasOne(x => x.Geofence).WithMany().HasForeignKey(x => x.GeofenceId);
        });

        modelBuilder.Entity<GeofenceHoliday>(b =>
        {
            b.ToTable("geofence_holidays");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.TenantId).HasColumnName("tenant_id");
            b.Property(x => x.GeofenceId).HasColumnName("geofence_id");
            b.Property(x => x.HolidayDate).HasColumnName("holiday_date");
            b.Property(x => x.Description).HasColumnName("description").HasMaxLength(100);
            b.HasOne(x => x.Geofence).WithMany().HasForeignKey(x => x.GeofenceId);
        });

        // 7. device_commands & security_audit_logs
        modelBuilder.Entity<DeviceCommand>(b =>
        {
            b.ToTable("device_commands");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.TenantId).HasColumnName("tenant_id");
            b.Property(x => x.AssetId).HasColumnName("asset_id");
            b.Property(x => x.Imei).HasColumnName("imei").HasMaxLength(15).IsRequired();
            b.Property(x => x.CommandType).HasColumnName("command_type").HasMaxLength(32).IsRequired();
            b.Property(x => x.DeliveryChannel).HasColumnName("delivery_channel").HasMaxLength(12).IsRequired();
            b.Property(x => x.Status).HasColumnName("status").HasMaxLength(24).IsRequired();
            b.Property(x => x.SupersededByCommandId).HasColumnName("superseded_by_command_id");
            b.Property(x => x.IssuedByUserId).HasColumnName("issued_by_user_id");
            b.Property(x => x.AuthMethod).HasColumnName("auth_method").HasMaxLength(24).IsRequired();
            b.Property(x => x.ExpiresAtUtc).HasColumnName("expires_at_utc");
            b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            b.Property(x => x.SentAtUtc).HasColumnName("sent_at_utc");
            b.Property(x => x.AckAtUtc).HasColumnName("ack_at_utc");
            b.Property(x => x.RawResponse).HasColumnName("raw_response");
            b.HasOne(x => x.Asset).WithMany().HasForeignKey(x => x.AssetId);
            b.HasOne(x => x.SupersededByCommand).WithMany().HasForeignKey(x => x.SupersededByCommandId);
            b.HasOne(x => x.IssuedByUser).WithMany().HasForeignKey(x => x.IssuedByUserId);
        });

        modelBuilder.Entity<SecurityAuditLog>(b =>
        {
            b.ToTable("security_audit_logs");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.TenantId).HasColumnName("tenant_id");
            b.Property(x => x.AssetId).HasColumnName("asset_id");
            b.Property(x => x.ActorUserId).HasColumnName("actor_user_id");
            b.Property(x => x.ActionType).HasColumnName("action_type").HasMaxLength(48).IsRequired();
            b.Property(x => x.SourceIpOrCli).HasColumnName("source_ip_or_cli").HasMaxLength(64);
            b.Property(x => x.DetailsJsonb).HasColumnName("details_jsonb").HasColumnType("jsonb").IsRequired();
            b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
        });

        // 8. telemetry_history & telemetry_person (Composite PK: asset_id, device_timestamp_utc, packet_type)
        ConfigureTelemetryEntity<TelemetryHistory>(modelBuilder, "telemetry_history");
        ConfigureTelemetryEntity<TelemetryPerson>(modelBuilder, "telemetry_person");

        // 9. commission_rates, ledger_accounts, ledger_transactions, ledger_entries
        modelBuilder.Entity<CommissionRate>(b =>
        {
            b.ToTable("commission_rates");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.TenantId).HasColumnName("tenant_id");
            b.Property(x => x.DealerUserId).HasColumnName("dealer_user_id");
            b.Property(x => x.AssetCategory).HasColumnName("asset_category").HasMaxLength(16).IsRequired();
            b.Property(x => x.CommissionPercent).HasColumnName("commission_percent").HasPrecision(5, 2);
            b.Property(x => x.ValidFromUtc).HasColumnName("valid_from_utc");
            b.Property(x => x.ValidToUtc).HasColumnName("valid_to_utc");
            b.HasOne(x => x.DealerUser).WithMany().HasForeignKey(x => x.DealerUserId);
        });

        modelBuilder.Entity<LedgerAccount>(b =>
        {
            b.ToTable("ledger_accounts");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.TenantId).HasColumnName("tenant_id");
            b.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            b.Property(x => x.AccountType).HasColumnName("account_type").HasMaxLength(32).IsRequired();
            b.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();
            b.HasOne(x => x.OwnerUser).WithMany().HasForeignKey(x => x.OwnerUserId);
        });

        modelBuilder.Entity<LedgerTransaction>(b =>
        {
            b.ToTable("ledger_transactions");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.TenantId).HasColumnName("tenant_id");
            b.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(64).IsRequired();
            b.Property(x => x.BkashTrxId).HasColumnName("bkash_trx_id").HasMaxLength(64);
            b.Property(x => x.ReferenceAssetId).HasColumnName("reference_asset_id");
            b.Property(x => x.Description).HasColumnName("description").HasMaxLength(200);
            b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            b.HasOne(x => x.ReferenceAsset).WithMany().HasForeignKey(x => x.ReferenceAssetId);
        });

        modelBuilder.Entity<LedgerEntry>(b =>
        {
            b.ToTable("ledger_entries");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.TenantId).HasColumnName("tenant_id");
            b.Property(x => x.TransactionId).HasColumnName("transaction_id");
            b.Property(x => x.AccountId).HasColumnName("account_id");
            b.Property(x => x.Debit).HasColumnName("debit").HasPrecision(12, 2);
            b.Property(x => x.Credit).HasColumnName("credit").HasPrecision(12, 2);
            b.Property(x => x.CommissionRateSnapshot).HasColumnName("commission_rate_snapshot").HasPrecision(5, 2);
            b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            b.HasOne(x => x.Transaction).WithMany(x => x.Entries).HasForeignKey(x => x.TransactionId);
            b.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId);
        });
    }

    private static void ConfigureTelemetryEntity<T>(ModelBuilder modelBuilder, string tableName)
        where T : TelemetryRecordBase
    {
        modelBuilder.Entity<T>(b =>
        {
            b.ToTable(tableName);
            b.HasKey(x => new { x.AssetId, x.DeviceTimestampUtc, x.PacketType });
            b.Property(x => x.AssetId).HasColumnName("asset_id");
            b.Property(x => x.DeviceTimestampUtc).HasColumnName("device_timestamp_utc");
            b.Property(x => x.PacketType).HasColumnName("packet_type");
            b.Property(x => x.TenantId).HasColumnName("tenant_id");
            b.Property(x => x.ServerReceivedUtc).HasColumnName("server_received_utc");
            b.Property(x => x.Imei).HasColumnName("imei").HasMaxLength(15).IsRequired();
            b.Property(x => x.Location).HasColumnName("location").HasColumnType("geography (Point, 4326)");
            b.Property(x => x.LocationSource).HasColumnName("location_source").HasMaxLength(12).IsRequired();
            b.Property(x => x.AccuracyM).HasColumnName("accuracy_m");
            b.Property(x => x.SpeedKmh).HasColumnName("speed_kmh");
            b.Property(x => x.Course).HasColumnName("course");
            b.Property(x => x.Satellites).HasColumnName("satellites");
            b.Property(x => x.IsGpsValid).HasColumnName("is_gps_valid");
            b.Property(x => x.Ignition).HasColumnName("ignition");
            b.Property(x => x.MainPowerConnected).HasColumnName("main_power_connected");
            b.Property(x => x.BatteryPercent).HasColumnName("battery_percent");
            b.Property(x => x.ExternalVoltage).HasColumnName("external_voltage");
            b.Property(x => x.GsmSignal).HasColumnName("gsm_signal");
            b.Property(x => x.AlarmCode).HasColumnName("alarm_code").HasMaxLength(32);
            b.Property(x => x.IsHistoricalReplay).HasColumnName("is_historical_replay");
            b.Property(x => x.IsClockSkewed).HasColumnName("is_clock_skewed");
            b.Property(x => x.AttributesJsonb).HasColumnName("attributes_jsonb").HasColumnType("jsonb");
        });
    }
}

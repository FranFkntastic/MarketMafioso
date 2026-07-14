using Newtonsoft.Json;
using MarketMafioso.Squire;

namespace MarketMafioso.Tests.Squire;

public sealed class SquireConfigurationTests
{
    [Fact]
    public void PlayerSignedGearProtection_DefaultsOff()
    {
        var config = new SquireConfiguration();

        Assert.False(config.ProtectPlayerSignedGear);
        Assert.False(config.ProtectFutureLevelingGearOptIn);
        Assert.False(config.ShowNonEquipment);
        Assert.True(config.ProtectBlueAndPurpleGear);
        Assert.False(config.AllowRiskyMateriaRetrieval);
        Assert.True(config.RecoverFromKnockout);
        Assert.True(config.WaitForCombatToEnd);
        Assert.Equal(90, config.CombatRecoveryTimeoutSeconds);
        Assert.False(config.LeaveDutyToExecute);
        Assert.True(config.PauseGatherBuddyReborn);
        Assert.True(config.PauseQuestionable);
        Assert.True(config.PauseArtisan);
        Assert.True(config.CloseSafeUserMenus);
    }

    [Fact]
    public void LegacyImplicitSignedGearDefault_DoesNotBecomeAnOptIn()
    {
        var config = JsonConvert.DeserializeObject<SquireConfiguration>(
            "{\"ProtectSignedGear\":true,\"ProtectFutureLevelingGear\":true}")!;

        Assert.False(config.ProtectPlayerSignedGear);
        Assert.False(config.ProtectFutureLevelingGearOptIn);
    }

    [Fact]
    public void LegacyItemPolicies_MigrateIntoCanonicalRulesOnce()
    {
        var config = new SquireConfiguration();
#pragma warning disable CS0618
        config.ExcludedItemIdsByCharacter["42"] = [100, 100];
        config.DuplicateRetentionByCharacter["42"] =
        [
            new() { ItemId = 200, IsHighQuality = true, MinimumCopies = 2 },
            new() { ItemId = 200, IsHighQuality = true, MinimumCopies = 4 },
        ];
#pragma warning restore CS0618

        Assert.True(SquireRuleMigration.Migrate(config));
        var rules = config.RulesByCharacter["42"];
        Assert.Single(rules, rule => rule is { Kind: SquireRuleKind.ProtectItem, ItemId: 100, Quality: SquireRuleQuality.Any });
        Assert.Equal(4, Assert.Single(rules, rule => rule is
            { Kind: SquireRuleKind.RetainCopies, ItemId: 200, Quality: SquireRuleQuality.HighQuality }).MinimumCopies);
#pragma warning disable CS0618
        Assert.Empty(config.ExcludedItemIdsByCharacter);
        Assert.Empty(config.DuplicateRetentionByCharacter);
#pragma warning restore CS0618
        Assert.False(SquireRuleMigration.Migrate(config));
        Assert.Equal(2, rules.Count);
    }

    [Fact]
    public void LegacySettingsPageSelection_MigratesToUnifiedRulesPage()
    {
        var config = new Configuration { SettingsSelectedPageId = "squire.duplicates" };

        Assert.True(SquireRuleMigration.Migrate(config));
        Assert.Equal("squire.rules", config.SettingsSelectedPageId);
    }

    [Fact]
    public void Policy_UsesOnlyEnabledRulesAndKeepsRuleIdentity()
    {
        var active = new SquireRule(Guid.NewGuid(), SquireRuleKind.RetainCopies, 100,
            SquireRuleQuality.NormalQuality, 3, true, "Retainer hand-me-down");
        var disabled = new SquireRule(Guid.Empty, SquireRuleKind.RetainCopies, 0,
            SquireRuleQuality.Any, 0, false, "Broken but disabled reservation");
        var policy = new SquireProtectionPolicy(Rules: [active, disabled]);

        Assert.Equal(3, policy.MinimumCopiesToKeep(100, false));
        Assert.Equal(active.Id, Assert.Single(policy.MatchingRules(100, false)).Id);
        Assert.Empty(policy.ValidationErrors);
    }

    [Fact]
    public void RuleStore_ManagesProtectionAndRetentionThroughOneCollection()
    {
        var config = new Configuration();
        var saves = 0;
        var store = new SquireRuleStore(config, () => saves++);

        store.SetItemProtection(42, 100, true, "Do not dismantle");
        store.SetRetention(42, 200, true, 3, "Retainer hand-me-down");

        var rules = store.Get(42);
        Assert.Equal(2, rules.Count);
        Assert.True(store.CreatePolicy(42).IsItemProtected(100));
        Assert.Equal(3, store.CreatePolicy(42).MinimumCopiesToKeep(200, true));
        var retention = Assert.Single(rules, rule => rule.Kind == SquireRuleKind.RetainCopies);
        store.Update(42, retention.Id, enabled: false);
        Assert.Equal(0, store.CreatePolicy(42).MinimumCopiesToKeep(200, true));
        store.Remove(42, retention.Id);
        Assert.Single(store.Get(42));
        Assert.Equal(4, saves);
    }
}

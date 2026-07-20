using System.Collections.Immutable;
using System.Globalization;
using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Outfitter.Crafting;

namespace MarketMafioso.Tests.Squire;

public sealed class OutfitterCraftingContractsTests
{
    private static readonly Guid CrafterGeneration = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly CharacterScope CrafterCharacter = new(1234, "Test Crafter", 74);

    [Fact]
    public void CompleteNqPlanAndCostComparison_ValidateAsPassiveContracts()
    {
        var plan = Plan();
        var comparison = Comparison(plan);

        Assert.True(plan.Validate(requireEconomyReady: true).IsValid);
        Assert.True(comparison.Validate().IsValid);
        Assert.IsType<EquipmentOfferAllocationKey>(comparison.ComparedAllocation.AllocationKey);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("sub")]
    public void EconomyReadyPlan_RejectsMasterRootOrSubcraft(string nodeId)
    {
        var plan = Plan();
        plan = plan with
        {
            ExpandedNodes = plan.ExpandedNodes
                .Select(node => node.NodeId == nodeId
                    ? node with
                    {
                        RecipeUnlockItemId = 999,
                        ResolvedRecipe = node.ResolvedRecipe! with { RecipeUnlockItemId = 999 },
                    }
                    : node)
                .ToImmutableArray(),
        };

        var validation = plan.Validate(requireEconomyReady: true);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("non-master", StringComparison.Ordinal));
    }

    [Fact]
    public void ActiveJobEligibility_RequiresExactCrafterJobAndLevelEvidence()
    {
        var baseline = Plan();
        var wrongJob = ChangeEligibility(baseline, "root", evidence => evidence with { ObservedClassJobId = 9 });
        var insufficientLevel = ChangeEligibility(baseline, "root", evidence => evidence with { ObservedLevel = 89 });
        var unproven = ChangeEligibility(baseline, "root", evidence => evidence with
        {
            State = OutfitterCraftEligibilityState.Unproven,
            ObservedClassJobId = 0,
            ObservedLevel = 0,
            Diagnostic = "Active crafter job was not observed.",
        });
        var contradictoryIneligible = ChangeEligibility(baseline, "root", evidence => evidence with
        {
            State = OutfitterCraftEligibilityState.ProvenIneligible,
            Diagnostic = "Contradictory fixture.",
        });

        Assert.Contains(wrongJob.Validate().Errors, error => error.Contains("active crafting job", StringComparison.Ordinal));
        Assert.Contains(insufficientLevel.Validate().Errors, error => error.Contains("active crafting job", StringComparison.Ordinal));
        Assert.Contains(contradictoryIneligible.Validate().Errors, error => error.Contains("ineligibility evidence", StringComparison.Ordinal));
        Assert.True(unproven.Validate().IsValid);
        Assert.Contains(unproven.Validate(requireEconomyReady: true).Errors, error => error.Contains("proven active-job", StringComparison.Ordinal));
    }

    [Fact]
    public void HqRequirement_RemainsDisplayOnlyWithoutCapabilityProof()
    {
        var plan = WithRootQuality(Plan(), EquipmentQuality.High, proof: null);
        var comparison = Comparison(plan, CraftCostComparisonStatus.DisplayOnly, "HQ capability is not proven.");

        Assert.True(plan.Validate().IsValid);
        Assert.False(plan.Validate(requireEconomyReady: true).IsValid);
        Assert.True(comparison.Validate().IsValid);
        Assert.Contains(plan.Validate(requireEconomyReady: true).Errors, error => error.Contains("HQ", StringComparison.Ordinal));
    }

    [Fact]
    public void HqCapabilityProof_MustMatchRecipeItemQuantityAndCrafterEvidence()
    {
        var baseline = Plan();
        var validProof = HqProof(baseline.ExpandedNodes.Single(node => node.NodeId == "root"));
        var valid = WithRootQuality(baseline, EquipmentQuality.High, validProof);
        var wrongItem = WithRootQuality(baseline, EquipmentQuality.High, validProof with { ItemId = 999 });
        var wrongCrafterEvidence = WithRootQuality(baseline, EquipmentQuality.High, validProof with { CrafterEvidenceId = "other-evidence" });

        Assert.True(valid.Validate(requireEconomyReady: true).IsValid);
        Assert.Contains(wrongItem.Validate().Errors, error => error.Contains("capability proof", StringComparison.Ordinal));
        Assert.Contains(wrongCrafterEvidence.Validate().Errors, error => error.Contains("capability proof", StringComparison.Ordinal));
    }

    [Fact]
    public void VendorMaterialSource_UsesVendorCatalogIdentityWithoutMarketLineage()
    {
        var plan = VendorOnlyPlan();

        var validation = plan.Validate(requireEconomyReady: true);

        Assert.True(validation.IsValid);
        Assert.Null(plan.MarketEvidence);
        var source = Assert.IsType<OutfitterGilVendorMaterialSourceIdentity>(plan.TerminalMaterials.Single().Source);
        Assert.Equal(20u, source.ShopId);
        Assert.Equal(30u, source.VendorId);
        Assert.Equal(40u, source.TerritoryId);
    }

    [Fact]
    public void MarketMaterialSource_RequiresExactPlanEvidenceLineage()
    {
        var plan = Plan();
        var marketLine = plan.TerminalMaterials.Single(line => line.Source.Kind == OutfitterMaterialSourceKind.MarketListing);
        var market = Assert.IsType<OutfitterMarketMaterialSourceIdentity>(marketLine.Source);
        var mismatched = marketLine with { Source = market with { EvidenceRevision = market.EvidenceRevision + 1 } };
        plan = plan with
        {
            TerminalMaterials = plan.TerminalMaterials
                .Select(line => line.MaterialKey == mismatched.MaterialKey ? mismatched : line)
                .ToImmutableArray(),
        };

        var validation = plan.Validate();

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("exact market evidence", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_RejectsCircularAmbiguousOverDepthAndIncompleteMaterialTrees()
    {
        var baseline = Plan();
        var circular = baseline with
        {
            ExpandedNodes = baseline.ExpandedNodes
                .Select(node => node.NodeId == "root" ? node with { ParentNodeId = "sub" } : node)
                .ToImmutableArray(),
        };
        var ambiguous = baseline with { ExpandedNodes = baseline.ExpandedNodes.Add(baseline.ExpandedNodes[1]) };
        var overDepth = baseline with { MaximumDepth = 1 };
        var incomplete = baseline with { TerminalMaterials = baseline.TerminalMaterials[..1] };

        Assert.Contains(circular.Validate().Errors, error => error.Contains("circular", StringComparison.Ordinal));
        Assert.Contains(ambiguous.Validate().Errors, error => error.Contains("ambiguous", StringComparison.Ordinal));
        Assert.Contains(overDepth.Validate().Errors, error => error.Contains("maximum recipe depth", StringComparison.Ordinal));
        Assert.Contains(incomplete.Validate().Errors, error => error.Contains("completely cover", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_RejectsIncorrectExpandedParentQuantity()
    {
        var plan = Plan() with
        {
            ExpandedNodes = Plan().ExpandedNodes
                .Select(node => node.NodeId == "ore" ? node with { RequiredQuantity = 5 } : node)
                .ToImmutableArray(),
        };

        var validation = plan.Validate();

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("expanded parent recipe quantity", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_RequiresExpandedChildrenToMatchFrozenRecipeResolution()
    {
        var plan = Plan();
        plan = plan with
        {
            ExpandedNodes = plan.ExpandedNodes
                .Select(node => node.NodeId == "root"
                    ? node with
                    {
                        ResolvedRecipe = node.ResolvedRecipe! with
                        {
                            Ingredients = node.ResolvedRecipe.Ingredients
                                .Select(ingredient => ingredient.ChildNodeId == "cloth"
                                    ? ingredient with { QuantityPerCraft = ingredient.QuantityPerCraft + 1 }
                                    : ingredient)
                                .ToImmutableArray(),
                        },
                    }
                    : node)
                .ToImmutableArray(),
        };

        var validation = plan.Validate();

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("frozen static recipe", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_RejectsUndefinedNodeKinds()
    {
        var plan = Plan() with
        {
            ExpandedNodes = Plan().ExpandedNodes
                .Select(node => node.NodeId == "ore" ? node with { Kind = (OutfitterCraftNodeKind)99 } : node)
                .ToImmutableArray(),
        };

        var validation = plan.Validate();

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("unsupported node kind", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_AggregatesRepeatedMarketListingAvailability()
    {
        var plan = Plan();
        var marketLine = plan.TerminalMaterials.Single(line => line.Source is OutfitterMarketMaterialSourceIdentity);
        var source = (OutfitterMarketMaterialSourceIdentity)marketLine.Source;
        plan = plan with
        {
            TerminalMaterials =
            [
                marketLine with { RequiredQuantity = 2, Source = source with { AvailableQuantity = 3 } },
                marketLine with { RequiredQuantity = 2, Source = source with { AvailableQuantity = 3 } },
                plan.TerminalMaterials.Single(line => line.Source is OutfitterGilVendorMaterialSourceIdentity),
            ],
        };

        var validation = plan.Validate();

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("beyond its available quantity", StringComparison.Ordinal));
    }

    [Fact]
    public void CrafterAndHqEvidence_MustShareOneFreshObservationEnvelope()
    {
        var baseline = Plan();
        var mixedCharacter = ChangeEligibility(
            baseline,
            "root",
            evidence => evidence with { Character = new CharacterScope(9999, "Other Crafter", 74) });
        var futureEvidence = ChangeEligibility(
            baseline,
            "root",
            evidence => evidence with { ObservedAtUtc = baseline.BuiltAtUtc.AddSeconds(1) });
        var validProof = HqProof(baseline.ExpandedNodes.Single(node => node.NodeId == "root"));
        var mixedGeneration = WithRootQuality(
            baseline,
            EquipmentQuality.High,
            validProof with { CrafterEvidenceGenerationId = Guid.Parse("99999999-8888-7777-6666-555555555555") });

        Assert.Contains(mixedCharacter.Validate().Errors, error => error.Contains("active-job eligibility", StringComparison.Ordinal));
        Assert.Contains(futureEvidence.Validate().Errors, error => error.Contains("active-job eligibility", StringComparison.Ordinal));
        Assert.Contains(mixedGeneration.Validate().Errors, error => error.Contains("capability proof", StringComparison.Ordinal));
    }

    [Fact]
    public void CostComparison_FailsClosedForMalformedRecords()
    {
        var plan = Plan();
        var nullAllocation = Comparison(plan) with { ComparedAllocation = null! };
        var malformedPlan = plan with { TerminalMaterials = default };
        var nullCrafterObservation = plan with { CrafterObservation = null! };
        var malformedComparison = Comparison(plan) with
        {
            Plan = malformedPlan,
            PlanIdentity = new("malformed"),
        };

        var nullAllocationValidation = nullAllocation.Validate();
        var malformedPlanValidation = malformedComparison.Validate();
        var nullCrafterObservationValidation = nullCrafterObservation.Validate();

        Assert.False(nullAllocationValidation.IsValid);
        Assert.False(malformedPlanValidation.IsValid);
        Assert.False(nullCrafterObservationValidation.IsValid);
        Assert.Contains(nullAllocationValidation.Errors, error => error.Contains("typed exact offer identity", StringComparison.Ordinal));
        Assert.Contains(malformedPlanValidation.Errors, error => error.Contains("initialized", StringComparison.Ordinal));
        Assert.Contains(nullCrafterObservationValidation.Errors, error => error.Contains("crafter observation", StringComparison.Ordinal));
    }

    [Fact]
    public void CostComparison_RejectsTypedAllocationIdentityMismatch()
    {
        var plan = Plan();
        var comparison = Comparison(plan) with
        {
            ComparedAllocation = new(
                new(
                    new(999, EquipmentQuality.Normal, EquipmentAcquisitionSourceKind.MarketBoard, "market:999:nq"),
                    "listing-999"),
                1,
                175),
        };

        var unsupportedSource = Comparison(plan) with
        {
            ComparedAllocation = new(
                new(
                    new(plan.GearItemId, plan.GearQuality, (EquipmentAcquisitionSourceKind)99, "unsupported"),
                    null),
                plan.GearQuantity,
                175),
        };

        var validation = comparison.Validate();
        var unsupportedSourceValidation = unsupportedSource.Validate();

        Assert.False(validation.IsValid);
        Assert.False(unsupportedSourceValidation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("typed exact offer identity", StringComparison.Ordinal));
        Assert.Contains(unsupportedSourceValidation.Errors, error => error.Contains("typed exact offer identity", StringComparison.Ordinal));
    }

    [Fact]
    public void CostComparison_RejectsPlanIdentityAndBurdenMismatch()
    {
        var plan = Plan();
        var comparison = Comparison(plan) with
        {
            PlanIdentity = new("BAD"),
            Burden = new(99, 98, 97, 96, 95),
        };

        var validation = comparison.Validate();

        Assert.Contains(validation.Errors, error => error.Contains("structural craft-plan identity", StringComparison.Ordinal));
        Assert.Contains(validation.Errors, error => error.Contains("burden", StringComparison.Ordinal));
    }

    [Fact]
    public void CostComparison_RejectsCheckedGilOverflow()
    {
        var plan = VendorOnlyPlan();
        plan = plan with
        {
            ExpandedNodes = plan.ExpandedNodes
                .Select(node => node.NodeId switch
                {
                    "root" => node with
                    {
                        ResolvedRecipe = node.ResolvedRecipe! with
                        {
                            Ingredients =
                            [
                                new("cloth", 400, EquipmentQuality.Normal, uint.MaxValue),
                                new("crystal", 401, EquipmentQuality.Normal, uint.MaxValue),
                            ],
                        },
                    },
                    "cloth" => node with { RequiredQuantity = uint.MaxValue, QuantityPerParentCraft = uint.MaxValue },
                    _ => node,
                })
                .Append(new OutfitterCraftNode(
                    "crystal",
                    "root",
                    OutfitterCraftNodeKind.Material,
                    401,
                    EquipmentQuality.Normal,
                    uint.MaxValue,
                    uint.MaxValue))
                .ToImmutableArray(),
            TerminalMaterials =
            [
                VendorMaterial(400, uint.MaxValue, uint.MaxValue),
                VendorMaterial(401, uint.MaxValue, uint.MaxValue),
            ],
        };
        var comparison = Comparison(VendorOnlyPlan()) with
        {
            Plan = plan,
            PlanIdentity = plan.ComputeStructuralIdentity(),
            TotalGil = 0,
            EffectiveUnitGil = 0,
            ComparedAllocation = Comparison(VendorOnlyPlan()).ComparedAllocation with { TotalGil = 0 },
            SavingsGil = 0,
            Burden = Burden(plan),
        };

        var validation = comparison.Validate();

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("overflowed", StringComparison.Ordinal));
    }

    [Fact]
    public void StructuralIdentity_IsDeterministicAcrossCollectionOrderAndRuntimeMetadata()
    {
        var first = Plan();
        var second = first with
        {
            PlanId = "another-runtime-id",
            BuiltAtUtc = first.BuiltAtUtc.AddHours(1),
            ExpandedNodes = first.ExpandedNodes.Reverse().ToImmutableArray(),
            TerminalMaterials = first.TerminalMaterials.Reverse().ToImmutableArray(),
        };
        var tiedSources = VendorOnlyPlan();
        var vendorLine = tiedSources.TerminalMaterials.Single();
        var vendor = (OutfitterGilVendorMaterialSourceIdentity)vendorLine.Source;
        tiedSources = tiedSources with
        {
            TerminalMaterials =
            [
                vendorLine with { RequiredQuantity = 1 },
                vendorLine with
                {
                    RequiredQuantity = 2,
                    Source = vendor with { CatalogVersion = "lumina-7.52" },
                },
            ],
        };

        Assert.Equal(first.ComputeStructuralIdentity(), second.ComputeStructuralIdentity());
        Assert.Equal(
            tiedSources.ComputeStructuralIdentity(),
            (tiedSources with { TerminalMaterials = tiedSources.TerminalMaterials.Reverse().ToImmutableArray() }).ComputeStructuralIdentity());
    }

    [Fact]
    public void StructuralIdentity_IsInvariantAcrossProcessCulture()
    {
        var plan = Plan();
        var expected = plan.ComputeStructuralIdentity();
        var previousCulture = CultureInfo.CurrentCulture;
        var customCulture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        customCulture.NumberFormat.NegativeSign = "~";
        try
        {
            CultureInfo.CurrentCulture = customCulture;

            Assert.Equal(expected, plan.ComputeStructuralIdentity());
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void StructuralIdentity_ChangesWithProofAndSourceEvidence()
    {
        var baseline = Plan();
        var changedRoot = baseline with { RootNodeId = "sub" };
        var changedEligibility = ChangeEligibility(baseline, "root", evidence => evidence with { EvidenceId = "new-crafter-evidence" });
        var marketLine = baseline.TerminalMaterials.Single(line => line.Source.Kind == OutfitterMaterialSourceKind.MarketListing);
        var changedPrice = baseline with
        {
            TerminalMaterials = baseline.TerminalMaterials
                .Select(line => line == marketLine
                    ? line with { Source = ((OutfitterMarketMaterialSourceIdentity)line.Source) with { UnitPriceGil = line.Source.UnitPriceGil + 1 } }
                    : line)
                .ToImmutableArray(),
        };

        Assert.NotEqual(baseline.ComputeStructuralIdentity(), changedRoot.ComputeStructuralIdentity());
        Assert.NotEqual(baseline.ComputeStructuralIdentity(), changedEligibility.ComputeStructuralIdentity());
        Assert.NotEqual(baseline.ComputeStructuralIdentity(), changedPrice.ComputeStructuralIdentity());
    }

    private static OutfitterCraftPlan Plan()
    {
        var generation = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var nodes = ImmutableArray.Create(
            CraftNode(
                "root",
                null,
                100,
                EquipmentQuality.Normal,
                1,
                0,
                500,
                1,
                8,
                90,
                [
                    new("sub", 200, EquipmentQuality.Normal, 2),
                    new("cloth", 400, EquipmentQuality.Normal, 3),
                ]),
            CraftNode(
                "sub",
                "root",
                200,
                EquipmentQuality.Normal,
                2,
                2,
                501,
                1,
                8,
                80,
                [new("ore", 300, EquipmentQuality.Normal, 2)]),
            new OutfitterCraftNode("ore", "sub", OutfitterCraftNodeKind.Material, 300, EquipmentQuality.Normal, 4, 2),
            new OutfitterCraftNode("cloth", "root", OutfitterCraftNodeKind.Material, 400, EquipmentQuality.Normal, 3, 3));
        var materials = ImmutableArray.Create(
            MarketMaterial(300, 4, 10, "listing-300", generation),
            VendorMaterial(400, 3, 20));
        return new(
            OutfitterCraftPlan.CurrentSchemaVersion,
            "runtime-plan",
            100,
            EquipmentQuality.Normal,
            1,
            "root",
            CrafterObservation(),
            4,
            nodes,
            materials,
            Evidence(generation),
            DateTimeOffset.Parse("2026-07-20T00:01:00Z"),
            ImmutableArray<OutfitterCraftDiagnostic>.Empty);
    }

    private static OutfitterCraftPlan VendorOnlyPlan()
    {
        var root = CraftNode(
            "root",
            null,
            100,
            EquipmentQuality.Normal,
            1,
            0,
            500,
            1,
            8,
            90,
            [new("cloth", 400, EquipmentQuality.Normal, 3)]);
        return new(
            OutfitterCraftPlan.CurrentSchemaVersion,
            "vendor-only-plan",
            100,
            EquipmentQuality.Normal,
            1,
            "root",
            CrafterObservation(),
            2,
            [
                root,
                new OutfitterCraftNode("cloth", "root", OutfitterCraftNodeKind.Material, 400, EquipmentQuality.Normal, 3, 3),
            ],
            [VendorMaterial(400, 3, 20)],
            null,
            DateTimeOffset.Parse("2026-07-20T00:01:00Z"),
            ImmutableArray<OutfitterCraftDiagnostic>.Empty);
    }

    private static OutfitterCraftNode CraftNode(
        string nodeId,
        string? parentNodeId,
        uint itemId,
        EquipmentQuality quality,
        uint requiredQuantity,
        uint quantityPerParentCraft,
        uint recipeId,
        uint recipeOutputQuantity,
        uint requiredClassJobId,
        int requiredLevel,
        ImmutableArray<OutfitterResolvedRecipeIngredient> ingredients)
    {
        var evidence = new OutfitterCraftEligibilityEvidence(
            OutfitterCraftEligibilityState.ProvenEligible,
            $"crafter-{nodeId}",
            CrafterGeneration,
            11,
            DateTimeOffset.Parse("2026-07-20T00:00:15Z"),
            CrafterCharacter,
            nodeId,
            recipeId,
            requiredClassJobId,
            requiredLevel,
            requiredClassJobId,
            100);
        var recipe = new OutfitterResolvedRecipeSnapshot(
            "craft-architect-static-recipes",
            "v1",
            $"recipe-{recipeId}-fingerprint",
            recipeId,
            itemId,
            recipeOutputQuantity,
            requiredClassJobId,
            requiredLevel,
            0,
            ingredients);
        return new(
            nodeId,
            parentNodeId,
            OutfitterCraftNodeKind.Craft,
            itemId,
            quality,
            requiredQuantity,
            quantityPerParentCraft,
            recipeId,
            recipeOutputQuantity,
            0,
            recipe,
            evidence);
    }

    private static OutfitterTerminalMaterialLine MarketMaterial(
        uint itemId,
        uint quantity,
        uint unitGil,
        string listingId,
        Guid generation)
    {
        var key = OutfitterCraftPlan.MaterialKey(itemId, EquipmentQuality.Normal);
        var source = new OutfitterMarketMaterialSourceIdentity(
            itemId,
            EquipmentQuality.Normal,
            unitGil,
            quantity,
            listingId,
            74,
            "Test World",
            DateTimeOffset.Parse("2026-07-20T00:00:00Z"),
            "source-r1",
            generation,
            7);
        return new(key, itemId, EquipmentQuality.Normal, quantity, source);
    }

    private static OutfitterTerminalMaterialLine VendorMaterial(uint itemId, uint quantity, uint unitGil)
    {
        var key = OutfitterCraftPlan.MaterialKey(itemId, EquipmentQuality.Normal);
        var source = new OutfitterGilVendorMaterialSourceIdentity(
            itemId,
            EquipmentQuality.Normal,
            unitGil,
            20,
            30,
            40,
            "Test Merchant",
            "Test Territory",
            "lumina-7.51");
        return new(key, itemId, EquipmentQuality.Normal, quantity, source);
    }

    private static CraftCostComparison Comparison(
        OutfitterCraftPlan plan,
        CraftCostComparisonStatus status = CraftCostComparisonStatus.Complete,
        string? diagnostic = null)
    {
        var total = plan.TerminalMaterials.Aggregate(0ul, (sum, line) => sum + (ulong)line.RequiredQuantity * line.Source.UnitPriceGil);
        var comparedTotal = checked(total + 75);
        return new(
            CraftCostComparison.CurrentSchemaVersion,
            "comparison-1",
            status,
            plan,
            plan.ComputeStructuralIdentity(),
            total,
            checked((uint)((total + plan.GearQuantity - 1) / plan.GearQuantity)),
            new(
                new(
                    new(plan.GearItemId, plan.GearQuality, EquipmentAcquisitionSourceKind.MarketBoard, $"market:{plan.GearItemId}:{plan.GearQuality}"),
                    $"listing-{plan.GearItemId}"),
                plan.GearQuantity,
                comparedTotal),
            75,
            Burden(plan),
            DateTimeOffset.Parse("2026-07-20T00:02:00Z"),
            diagnostic is null ? ImmutableArray<string>.Empty : [diagnostic]);
    }

    private static CraftAcquisitionBurden Burden(OutfitterCraftPlan plan)
    {
        var craftNodes = plan.ExpandedNodes.Count(node => node.Kind == OutfitterCraftNodeKind.Craft);
        return new(
            craftNodes,
            Math.Max(0, craftNodes - 1),
            plan.TerminalMaterials.Select(line => line.MaterialKey).Distinct(StringComparer.Ordinal).Count(),
            plan.TerminalMaterials.Count(line => line.Source.Kind == OutfitterMaterialSourceKind.MarketListing),
            plan.TerminalMaterials.Count(line => line.Source.Kind == OutfitterMaterialSourceKind.GilVendor));
    }

    private static OutfitterCrafterObservationIdentity CrafterObservation() => new(
        CrafterCharacter,
        CrafterGeneration,
        11,
        DateTimeOffset.Parse("2026-07-20T00:00:00Z"),
        DateTimeOffset.Parse("2026-07-20T00:10:00Z"));

    private static CraftMarketEvidenceReference Evidence(Guid generation) => new(
        generation,
        7,
        "marketmafioso-outfitter-market-evidence/v1",
        "test",
        "NA");

    private static OutfitterCraftPlan ChangeEligibility(
        OutfitterCraftPlan plan,
        string nodeId,
        Func<OutfitterCraftEligibilityEvidence, OutfitterCraftEligibilityEvidence> change) =>
        plan with
        {
            ExpandedNodes = plan.ExpandedNodes
                .Select(node => node.NodeId == nodeId ? node with { Eligibility = change(node.Eligibility!) } : node)
                .ToImmutableArray(),
        };

    private static OutfitterCraftPlan WithRootQuality(
        OutfitterCraftPlan plan,
        EquipmentQuality quality,
        OutfitterCraftHqCapabilityProof? proof)
    {
        var root = plan.ExpandedNodes.Single(node => node.NodeId == "root");
        return plan with
        {
            GearQuality = quality,
            ExpandedNodes = plan.ExpandedNodes
                .Select(node => node.NodeId == "root" ? node with { Quality = quality, HqCapabilityProof = proof } : node)
                .ToImmutableArray(),
        };
    }

    private static OutfitterCraftHqCapabilityProof HqProof(OutfitterCraftNode node) => new(
        "hq-proof-root",
        "verified-craft-capability",
        "v1",
        node.Eligibility!.EvidenceId,
        node.Eligibility.EvidenceGenerationId,
        node.Eligibility.EvidenceRevision,
        DateTimeOffset.Parse("2026-07-20T00:00:30Z"),
        node.NodeId,
        node.RecipeId,
        node.ItemId,
        EquipmentQuality.High,
        node.RequiredQuantity);
}

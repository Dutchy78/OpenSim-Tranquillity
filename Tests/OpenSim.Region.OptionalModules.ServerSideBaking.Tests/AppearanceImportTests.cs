using System.Text;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.OptionalModules.Avatar.AppearanceImport;
using OpenSim.Services.Interfaces;
using OpenSimNGC.Appearance.Baking;
using Xunit;

namespace OpenSim.Region.OptionalModules.ServerSideBaking.Tests;

/// <summary>
/// <c>appearance import</c>: the pure planner (document → validated plan → LLWearable text), and the importer
/// against in-memory services, down to what the avatar service stores.
/// </summary>
public class AppearanceImportPlannerTests
{
    private static ParamCatalog Catalog => ParamCatalog.Embedded;

    private static AvatarSpec Spec(params WearableSpec[] wearables) => new() { FirstName = "Load", LastName = "Tester", Wearables = wearables.ToList() };

    [Fact]
    public void SliderValuesScaleIntoTheParameterRange()
    {
        var plan = AppearancePlanner.Plan(Spec(new WearableSpec { Type = "shape", Params = new() { ["Height"] = 62, ["Body Thickness"] = 40 } }),
            new ImportDocument { ParamScale = "slider" }, Catalog);

        Assert.True(plan.Ok, string.Join("; ", plan.Errors));
        var shape = Assert.Single(plan.Wearables);
        Assert.Equal(-2.3f + 0.62f * 4.3f, shape.Params[33], 4);   // Height: -2.3 .. 2
        Assert.Equal(-0.7f + 0.40f * 2.2f, shape.Params[34], 4);   // "Body Thickness" is the editor label of 34 "Thickness"
        Assert.Equal(Catalog.TweakablesOf(WearableKind.Shape).Count(), shape.Params.Count);
    }

    [Fact]
    public void ByteAndValueScales()
    {
        var bytes = AppearancePlanner.Plan(new AvatarSpec { FirstName = "a", LastName = "b", ParamScale = "byte", Wearables = new() { new WearableSpec { Type = "shape", Params = new() { ["33"] = 255 } } } }, null, Catalog);
        Assert.Equal(2f, bytes.Wearables[0].Params[33], 5);

        var values = AppearancePlanner.Plan(Spec(new WearableSpec { Type = "skin", Params = new() { ["Pigment"] = 5 } }), null, Catalog);
        Assert.Equal(1f, values.Wearables[0].Params[111]);   // clamped to value_max
        Assert.Contains(values.Warnings, w => w.Contains("clamped"));
    }

    [Fact]
    public void GeneratedWearableRoundTripsThroughTheBakeParser()
    {
        var plan = AppearancePlanner.Plan(Spec(
            new WearableSpec { Type = "skin", Name = "My Skin", Params = new() { ["Freckles"] = 0.5 },
                Textures = new() { ["head_bodypaint"] = "c228d1cf-4b5d-4ba8-84f4-899a0796aa97", ["UpperBodypaint"] = "upper.png" } }), null, Catalog);
        var skin = plan.Wearables.Single();
        var upper = UUID.Random();
        var ids = new Dictionary<TextureSlot, UUID> { [TextureSlot.HeadBodypaint] = skin.Textures[TextureSlot.HeadBodypaint].AssetId, [TextureSlot.UpperBodypaint] = upper };

        var parsed = WearableParser.Parse(AppearancePlanner.ToLLWearable(skin, ids, UUID.Random(), UUID.Random()));

        Assert.Equal(WearableKind.Skin, parsed.Kind);
        Assert.Equal("My Skin", parsed.Name);
        Assert.Equal(0.5f, parsed.Params[165], 3);
        Assert.Equal(upper, parsed.Textures[TextureSlot.UpperBodypaint]);
        Assert.Equal(2, parsed.Textures.Count);
    }

    [Fact]
    public void InvalidDocumentsAreRejectedBeforeAnythingIsWritten()
    {
        var spec = Spec(
            new WearableSpec { Type = "shape" }, new WearableSpec { Type = "shape" },
            new WearableSpec { Type = "cape" },
            new WearableSpec { Type = "hair", Textures = new() { ["upper_shirt"] = "x.png" } });
        spec.Attachments.Add(new AttachmentSpec { Point = "Elbow", AssetId = UUID.Random().ToString() });

        var plan = AppearancePlanner.Plan(spec, null, Catalog);

        Assert.False(plan.Ok);
        Assert.Contains(plan.Errors, e => e.Contains("only one shape"));
        Assert.Contains(plan.Errors, e => e.Contains("unknown type 'cape'"));
        Assert.Contains(plan.Errors, e => e.Contains("does not paint slot UpperShirt"));
        Assert.Contains(plan.Errors, e => e.Contains("'Elbow'"));
    }

    [Fact]
    public void ParametersOfAnotherWearableAreIgnoredWithAWarning()
    {
        var plan = AppearancePlanner.Plan(Spec(new WearableSpec { Type = "skin", Params = new() { ["Height"] = 1, ["nonsense"] = 1 } }), null, Catalog);
        Assert.True(plan.Ok);
        Assert.Contains(plan.Warnings, w => w.Contains("'Height'") && w.Contains("belongs to shape"));
        Assert.Contains(plan.Warnings, w => w.Contains("unknown parameter 'nonsense'"));
        Assert.DoesNotContain(33, plan.Wearables[0].Params.Keys);
    }

    [Fact]
    public void MissingBodyPartsAreListed()
    {
        var plan = AppearancePlanner.Plan(Spec(new WearableSpec { Type = "shape" }), null, Catalog);
        Assert.Equal(new[] { WearableKind.Skin, WearableKind.Hair, WearableKind.Eyes }, plan.MissingBodyParts);
    }

    [Fact]
    public void DocumentShapes()
    {
        Assert.Single(ImportDocumentReader.Parse("[{\"firstName\":\"a\",\"lastName\":\"b\"}]").Avatars);
        Assert.Equal("a", ImportDocumentReader.Parse("{\"firstName\":\"a\",\"lastName\":\"b\"}").Avatars[0].FirstName);
        Assert.Equal(2, ImportDocumentReader.Parse("{ // comment\n \"avatars\": [ {}, {}, ] }").Avatars.Count);
        Assert.Throws<FormatException>(() => ImportDocumentReader.Parse("{ nope"));
    }

    /// <summary>A VisualParams blob as the avatar service stores it (253 bytes, the current avatar_lad.xml send list).</summary>
    internal const string SampleBlob = "31,20,69,0,111,140,25,71,38,0,0,192,43,119,149,140,137,51,25,43,5,37,127,99,25,142,46,71,53,51,66,0,203,255,0,63,0,0,127,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,46,0,0,0,0,0,0,0,0,0,0,0,0,0,76,0,140,127,0,0,117,73,66,85,127,127,0,76,0,100,216,214,204,204,204,51,25,89,76,204,0,107,7,0,160,30,71,132,130,89,0,127,76,127,127,127,112,0,25,53,127,96,84,46,79,122,81,122,63,0,0,0,0,127,127,0,0,0,0,127,0,159,0,0,89,127,51,0,0,63,239,165,147,122,0,5,76,25,68,130,0,214,204,198,0,0,2,30,140,226,255,198,255,255,255,255,255,255,255,255,255,204,0,255,255,255,255,255,255,255,255,255,255,255,0,255,255,255,255,255,0,99,89,255,25,100,255,255,255,255,84,0,0,0,51,0,255,255,255,0,0,25,160,25,160,51,0,25,23,51,0,0,25,0,25,23,51,0,0,25,0,25,23,51,0,25,23,51,0,25,23,51,1,127";

    private static System.Text.Json.JsonElement Json(string s) => System.Text.Json.JsonDocument.Parse(s).RootElement.Clone();

    [Fact]
    public void AVisualParamsBlobBecomesTheBodyPartsAndRoundTrips()
    {
        var spec = new AvatarSpec { FirstName = "a", LastName = "b", VisualParams = Json("\"" + SampleBlob + "\"") };
        var plan = AppearancePlanner.Plan(spec, null, Catalog);

        Assert.True(plan.Ok, string.Join("; ", plan.Errors));
        Assert.Empty(plan.MissingBodyParts);   // generated from the blob, not library defaults
        Assert.Equal(new[] { WearableKind.Shape, WearableKind.Skin, WearableKind.Hair, WearableKind.Eyes }, plan.Wearables.Select(w => w.Kind));

        // every byte a generated wearable carries survives: wearable text → parser → encoder gives the blob back
        var blob = SampleBlob.Split(',').Select(byte.Parse).ToArray();
        var reparsed = plan.Wearables.Select(w => (w.Kind, (IReadOnlyDictionary<int, float>)WearableParser.Parse(
            AppearancePlanner.ToLLWearable(w, new Dictionary<TextureSlot, UUID>(), UUID.Zero, UUID.Zero)).Params)).ToList();
        var send = VisualParamEncoder.SendList(Catalog.Lad);
        var encoded = AppearancePlanner.EncodeVisualParams(Catalog.Lad, reparsed);
        var worn = new HashSet<string> { "shape", "skin", "hair", "eyes" };
        for (var i = 0; i < send.Count; i++)
            if (send[i].Group == 0 && send[i].Wearable is { } w && worn.Contains(w))
                Assert.True(blob[i] == encoded[i], $"param {send[i].Id} {send[i].Name}: blob {blob[i]}, round trip {encoded[i]}");

        // the stored VisualParams are the blob itself
        Assert.Equal(blob, AppearancePlanner.EncodeVisualParams(Catalog.Lad, plan.Wearables.Select(w => (w.Kind, w.Params)), plan));
    }

    [Fact]
    public void ExplicitParamsWinOverTheBlobAndClothingTakesItsTopmostValues()
    {
        var spec = new AvatarSpec
        {
            FirstName = "a", LastName = "b", VisualParams = Json("[" + SampleBlob + "]"),
            Wearables = new() { new WearableSpec { Type = "shape", Params = new() { ["Height"] = 2 } }, new WearableSpec { Type = "shirt" }, new WearableSpec { Type = "shirt" } },
        };
        var plan = AppearancePlanner.Plan(spec, null, Catalog);
        Assert.True(plan.Ok, string.Join("; ", plan.Errors));

        var send = VisualParamEncoder.SendList(Catalog.Lad);
        var blob = SampleBlob.Split(',').Select(byte.Parse).ToArray();
        var vp = AppearancePlanner.EncodeVisualParams(Catalog.Lad, plan.Wearables.Select(w => (w.Kind, w.Params)), plan);
        Assert.Equal(255, vp[send.FindIndex(p => p.Id == 33)]);            // Height set explicitly to its max
        Assert.Equal(blob[send.FindIndex(p => p.Id == 1)], vp[send.FindIndex(p => p.Id == 1)]);

        var shirts = plan.Wearables.Where(w => w.Kind == WearableKind.Shirt).ToList();
        var sleeve = send.FindIndex(p => p.Id == 800);
        Assert.Equal(AppearancePlanner.DefaultWeight(Catalog.Lad.Params[800]), shirts[0].Params[800]);   // under: default
        Assert.Equal(blob[sleeve], VisualParamEncoder.F32ToU8(shirts[1].Params[800], 0, 1));            // top: the blob
    }

    [Fact]
    public void ABlobOfTheWrongLengthIsRejected()
    {
        var plan = AppearancePlanner.Plan(new AvatarSpec { FirstName = "a", LastName = "b", VisualParams = Json("\"1,2,3\"") }, null, Catalog);
        Assert.Contains(plan.Errors, e => e.Contains("has 3 values"));
    }

    [Fact]
    public void APrePhysicsBlobIsThePrefixOfTodaysAndTheRestDefaults()
    {
        var full = SampleBlob.Split(',').Select(byte.Parse).ToArray();
        var legacy = AppearancePlanner.LegacyVisualParamCount(Catalog.Lad);
        Assert.Equal(218, legacy);
        var send = VisualParamEncoder.SendList(Catalog.Lad);
        Assert.Equal(80, send[31].Id);    // "Female [0] / Shape Male [1]" at index 31 in the old viewer's table
        Assert.Equal(10000, send[218].Id); // the first "[NEW]" entry is the first physics parameter

        var plan = AppearancePlanner.Plan(new AvatarSpec { FirstName = "a", LastName = "b", VisualParams = Json("\"" + string.Join(",", full.Take(legacy)) + "\"") }, null, Catalog);

        Assert.True(plan.Ok, string.Join("; ", plan.Errors));
        Assert.Contains(plan.Warnings, w => w.Contains("pre-physics"));
        Assert.Equal(send.Count, plan.VisualParams.Length);
        Assert.Equal(full.Take(legacy), plan.VisualParams.Take(legacy));
        var hover = send.FindIndex(p => p.Id == 11001);
        Assert.Equal(VisualParamEncoder.F32ToU8(0f, -2f, 2f), plan.VisualParams[hover]);
    }

    [Fact]
    public void VisualParamsAreTheViewersEncodingOfTheWornValues()
    {
        var plan = AppearancePlanner.Plan(Spec(new WearableSpec { Type = "shape", Params = new() { ["male"] = 1, ["Height"] = 1.5 } }), null, Catalog);
        var vp = AppearancePlanner.EncodeVisualParams(Catalog.Lad, plan.Wearables.Select(w => (w.Kind, w.Params)));
        var send = VisualParamEncoder.SendList(Catalog.Lad);

        Assert.Equal(send.Count, vp.Length);
        Assert.Equal(255, vp[send.FindIndex(p => p.Id == 80)]);
        Assert.Equal(VisualParamEncoder.F32ToU8(1.5f, -2.3f, 2f), vp[send.FindIndex(p => p.Id == 33)]);
    }
}

public class AppearanceImporterTests
{
    private sealed class Rig
    {
        public readonly FakeUserAccountService Accounts = new();
        public readonly FakeAuthenticationService Auth = new();
        public readonly FakeInventoryService Inventory = new();
        public readonly FakeAssetService Assets = new();
        public readonly FakeAvatarService Avatars = new();
        public readonly AppearanceImporter Importer;

        public Rig()
        {
            Importer = new AppearanceImporter(new ImportServices
            {
                Accounts = Accounts, Authentication = Auth, Inventory = Inventory, Assets = Assets, Avatars = Avatars,
            }, ParamCatalog.Embedded);
            // The library body parts, as the default asset set loads them.
            foreach (var (id, type) in new[] { (AvatarWearable.DEFAULT_BODY_ASSET, 0), (AvatarWearable.DEFAULT_SKIN_ASSET, 1), (AvatarWearable.DEFAULT_HAIR_ASSET, 2), (AvatarWearable.DEFAULT_EYES_ASSET, 3) })
                Assets.Put(new AssetBase(id, "default", (sbyte)AssetType.Bodypart, UUID.Zero.ToString())
                {
                    Data = Encoding.UTF8.GetBytes($"LLWearable version 22\ndefault\n\ntype {type}\nparameters 0\ntextures 0\n"),
                });
        }

        public AvatarImportResult Import(AvatarSpec spec, ImportDocument doc = null, ImportOptions options = null)
            => Importer.Import(AppearancePlanner.Plan(spec, doc, ParamCatalog.Embedded), doc ?? new ImportDocument(), options ?? new ImportOptions());

        public UserAccount Account(string first, string last) => Accounts.GetUserAccount(UUID.Zero, first, last);
        public List<InventoryItemBase> Cof(UUID pid) => Inventory.GetFolderItems(pid, Inventory.GetFolderForType(pid, FolderType.CurrentOutfit).ID);
    }

    private static AvatarSpec FullSpec(bool create = true) => new()
    {
        FirstName = "Load", LastName = "Tester01",
        Account = new AccountSpec { Create = create, Password = "pw" },
        OutfitName = "Test Look",
        Wearables = new()
        {
            new WearableSpec { Type = "shape", Params = new() { ["male"] = 1, ["Height"] = 0.5 } },
            new WearableSpec { Type = "skin" },
            new WearableSpec { Type = "hair" },
            new WearableSpec { Type = "eyes" },
            new WearableSpec { Type = "shirt", Name = "Under" },
            new WearableSpec { Type = "shirt", Name = "Over" },
        },
    };

    [Fact]
    public void ImportWritesAssetsItemsCofAndAppearance()
    {
        var rig = new Rig();
        var hair = UUID.Random();
        rig.Assets.Put(new AssetBase(hair, "mesh hair", (sbyte)AssetType.Object, UUID.Zero.ToString()) { Data = new byte[] { 1 } });
        var spec = FullSpec();
        spec.Attachments.Add(new AttachmentSpec { Point = "Skull", AssetId = hair.ToString(), Name = "Mesh hair" });

        var result = rig.Import(spec);

        Assert.True(result.Success, string.Join("\n", result.Messages));
        var account = rig.Account("Load", "Tester01");
        Assert.NotNull(account);
        Assert.Equal("pw", rig.Auth.Passwords[account.PrincipalID]);
        var pid = account.PrincipalID;

        // six generated wearables, each a parseable LLWearable of the right asset type
        Assert.Equal(6, rig.Assets.Stored.Count);
        foreach (var a in rig.Assets.Stored)
        {
            var parsed = WearableParser.Parse(Encoding.UTF8.GetString(a.Data));
            Assert.Equal(WearableKinds.IsBodyPart(parsed.Kind) ? (sbyte)AssetType.Bodypart : (sbyte)AssetType.Clothing, a.Type);
        }

        // items live in Clothing/Test Look
        var clothing = rig.Inventory.GetFolderForType(pid, FolderType.Clothing);
        var outfit = rig.Inventory.GetFolderContent(pid, clothing.ID).Folders.Single(f => f.Name == "Test Look");
        Assert.Equal(7, rig.Inventory.GetFolderItems(pid, outfit.ID).Count);   // 6 wearables + 1 attachment

        // COF: a link per worn item; clothing links carry the viewer's ordering strings
        var cof = rig.Cof(pid);
        Assert.Equal(7, cof.Count);
        Assert.All(cof, l => Assert.Equal((int)AssetType.Link, l.AssetType));
        var shirts = cof.Where(l => l.InvType == (int)InventoryType.Wearable && l.Flags == (uint)WearableType.Shirt).OrderBy(l => l.Description).ToList();
        Assert.Equal(new[] { "@400", "@401" }, shirts.Select(l => l.Description));
        Assert.Equal("Under", shirts[0].Name);
        Assert.Contains(cof, l => l.InvType == (int)InventoryType.Object && l.Name == "Mesh hair");
        Assert.All(cof, l => Assert.True(rig.Inventory.Items.ContainsKey(l.AssetID), "link target exists"));

        // appearance, read back through AvatarData as the avatar service stores it
        var app = rig.Avatars.GetAppearance(pid);
        Assert.Equal(1, app.Serial);
        Assert.Equal(VisualParamEncoder.SendList(ParamCatalog.Embedded.Lad).Count, app.VisualParams.Length);
        Assert.Equal(2, app.Wearables[(int)WearableType.Shirt].Count);
        Assert.Equal(shirts[0].AssetID, app.Wearables[(int)WearableType.Shirt][0].ItemID);   // "Under" first, "Over" on top
        Assert.Equal(shirts[1].AssetID, app.Wearables[(int)WearableType.Shirt][1].ItemID);
        foreach (var t in new[] { WearableType.Shape, WearableType.Skin, WearableType.Hair, WearableType.Eyes })
            Assert.Equal(1, app.Wearables[(int)t].Count);
        var att = Assert.Single(app.GetAttachments());
        Assert.Equal((int)AttachmentPoint.Skull, att.AttachPoint);
        Assert.Equal(hair, att.AssetID);
    }

    [Fact]
    public void DryRunWritesNothing()
    {
        var rig = new Rig();
        var result = rig.Import(FullSpec(), options: new ImportOptions { DryRun = true });

        Assert.True(result.Success, string.Join("\n", result.Messages));
        Assert.Empty(rig.Accounts.Accounts);
        Assert.Empty(rig.Assets.Stored);
        Assert.Empty(rig.Inventory.Items);
    }

    [Fact]
    public void AMissingAccountIsNotCreatedUnlessAsked()
    {
        var rig = new Rig();
        var result = rig.Import(FullSpec(create: false));

        Assert.False(result.Success);
        Assert.Contains(result.Messages, m => m.Contains("account does not exist"));
        Assert.Empty(rig.Assets.Stored);

        Assert.True(rig.Import(FullSpec(create: false), options: new ImportOptions { CreateAccounts = true }).Success);
    }

    [Fact]
    public void ReimportTrashesThePreviousFolderAndReplacesTheCof()
    {
        var rig = new Rig();
        Assert.True(rig.Import(FullSpec()).Success);
        var pid = rig.Account("Load", "Tester01").PrincipalID;
        var clothing = rig.Inventory.GetFolderForType(pid, FolderType.Clothing);
        var first = rig.Inventory.GetFolderContent(pid, clothing.ID).Folders.Single(f => f.Name == "Test Look");

        var again = rig.Import(FullSpec());

        Assert.True(again.Success, string.Join("\n", again.Messages));
        Assert.Equal(rig.Inventory.GetFolderForType(pid, FolderType.Trash).ID, rig.Inventory.Folders[first.ID].ParentID);
        Assert.Single(rig.Inventory.GetFolderContent(pid, clothing.ID).Folders, f => f.Name == "Test Look");
        Assert.Equal(6, rig.Cof(pid).Count);
        Assert.Equal(2, rig.Avatars.GetAppearance(pid).Serial);
    }

    [Fact]
    public void MissingBodyPartsWearTheLibraryDefaults()
    {
        var rig = new Rig();
        var spec = new AvatarSpec { FirstName = "Only", LastName = "Shape", Account = new AccountSpec { Create = true, Password = "pw" }, Wearables = new() { new WearableSpec { Type = "shape" } } };

        var result = rig.Import(spec);

        Assert.True(result.Success, string.Join("\n", result.Messages));
        var app = rig.Avatars.GetAppearance(rig.Account("Only", "Shape").PrincipalID);
        Assert.Equal(AvatarWearable.DEFAULT_SKIN_ASSET, app.Wearables[(int)WearableType.Skin][0].AssetID);
        Assert.Equal(AvatarWearable.DEFAULT_HAIR_ASSET, app.Wearables[(int)WearableType.Hair][0].AssetID);
        Assert.Equal(AvatarWearable.DEFAULT_EYES_ASSET, app.Wearables[(int)WearableType.Eyes][0].AssetID);
        Assert.Single(rig.Assets.Stored);   // only the shape was generated
    }

    [Fact]
    public void AnExistingWearableAssetIsWornAsIsAndAMissingOneFailsFirst()
    {
        var rig = new Rig();
        var spec = FullSpec();
        spec.Wearables[2] = new WearableSpec { Type = "hair", AssetId = AvatarWearable.DEFAULT_HAIR_ASSET.ToString() };
        Assert.True(rig.Import(spec).Success);
        Assert.Equal(5, rig.Assets.Stored.Count);

        var rig2 = new Rig();
        var bad = FullSpec();
        bad.Wearables[2] = new WearableSpec { Type = "hair", AssetId = UUID.Random().ToString() };
        var result = rig2.Import(bad);
        Assert.False(result.Success);
        Assert.Empty(rig2.Accounts.Accounts);   // checked before the account was created
        Assert.Empty(rig2.Assets.Stored);
    }

    [Fact]
    public void AMissingTextureFileFailsBeforeAnythingIsWritten()
    {
        var rig = new Rig();
        var spec = FullSpec();
        spec.Wearables[1] = new WearableSpec { Type = "skin", Textures = new() { ["head_bodypaint"] = "does-not-exist.png" } };

        var result = rig.Import(spec, options: new ImportOptions { BaseDirectory = Path.GetTempPath() });

        Assert.False(result.Success);
        Assert.Contains(result.Messages, m => m.Contains("does-not-exist.png"));
        Assert.Empty(rig.Assets.Stored);
        Assert.Empty(rig.Accounts.Accounts);
    }

    [Fact]
    public void KeepingTheOutfitKeepsExistingAttachments()
    {
        var rig = new Rig();
        var hat = UUID.Random();
        rig.Assets.Put(new AssetBase(hat, "hat", (sbyte)AssetType.Object, UUID.Zero.ToString()) { Data = new byte[] { 1 } });
        var first = FullSpec();
        first.Attachments.Add(new AttachmentSpec { Point = "Skull", AssetId = hat.ToString(), Name = "Hat" });
        Assert.True(rig.Import(first).Success);
        var pid = rig.Account("Load", "Tester01").PrincipalID;

        var second = FullSpec();
        second.ReplaceOutfit = false;
        Assert.True(rig.Import(second).Success);

        Assert.Single(rig.Avatars.GetAppearance(pid).GetAttachments());
        Assert.Contains(rig.Cof(pid), l => l.InvType == (int)InventoryType.Object);
        Assert.Equal(6, rig.Cof(pid).Count(l => l.InvType == (int)InventoryType.Wearable));
    }
}

public class AppearanceImporterGridTests
{
    [Fact]
    public void OnAGridTheAccountIsCreatedWithRobustsCreateUser()
    {
        var inventory = new FakeInventoryService();
        var robust = new FakeRobustAccounts(inventory);
        var avatars = new FakeAvatarService();
        var importer = new AppearanceImporter(new ImportServices
        {
            Accounts = robust, Inventory = inventory, Assets = new FakeAssetService(), Avatars = avatars,
        }, ParamCatalog.Embedded);
        var spec = new AvatarSpec
        {
            FirstName = "Grid", LastName = "Tester", Account = new AccountSpec { Create = true, Password = "pw" },
            Wearables = new() { new WearableSpec { Type = "shape" }, new WearableSpec { Type = "skin" }, new WearableSpec { Type = "hair" }, new WearableSpec { Type = "eyes" } },
        };

        var result = importer.Import(AppearancePlanner.Plan(spec, null, ParamCatalog.Embedded), new ImportDocument(), new ImportOptions());

        Assert.True(result.Success, string.Join("\n", result.Messages));
        var account = Assert.Single(robust.Accounts.Values);
        Assert.Equal("pw", robust.Passwords[account.PrincipalID]);
        Assert.Equal(account.PrincipalID, result.PrincipalID);
        Assert.Equal(4, avatars.GetAppearance(account.PrincipalID).Wearables.Take(4).Sum(w => w.Count));
    }
}

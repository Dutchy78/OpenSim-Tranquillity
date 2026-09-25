using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Connectors;
using OpenSim.Services.Interfaces;
using OpenSimNGC.Appearance.Baking;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace OpenSim.Region.OptionalModules.Avatar.AppearanceImport;

/// <summary>The services an import writes through. In a region these are the scene's (local or Robust connectors).</summary>
public sealed class ImportServices
{
    public IUserAccountService Accounts { get; init; }
    public IAuthenticationService Authentication { get; init; }
    public IGridService Grid { get; init; }
    public IGridUserService GridUsers { get; init; }
    public IInventoryService Inventory { get; init; }
    public IAssetService Assets { get; init; }
    public IAvatarService Avatars { get; init; }
    public UUID ScopeID { get; init; }
}

public sealed class ImportOptions
{
    /// <summary>Validate everything and report, write nothing.</summary>
    public bool DryRun { get; init; }

    /// <summary>Create missing accounts even when the avatar's <c>account.create</c> is not set.</summary>
    public bool CreateAccounts { get; init; }

    /// <summary>Directory texture file paths are relative to (the import document's directory).</summary>
    public string BaseDirectory { get; init; } = ".";

    public int MaxTextureSize { get; init; } = TextureFileEncoder.DefaultMaxSize;
    public double TextureQuality { get; init; } = 0.9;
}

public sealed class AvatarImportResult
{
    public string Name { get; init; }
    public UUID PrincipalID { get; set; }
    public bool Success { get; set; }
    public bool AccountCreated { get; set; }
    public int AssetsStored { get; set; }
    public int ItemsCreated { get; set; }
    public List<string> Messages { get; } = new();
}

/// <summary>
/// Writes an <see cref="AvatarPlan"/> into the grid the way a viewer would have left it after wearing the outfit:
/// wearable assets, the inventory items for them, the Current Outfit Folder links (ordered the way
/// <c>CofWearables</c> and the viewer read them), and the avatar service row (wearables, attachments and
/// VisualParams, with no bakes). The next login bakes — on the server when the region has
/// <c>[Appearance] ServerSideBaking</c> on, in the viewer otherwise.
///
/// <para>
/// Everything is checked before anything is written: a missing account that may not be created, a missing
/// existing wearable asset, or a missing texture file fails the avatar with nothing stored. Faults after that point
/// (a service refusing a write) stop the avatar where they happen; the appearance row is written last, so an
/// avatar that failed part-way keeps its previous look.
/// </para>
/// </summary>
public sealed class AppearanceImporter
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(typeof(AppearanceImporter));

    public const string TexturesFolderName = "Textures";

    private readonly ImportServices m_services;
    private readonly ParamCatalog m_catalog;

    /// <summary>Image files already uploaded in this import run, by full path, so a texture shared by many avatars is stored once.</summary>
    private readonly Dictionary<string, UUID> m_uploaded = new(StringComparer.Ordinal);

    public AppearanceImporter(ImportServices services, ParamCatalog catalog)
    {
        m_services = services ?? throw new ArgumentNullException(nameof(services));
        m_catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        if (services.Accounts is null || services.Inventory is null || services.Assets is null || services.Avatars is null)
            throw new ArgumentException("the user account, inventory, asset and avatar services are required", nameof(services));
    }

    private static readonly Dictionary<WearableKind, UUID> LibraryDefaults = new()
    {
        [WearableKind.Shape] = AvatarWearable.DEFAULT_BODY_ASSET,
        [WearableKind.Skin] = AvatarWearable.DEFAULT_SKIN_ASSET,
        [WearableKind.Hair] = AvatarWearable.DEFAULT_HAIR_ASSET,
        [WearableKind.Eyes] = AvatarWearable.DEFAULT_EYES_ASSET,
    };

    /// <summary>The account for this spec, or null. Used by the module to check presence before importing.</summary>
    public UserAccount FindAccount(AvatarSpec spec)
        => string.IsNullOrWhiteSpace(spec?.FirstName) || string.IsNullOrWhiteSpace(spec.LastName)
            ? null
            : m_services.Accounts.GetUserAccount(m_services.ScopeID, spec.FirstName, spec.LastName);

    public AvatarImportResult Import(AvatarPlan plan, ImportDocument document, ImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(plan);
        options ??= new ImportOptions();
        var spec = plan.Spec;
        var result = new AvatarImportResult { Name = spec?.DisplayName ?? "(unnamed)" };
        foreach (var w in plan.Warnings) result.Messages.Add("warning: " + w);

        if (!plan.Ok)
        {
            foreach (var e in plan.Errors) result.Messages.Add("error: " + e);
            return result;
        }

        try
        {
            ImportChecked(plan, document, options, result);
        }
        catch (Exception e)
        {
            result.Success = false;
            result.Messages.Add($"error: {e.Message}");
            m_log.LogError(e, "[APPEARANCE IMPORT]: importing {Name} failed", result.Name);
        }
        return result;
    }

    private void ImportChecked(AvatarPlan plan, ImportDocument document, ImportOptions options, AvatarImportResult result)
    {
        var spec = plan.Spec;
        var dry = options.DryRun;

        // ---------------------------------------------------------------- 1. checks (nothing written)

        var account = FindAccount(spec);
        var willCreate = false;
        string password = null;
        if (account is null)
        {
            if (!(spec.Account?.Create ?? false) && !options.CreateAccounts)
            {
                Fail(result, "account does not exist; set \"account\": { \"create\": true } or run with --create-accounts");
                return;
            }
            password = FirstNonEmpty(spec.Account?.Password, document?.DefaultPassword);
            if (password is null)
            {
                Fail(result, "account must be created but no password is given (account.password or defaultPassword)");
                return;
            }
            if (UUID.TryParse(spec.Uuid ?? "", out var wanted) && !wanted.IsZero()
                && m_services.Accounts.GetUserAccount(m_services.ScopeID, wanted) is { } clash)
            {
                Fail(result, $"uuid {wanted} already belongs to {clash.FirstName} {clash.LastName}");
                return;
            }
            willCreate = true;
        }

        // Existing wearable assets must exist: they are worn, and their parameters feed the VisualParams.
        var existingParams = new Dictionary<UUID, IReadOnlyDictionary<int, float>>();
        foreach (var w in plan.Wearables.Where(w => w.UsesExistingAsset))
        {
            var parsed = FetchWearable(w.ExistingAssetId, out var why);
            if (parsed is null) { Fail(result, $"{WearableKinds.TypeName(w.Kind)} asset {w.ExistingAssetId}: {why}"); return; }
            if (parsed.Kind != w.Kind)
                result.Messages.Add($"warning: asset {w.ExistingAssetId} is a {WearableKinds.TypeName(parsed.Kind)}, listed as {WearableKinds.TypeName(w.Kind)}");
            existingParams[w.ExistingAssetId] = parsed.Params;
        }

        foreach (var kind in plan.MissingBodyParts)
        {
            if (!AssetExists(LibraryDefaults[kind]))
                result.Messages.Add($"warning: library default {WearableKinds.TypeName(kind)} asset {LibraryDefaults[kind]} is not in the asset service; the viewer will not finish loading this avatar until a {WearableKinds.TypeName(kind)} is worn");
        }

        // Texture files must exist; texture UUIDs should (a missing one only leaves that layer unpainted).
        var baseDir = string.IsNullOrEmpty(options.BaseDirectory) ? "." : options.BaseDirectory;
        foreach (var w in plan.Wearables)
            foreach (var (slot, tex) in w.Textures)
            {
                if (tex.IsFile)
                {
                    var full = Path.GetFullPath(Path.Combine(baseDir, tex.FilePath));
                    if (!File.Exists(full)) { Fail(result, $"{WearableKinds.TypeName(w.Kind)} {slot}: texture file {full} not found"); return; }
                }
                else if (!AssetExists(tex.AssetId))
                    result.Messages.Add($"warning: {WearableKinds.TypeName(w.Kind)} {slot}: texture {tex.AssetId} is not in the asset service");
            }

        var attachments = new List<ComposedAttachment>();
        foreach (var a in plan.Attachments)
        {
            if (AssetExists(a.AssetId)) attachments.Add(a);
            else result.Messages.Add($"warning: attachment '{a.Name}' asset {a.AssetId} is not in the asset service; skipped");
        }

        if (dry)
        {
            result.Success = true;
            result.Messages.Add(willCreate ? "would create the account" : $"account exists ({account.PrincipalID})");
            result.Messages.Add($"would store {plan.Wearables.Count(w => !w.UsesExistingAsset)} wearable assets, "
                + $"{plan.Wearables.SelectMany(w => w.Textures.Values).Count(t => t.IsFile)} textures, "
                + $"wear {plan.Wearables.Count + plan.MissingBodyParts.Count} wearables and {attachments.Count} attachments");
            return;
        }

        // ---------------------------------------------------------------- 2. account and inventory

        if (willCreate)
        {
            account = CreateAccount(spec, password, result);
            if (account is null) return;
        }
        var pid = account.PrincipalID;
        result.PrincipalID = pid;

        var inv = m_services.Inventory;
        var root = inv.GetRootFolder(pid);
        if (root is null)
        {
            if (!inv.CreateUserInventory(pid) || (root = inv.GetRootFolder(pid)) is null)
            {
                Fail(result, "the user has no inventory and it could not be created");
                return;
            }
        }
        var clothing = inv.GetFolderForType(pid, FolderType.Clothing) ?? root;
        var cof = inv.GetFolderForType(pid, FolderType.CurrentOutfit);
        if (cof is null)
        {
            cof = new InventoryFolderBase(UUID.Random(), "Current Outfit", pid, (short)FolderType.CurrentOutfit, root.ID, 1);
            if (!inv.AddFolder(cof)) { Fail(result, "the user has no Current Outfit folder and it could not be created"); return; }
        }

        // With replaceOutfit false the COF's attachment links stay, so the items they point at must not go to the
        // Trash with the previous outfit folder.
        var replaceAll = spec.ReplaceOutfit ?? true;
        var oldLinks = inv.GetFolderItems(pid, cof.ID) ?? new List<InventoryItemBase>();
        var keptTargets = replaceAll
            ? new HashSet<UUID>()
            : oldLinks.Where(i => i.InvType != (int)InventoryType.Wearable).Select(i => i.AssetID).ToHashSet();

        var outfitName = AppearancePlanner.CleanLine(spec.OutfitName, AppearancePlanner.DefaultOutfitName);
        var outfit = ReplaceOutfitFolder(pid, clothing, outfitName, keptTargets, result);
        if (outfit is null) return;

        // ---------------------------------------------------------------- 3. textures

        InventoryFolderBase texturesFolder = null;
        var textureIds = new Dictionary<ComposedWearable, Dictionary<TextureSlot, UUID>>();
        foreach (var w in plan.Wearables.Where(w => !w.UsesExistingAsset))
        {
            var ids = new Dictionary<TextureSlot, UUID>();
            foreach (var (slot, tex) in w.Textures)
            {
                var id = tex.AssetId;
                if (tex.IsFile)
                {
                    id = UploadTexture(Path.GetFullPath(Path.Combine(baseDir, tex.FilePath)), pid, options, result);
                    if (id.IsZero()) return;
                }
                ids[slot] = id;

                texturesFolder ??= AddFolder(pid, TexturesFolderName, outfit.ID, result);
                if (texturesFolder is null) return;
                var name = tex.IsFile ? Path.GetFileNameWithoutExtension(tex.FilePath) : $"{w.Name} {slot}";
                if (AddItem(NewItem(pid, texturesFolder.ID, name, id, AssetType.Texture, InventoryType.Texture, 0), result) is null) return;
            }
            textureIds[w] = ids;
        }

        // ---------------------------------------------------------------- 4. wearables

        var worn = new List<(WearableKind Kind, UUID ItemID, UUID AssetID, string Name)>();
        var wornParams = new List<(WearableKind Kind, IReadOnlyDictionary<int, float> Params)>();
        foreach (var w in plan.Wearables)
        {
            UUID assetId;
            if (w.UsesExistingAsset)
            {
                assetId = w.ExistingAssetId;
                wornParams.Add((w.Kind, existingParams[assetId]));
            }
            else
            {
                var text = AppearancePlanner.ToLLWearable(w, textureIds[w], pid, pid);
                var asset = new AssetBase(UUID.Random(), w.Name, (sbyte)(w.IsBodyPart ? AssetType.Bodypart : AssetType.Clothing), pid.ToString())
                {
                    Data = Encoding.UTF8.GetBytes(text),
                    Description = w.Description ?? string.Empty,
                };
                assetId = StoreAsset(asset, result);
                if (assetId.IsZero()) return;
                wornParams.Add((w.Kind, w.Params));
            }

            var item = AddItem(NewItem(pid, outfit.ID, w.Name, assetId,
                w.IsBodyPart ? AssetType.Bodypart : AssetType.Clothing, InventoryType.Wearable, (uint)w.Kind), result);
            if (item is null) return;
            worn.Add((w.Kind, item.ID, assetId, w.Name));
        }

        foreach (var kind in plan.MissingBodyParts)
        {
            var assetId = LibraryDefaults[kind];
            var name = "Default " + char.ToUpperInvariant(WearableKinds.TypeName(kind)[0]) + WearableKinds.TypeName(kind)[1..];
            var item = AddItem(NewItem(pid, outfit.ID, name, assetId, AssetType.Bodypart, InventoryType.Wearable, (uint)kind), result);
            if (item is null) return;
            worn.Add((kind, item.ID, assetId, name));
            var parsed = FetchWearable(assetId, out _);
            if (parsed is not null) wornParams.Add((kind, parsed.Params));
        }

        var attached = new List<(ComposedAttachment Attachment, UUID ItemID)>();
        foreach (var a in attachments)
        {
            var item = AddItem(NewItem(pid, outfit.ID, a.Name, a.AssetId, AssetType.Object, InventoryType.Object, 0), result);
            if (item is null) return;
            attached.Add((a, item.ID));
        }

        // ---------------------------------------------------------------- 5. Current Outfit Folder

        var remove = oldLinks
            .Where(i => i.AssetType is (int)AssetType.Link or (int)AssetType.LinkFolder)
            .Where(i => replaceAll || i.InvType == (int)InventoryType.Wearable)
            .Select(i => i.ID)
            .ToList();
        if (remove.Count > 0 && !inv.DeleteItems(pid, remove))
            result.Messages.Add($"warning: the inventory service refused to remove {remove.Count} old Current Outfit links");

        var index = new Dictionary<WearableKind, int>();
        foreach (var (kind, itemId, _, name) in worn)
        {
            index.TryGetValue(kind, out var i);
            index[kind] = i + 1;
            // The viewer's ordering string (llappearancemgr.cpp build_order_string), clothing only: see CofWearables.OrderKey.
            var description = WearableKinds.IsBodyPart(kind) ? string.Empty : "@" + ((int)kind * 100 + i);
            if (AddItem(NewLink(pid, cof.ID, name, itemId, InventoryType.Wearable, (uint)kind, description), result) is null) return;
        }
        foreach (var (a, itemId) in attached)
            if (AddItem(NewLink(pid, cof.ID, a.Name, itemId, InventoryType.Object, 0, string.Empty), result) is null) return;

        // ---------------------------------------------------------------- 6. the avatar service row

        AvatarAppearance previous = null;
        try { previous = m_services.Avatars.GetAppearance(pid); }
        catch (Exception e) { m_log.LogDebug("[APPEARANCE IMPORT]: no previous appearance for {Agent}: {Message}", pid, e.Message); }

        var appearance = new AvatarAppearance();
        appearance.ClearWearables();
        foreach (var group in worn.GroupBy(w => w.Kind))
        {
            var aw = new AvatarWearable();
            foreach (var (_, itemId, assetId, _) in group) aw.Add(itemId, assetId);
            appearance.SetWearable((int)group.Key, aw);
        }
        appearance.VisualParams = AppearancePlanner.EncodeVisualParams(m_catalog.Lad, wornParams);
        appearance.Serial = (previous?.Serial ?? 0) + 1;

        if (!replaceAll && previous is not null)
            foreach (var a in previous.GetAttachments())
                appearance.SetAttachment(a.AttachPoint | 0x80, a.ItemID, a.AssetID);
        foreach (var (a, itemId) in attached)
            appearance.SetAttachment(a.Point | 0x80, itemId, a.AssetId);

        if (!m_services.Avatars.SetAppearance(pid, appearance))
        {
            Fail(result, "the avatar service refused the appearance");
            return;
        }

        result.Success = true;
        result.Messages.Add($"wearing {worn.Count} wearables and {attached.Count} attachments from folder Clothing/{outfitName}; the avatar bakes at its next login");
        m_log.LogInformation("[APPEARANCE IMPORT]: {Name} ({Agent}): {Wearables} wearables, {Attachments} attachments, {Assets} assets stored",
            result.Name, pid, worn.Count, attached.Count, result.AssetsStored);
    }

    // ---------------------------------------------------------------- helpers

    private UserAccount CreateAccount(AvatarSpec spec, string password, AvatarImportResult result)
    {
        // On a grid the scene's account service is the Robust connector, and Robust's "setaccount" only updates
        // accounts that already exist (UserAccountServerPostHandler.StoreAccount). Accounts are created there with
        // "createuser", which runs UserAccountService.CreateUser: account, password, inventory and a default
        // appearance in one call. It is gated by [UserAccountService] AllowCreateUser on Robust.
        if (m_services.Accounts is UserAccountServicesConnector robust)
        {
            var created = robust.CreateUser(spec.FirstName, spec.LastName, password, spec.Account?.Email ?? string.Empty, m_services.ScopeID);
            if (created is null)
            {
                Fail(result, "Robust refused to create the account; set AllowCreateUser = true in Robust's [UserAccountService] section, or create it there with 'create user'");
                return null;
            }
            if (!string.IsNullOrWhiteSpace(spec.Uuid))
                result.Messages.Add($"warning: Robust chooses the id of accounts it creates; the uuid in the document was not used");
            result.AccountCreated = true;
            result.Messages.Add($"created account {created.PrincipalID} on Robust");
            return created;
        }

        var principal = UUID.TryParse(spec.Uuid ?? "", out var wanted) && !wanted.IsZero() ? wanted : UUID.Random();
        var account = new UserAccount(m_services.ScopeID, principal, spec.FirstName, spec.LastName, spec.Account?.Email ?? string.Empty)
        {
            ServiceURLs = new Dictionary<string, object>
            {
                ["HomeURI"] = string.Empty,
                ["InventoryServerURI"] = string.Empty,
                ["AssetServerURI"] = string.Empty,
            },
        };

        // The same sequence RemoteAdmin's admin_create_user runs from a region.
        if (!m_services.Accounts.StoreUserAccount(account))
        {
            Fail(result, "the user account service refused to create the account");
            return null;
        }
        result.AccountCreated = true;

        if (m_services.Authentication is null || !m_services.Authentication.SetPassword(principal, password))
            result.Messages.Add("warning: the password could not be set; set it with 'reset user password'");

        if (m_services.Grid is not null && m_services.GridUsers is not null)
        {
            List<GridRegion> defaults = null;
            try { defaults = m_services.Grid.GetDefaultRegions(m_services.ScopeID); } catch (Exception e) { m_log.LogDebug("[APPEARANCE IMPORT]: no default regions: {Message}", e.Message); }
            if (defaults is { Count: > 0 })
                m_services.GridUsers.SetHome(principal.ToString(), defaults[0].RegionID, new Vector3(128, 128, 0), new Vector3(0, 1, 0));
        }

        if (!m_services.Inventory.CreateUserInventory(principal))
            result.Messages.Add("warning: the inventory service did not create an inventory skeleton");

        result.Messages.Add($"created account {principal}");
        return account;
    }

    /// <summary>
    /// A fresh outfit folder under Clothing. A folder of the same name left by an earlier import is moved to the
    /// Trash (never deleted), so re-running an import does not pile up copies and nothing is lost. Items in it that
    /// the avatar keeps wearing (<paramref name="keep"/>) are moved into the new folder first.
    /// </summary>
    private InventoryFolderBase ReplaceOutfitFolder(UUID pid, InventoryFolderBase clothing, string name, HashSet<UUID> keep, AvatarImportResult result)
    {
        var inv = m_services.Inventory;
        var content = inv.GetFolderContent(pid, clothing.ID);
        var old = content?.Folders?.Where(f => string.Equals(f.Name, name, StringComparison.Ordinal)).ToList() ?? new List<InventoryFolderBase>();

        var folder = AddFolder(pid, name, clothing.ID, result);
        if (folder is null || old.Count == 0) return folder;

        var trash = inv.GetFolderForType(pid, FolderType.Trash);
        foreach (var f in old)
        {
            var stillWorn = (inv.GetFolderItems(pid, f.ID) ?? new List<InventoryItemBase>()).Where(i => keep.Contains(i.ID)).ToList();
            if (stillWorn.Count > 0)
            {
                foreach (var i in stillWorn) i.Folder = folder.ID;
                if (!inv.MoveItems(pid, stillWorn))
                {
                    result.Messages.Add($"warning: could not move {stillWorn.Count} still-worn items out of the previous Clothing/{name} folder; it is kept");
                    continue;
                }
            }
            if (trash is not null)
            {
                f.ParentID = trash.ID;
                if (inv.MoveFolder(f)) { result.Messages.Add($"moved the previous Clothing/{name} folder to the Trash"); continue; }
            }
            result.Messages.Add($"warning: could not move the previous Clothing/{name} folder to the Trash; both are kept");
        }
        return folder;
    }

    private InventoryFolderBase AddFolder(UUID pid, string name, UUID parent, AvatarImportResult result)
    {
        var folder = new InventoryFolderBase(UUID.Random(), name, pid, (short)FolderType.None, parent, 1);
        if (m_services.Inventory.AddFolder(folder)) return folder;
        Fail(result, $"the inventory service refused to create folder '{name}'");
        return null;
    }

    private InventoryItemBase AddItem(InventoryItemBase item, AvatarImportResult result)
    {
        if (m_services.Inventory.AddItem(item))
        {
            result.ItemsCreated++;
            return item;
        }
        Fail(result, $"the inventory service refused to create item '{item.Name}'");
        return null;
    }

    private static InventoryItemBase NewItem(UUID owner, UUID folder, string name, UUID assetId, AssetType assetType, InventoryType invType, uint flags)
    {
        const uint all = (uint)PermissionMask.All;
        return new InventoryItemBase(UUID.Random(), owner)
        {
            Name = name,
            Description = string.Empty,
            AssetID = assetId,
            AssetType = (int)assetType,
            InvType = (int)invType,
            Folder = folder,
            CreatorId = owner.ToString(),
            Flags = flags,
            BasePermissions = all,
            CurrentPermissions = all,
            EveryOnePermissions = 0,
            GroupPermissions = 0,
            NextPermissions = all,
            CreationDate = Util.UnixTimeSinceEpoch(),
        };
    }

    /// <summary>A COF link, as <c>UserAccountService.CreateCurrentOutfitLink</c> writes one, plus the viewer's ordering description.</summary>
    private static InventoryItemBase NewLink(UUID owner, UUID cof, string name, UUID targetItem, InventoryType invType, uint flags, string description)
    {
        const uint copy = (uint)PermissionMask.Copy;
        return new InventoryItemBase(UUID.Random(), owner)
        {
            Name = name,
            Description = description,
            AssetID = targetItem,
            AssetType = (int)AssetType.Link,
            InvType = (int)invType,
            Folder = cof,
            CreatorId = owner.ToString(),
            Flags = flags,
            BasePermissions = copy,
            CurrentPermissions = copy,
            EveryOnePermissions = copy,
            GroupPermissions = copy,
            NextPermissions = copy,
            CreationDate = Util.UnixTimeSinceEpoch(),
        };
    }

    private UUID StoreAsset(AssetBase asset, AvatarImportResult result)
    {
        var stored = m_services.Assets.Store(asset);
        if (UUID.TryParse(stored ?? string.Empty, out var id) && !id.IsZero())
        {
            result.AssetsStored++;
            return id;
        }
        Fail(result, $"the asset service refused asset '{asset.Name}'");
        return UUID.Zero;
    }

    private UUID UploadTexture(string fullPath, UUID creator, ImportOptions options, AvatarImportResult result)
    {
        if (m_uploaded.TryGetValue(fullPath, out var known)) return known;

        byte[] j2c;
        try { j2c = TextureFileEncoder.Encode(fullPath, options.MaxTextureSize, options.TextureQuality); }
        catch (Exception e) when (e is InvalidDataException or IOException or FormatException or ArgumentException)
        {
            Fail(result, $"texture {fullPath}: {e.Message}");
            return UUID.Zero;
        }

        var asset = new AssetBase(UUID.Random(), Path.GetFileNameWithoutExtension(fullPath), (sbyte)AssetType.Texture, creator.ToString())
        {
            Data = j2c,
            Description = "appearance import: " + Path.GetFileName(fullPath),
        };
        var id = StoreAsset(asset, result);
        if (!id.IsZero())
        {
            m_uploaded[fullPath] = id;
            result.Messages.Add($"uploaded {Path.GetFileName(fullPath)} as texture {id}");
        }
        return id;
    }

    private ParsedWearable FetchWearable(UUID assetId, out string why)
    {
        why = null;
        var asset = m_services.Assets.Get(assetId.ToString());
        if (asset?.Data is not { Length: > 0 }) { why = "not in the asset service"; return null; }
        if (asset.Type is not ((sbyte)AssetType.Bodypart or (sbyte)AssetType.Clothing)) { why = $"is asset type {asset.Type}, not a body part or clothing"; return null; }
        try { return WearableParser.Parse(Encoding.UTF8.GetString(asset.Data).TrimEnd('\0')); }
        catch (FormatException e) { why = e.Message; return null; }
    }

    private bool AssetExists(UUID id)
    {
        if (id.IsZero()) return false;
        try
        {
            var r = m_services.Assets.AssetsExist(new[] { id.ToString() });
            if (r is { Length: 1 }) return r[0];
        }
        catch (Exception e) when (e is NotImplementedException or NotSupportedException)
        {
            // fall through to a metadata read
        }
        return m_services.Assets.GetMetadata(id.ToString()) is not null;
    }

    private static void Fail(AvatarImportResult result, string message)
    {
        result.Success = false;
        result.Messages.Add("error: " + message);
    }

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrEmpty(v));
}

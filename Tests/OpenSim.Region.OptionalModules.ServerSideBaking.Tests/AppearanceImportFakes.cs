using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;
using PermissionMask = OpenSim.Framework.PermissionMask;

namespace OpenSim.Region.OptionalModules.ServerSideBaking.Tests;

/// <summary>An in-memory inventory: the folder skeleton <c>CreateUserInventory</c> makes, and plain item/folder storage.</summary>
public sealed class FakeInventoryService : IInventoryService
{
    public readonly Dictionary<UUID, InventoryFolderBase> Folders = new();
    public readonly Dictionary<UUID, InventoryItemBase> Items = new();
    public bool RefuseAddItem;

    public bool CreateUserInventory(UUID user)
    {
        if (GetRootFolder(user) is not null) return true;
        var root = Folder(user, "My Inventory", FolderType.Root, UUID.Zero);
        Folder(user, "Clothing", FolderType.Clothing, root.ID);
        Folder(user, "Body Parts", FolderType.BodyPart, root.ID);
        Folder(user, "Textures", FolderType.Texture, root.ID);
        Folder(user, "Current Outfit", FolderType.CurrentOutfit, root.ID);
        Folder(user, "Trash", FolderType.Trash, root.ID);
        return true;
    }

    private InventoryFolderBase Folder(UUID owner, string name, FolderType type, UUID parent)
    {
        var f = new InventoryFolderBase(UUID.Random(), name, owner, (short)type, parent, 1);
        Folders[f.ID] = f;
        return f;
    }

    public InventoryFolderBase GetRootFolder(UUID userID) => GetFolderForType(userID, FolderType.Root);
    public InventoryFolderBase GetFolderForType(UUID userID, FolderType type)
        => Folders.Values.FirstOrDefault(f => f.Owner == userID && f.Type == (short)type);

    public InventoryCollection GetFolderContent(UUID userID, UUID folderID) => new()
    {
        OwnerID = userID,
        FolderID = folderID,
        Folders = Folders.Values.Where(f => f.Owner == userID && f.ParentID == folderID).ToList(),
        Items = GetFolderItems(userID, folderID),
    };

    public List<InventoryItemBase> GetFolderItems(UUID userID, UUID folderID)
        => Items.Values.Where(i => i.Owner == userID && i.Folder == folderID).ToList();

    public bool AddFolder(InventoryFolderBase folder) { Folders[folder.ID] = folder; return true; }
    public bool MoveFolder(InventoryFolderBase folder)
    {
        if (!Folders.TryGetValue(folder.ID, out var f)) return false;
        f.ParentID = folder.ParentID;
        return true;
    }
    public bool AddItem(InventoryItemBase item) { if (RefuseAddItem) return false; Items[item.ID] = item; return true; }
    public bool DeleteItems(UUID userID, List<UUID> itemIDs) { foreach (var id in itemIDs) Items.Remove(id); return true; }
    public InventoryItemBase GetItem(UUID userID, UUID itemID) => Items.TryGetValue(itemID, out var i) ? i : null;
    public InventoryFolderBase GetFolder(UUID userID, UUID folderID) => Folders.TryGetValue(folderID, out var f) ? f : null;
    public bool HasInventoryForUser(UUID userID) => GetRootFolder(userID) is not null;

    public List<InventoryFolderBase> GetInventorySkeleton(UUID userId) => Folders.Values.Where(f => f.Owner == userId).ToList();
    public InventoryCollection[] GetMultipleFoldersContent(UUID userID, UUID[] folderIDs) => folderIDs.Select(id => GetFolderContent(userID, id)).ToArray();
    public bool UpdateFolder(InventoryFolderBase folder) => AddFolder(folder);
    public bool DeleteFolders(UUID userID, List<UUID> folderIDs) => DeleteFolders(userID, folderIDs, true);
    public bool DeleteFolders(UUID userID, List<UUID> folderIDs, bool onlyIfTrash) => throw new NotSupportedException("the importer must not delete folders");
    public bool PurgeFolder(InventoryFolderBase folder) => throw new NotSupportedException("the importer must not purge folders");
    public bool UpdateItem(InventoryItemBase item) => AddItem(item);
    public bool MoveItems(UUID ownerID, List<InventoryItemBase> items) { foreach (var i in items) if (Items.TryGetValue(i.ID, out var x)) x.Folder = i.Folder; return true; }
    public InventoryItemBase[] GetMultipleItems(UUID userID, UUID[] ids) => ids.Select(id => GetItem(userID, id)).ToArray();
    public List<InventoryItemBase> GetActiveGestures(UUID userId) => new();
    public int GetAssetPermissions(UUID userID, UUID assetID) => (int)PermissionMask.All;
}

public sealed class FakeUserAccountService : IUserAccountService
{
    public readonly Dictionary<UUID, UserAccount> Accounts = new();
    public bool RefuseStore;

    public UserAccount GetUserAccount(UUID scopeID, UUID userID) => Accounts.TryGetValue(userID, out var a) ? a : null;
    public UserAccount GetUserAccount(UUID scopeID, string FirstName, string LastName)
        => Accounts.Values.FirstOrDefault(a => string.Equals(a.FirstName, FirstName, StringComparison.OrdinalIgnoreCase)
                                             && string.Equals(a.LastName, LastName, StringComparison.OrdinalIgnoreCase));
    public UserAccount GetUserAccount(UUID scopeID, string Email) => null;
    public bool SetDisplayName(UUID agentID, string displayName) => false;
    public List<UserAccount> GetUserAccounts(UUID scopeID, string query) => new();
    public List<UserAccount> GetUserAccountsWhere(UUID scopeID, string where) => new();
    public List<UserAccount> GetUserAccounts(UUID scopeID, List<string> IDs) => new();
    public bool StoreUserAccount(UserAccount data) { if (RefuseStore) return false; Accounts[data.PrincipalID] = data; return true; }
    public void InvalidateCache(UUID userID) { }
}

public sealed class FakeAuthenticationService : IAuthenticationService
{
    public readonly Dictionary<UUID, string> Passwords = new();

    public bool SetPassword(UUID principalID, string passwd) { Passwords[principalID] = passwd; return true; }
    public string Authenticate(UUID principalID, string password, int lifetime) => null;
    public string Authenticate(UUID principalID, string password, int lifetime, out UUID realID) { realID = UUID.Zero; return null; }
    public bool Verify(UUID principalID, string token, int lifetime) => false;
    public bool Release(UUID principalID, string token) => false;
    public AuthInfo GetAuthInfo(UUID principalID) => null;
    public bool SetAuthInfo(AuthInfo info) => false;
}

/// <summary>
/// The region's view of Robust's account service: <c>setaccount</c> only updates existing accounts, and new ones
/// are made with <c>createuser</c>, which (UserAccountService.CreateUser) also sets the password and makes the inventory.
/// </summary>
public sealed class FakeRobustAccounts : OpenSim.Services.Connectors.UserAccountServicesConnector
{
    private readonly FakeInventoryService m_inventory;
    public readonly Dictionary<UUID, UserAccount> Accounts = new();
    public readonly Dictionary<UUID, string> Passwords = new();

    public FakeRobustAccounts(FakeInventoryService inventory) { m_inventory = inventory; }

    public override UserAccount GetUserAccount(UUID scopeID, UUID userID) => Accounts.TryGetValue(userID, out var a) ? a : null;
    public override UserAccount GetUserAccount(UUID scopeID, string firstName, string lastName)
        => Accounts.Values.FirstOrDefault(a => a.FirstName == firstName && a.LastName == lastName);
    public override bool StoreUserAccount(UserAccount data) => Accounts.ContainsKey(data.PrincipalID);
    public override UserAccount CreateUser(string first, string last, string password, string email, UUID scopeID)
        => CreateUser(first, last, password, email, scopeID, UUID.Zero);

    /// <summary>Robust's createuser uses PrincipalID when the request carries one.</summary>
    public override UserAccount CreateUser(string first, string last, string password, string email, UUID scopeID, UUID principalID)
    {
        var a = new UserAccount(scopeID, principalID.IsZero() ? UUID.Random() : principalID, first, last, email);
        Accounts[a.PrincipalID] = a;
        Passwords[a.PrincipalID] = password;
        m_inventory.CreateUserInventory(a.PrincipalID);
        return a;
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSimNGC.Appearance.Baking;

namespace OpenSim.Region.OptionalModules.Avatar.AppearanceImport;

/// <summary>
/// Console commands that give accounts a complete, viewer-compatible appearance from a JSON document:
/// <code>
/// appearance import &lt;file-or-directory&gt; [--dry-run] [--create-accounts] [--force]
/// appearance export &lt;first&gt; &lt;last&gt; &lt;file&gt;
/// appearance params [&lt;wearable-type&gt;]
/// </code>
/// The import writes wearable assets, inventory items, Current Outfit Folder links and the avatar service row
/// through the scene's services (<see cref="AppearanceImporter"/>), so it works the same on a standalone and on a
/// grid. It writes no bakes: the avatar is baked at its next login, by <c>ServerSideBakingModule</c> where
/// <c>[Appearance] ServerSideBaking</c> is on and by the viewer elsewhere. See Docs/feature/appearance-import.
///
/// <para>Config (optional):</para>
/// <code>
/// [AppearanceImport]
///     Enabled = true          ; the commands exist on every region console unless this is false
///     MaxTextureSize = 1024   ; image files are scaled to power-of-two sides no larger than this
///     TextureQuality = 0.9
/// </code>
/// </summary>
public class AppearanceImportModule : ISharedRegionModule
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(typeof(AppearanceImportModule));

    public const string ConfigSection = "AppearanceImport";

    private readonly List<Scene> m_scenes = new();
    private bool m_enabled = true;
    private int m_maxTextureSize = TextureFileEncoder.DefaultMaxSize;
    private double m_textureQuality = 0.9;

    public string Name => "AppearanceImportModule";
    public Type ReplaceableInterface => null;

    public void Initialise(IConfigSource source)
    {
        var config = source.Configs[ConfigSection];
        if (config is null) return;
        m_enabled = config.GetBoolean("Enabled", true);
        m_maxTextureSize = config.GetInt("MaxTextureSize", m_maxTextureSize);
        if (m_maxTextureSize is not (256 or 512 or 1024 or 2048))
        {
            m_log.LogWarning("[APPEARANCE IMPORT]: MaxTextureSize {Size} is not 256, 512, 1024 or 2048; using 1024", m_maxTextureSize);
            m_maxTextureSize = 1024;
        }
        m_textureQuality = Math.Clamp(config.GetDouble("TextureQuality", m_textureQuality), 0.1, 1.0);
    }

    public void PostInitialise() { }
    public void Close() { }
    public void AddRegion(Scene scene) { }

    public void RemoveRegion(Scene scene)
    {
        lock (m_scenes) m_scenes.Remove(scene);
    }

    public void RegionLoaded(Scene scene)
    {
        if (!m_enabled) return;
        lock (m_scenes) m_scenes.Add(scene);

        scene.AddCommand(
            "Users", this, "appearance import",
            "appearance import <file-or-directory> [--dry-run] [--create-accounts] [--force]",
            "Give accounts the appearance described in a JSON document (or every .json file in a directory).",
            "Stores the wearable assets and uploads image files as textures, creates the inventory items in "
            + "Clothing/<outfitName>, replaces the Current Outfit Folder links and writes the avatar's appearance. "
            + "The avatar bakes at its next login.\n"
            + "--dry-run          validate and report, write nothing\n"
            + "--create-accounts  create every missing account (otherwise only those with \"account\": {\"create\": true})\n"
            + "--force            import even when the avatar is logged in to this simulator (it must relog to see it)",
            HandleImport);

        scene.AddCommand(
            "Users", this, "appearance export",
            "appearance export <first-name> <last-name> <file>",
            "Write an avatar's stored appearance as an 'appearance import' document.",
            "Wearables are written as their parameters and texture ids (paramScale \"value\"), attachments as their object "
            + "asset ids. Use it as a template, or to copy a look you built in the viewer onto many test accounts.",
            HandleExport);

        scene.AddCommand(
            "Users", this, "appearance params",
            "appearance params [<wearable-type>]",
            "List the visual parameters an 'appearance import' document can set: id, name, editor label, range and default.",
            HandleParams);
    }

    // ------------------------------------------------------------------ services

    private Scene FirstScene()
    {
        lock (m_scenes) return m_scenes.Count > 0 ? m_scenes[0] : null;
    }

    private static ImportServices ServicesOf(Scene scene) => new()
    {
        Accounts = scene.UserAccountService,
        Authentication = scene.AuthenticationService,
        Grid = scene.GridService,
        GridUsers = scene.GridUserService,
        Inventory = scene.InventoryService,
        Assets = scene.AssetService,
        Avatars = scene.AvatarService,
        ScopeID = scene.RegionInfo.ScopeID,
    };

    /// <summary>The region an avatar is a root agent in on this simulator, or null.</summary>
    private Scene RootSceneOf(UUID agentId)
    {
        List<Scene> scenes;
        lock (m_scenes) scenes = new List<Scene>(m_scenes);
        foreach (var s in scenes)
            if (s.GetScenePresence(agentId) is { IsChildAgent: false }) return s;
        return null;
    }

    // ------------------------------------------------------------------ appearance import

    private void HandleImport(string module, string[] cmd)
    {
        var args = cmd.Skip(2).ToList();
        var dryRun = args.Remove("--dry-run");
        var createAccounts = args.Remove("--create-accounts");
        var force = args.Remove("--force");
        if (args.Count != 1 || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            MainConsole.Instance.Output("Usage: appearance import <file-or-directory> [--dry-run] [--create-accounts] [--force]");
            return;
        }

        var scene = FirstScene();
        if (scene is null) { MainConsole.Instance.Output("No region is loaded."); return; }

        var path = Path.GetFullPath(args[0]);
        List<string> files;
        if (Directory.Exists(path)) files = Directory.GetFiles(path, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToList();
        else if (File.Exists(path)) files = new List<string> { path };
        else { MainConsole.Instance.Output("{0} is neither a file nor a directory.", path); return; }
        if (files.Count == 0) { MainConsole.Instance.Output("No .json files in {0}.", path); return; }

        AppearanceImporter importer;
        try { importer = new AppearanceImporter(ServicesOf(scene), ParamCatalog.Embedded); }
        catch (ArgumentException e) { MainConsole.Instance.Output("Cannot import: {0}", e.Message); return; }

        int ok = 0, failed = 0, skipped = 0;
        foreach (var file in files)
        {
            ImportDocument doc;
            try { doc = ImportDocumentReader.Load(file); }
            catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException)
            {
                MainConsole.Instance.Output("{0}: {1}", Path.GetFileName(file), e.Message);
                failed++;
                continue;
            }

            var options = new ImportOptions
            {
                DryRun = dryRun,
                CreateAccounts = createAccounts,
                BaseDirectory = Path.GetDirectoryName(file),
                MaxTextureSize = m_maxTextureSize,
                TextureQuality = m_textureQuality,
            };

            MainConsole.Instance.Output("{0}{1}: {2} avatar(s)", dryRun ? "[dry run] " : "", Path.GetFileName(file), doc.Avatars.Count);
            foreach (var spec in doc.Avatars)
            {
                if (spec is null) continue;
                var plan = AppearancePlanner.Plan(spec, doc, ParamCatalog.Embedded);

                // A logged-in viewer holds its own copy of the outfit and would write it back over the import.
                var existing = plan.Ok ? importer.FindAccount(spec) : null;
                if (existing is not null && !force && !dryRun && RootSceneOf(existing.PrincipalID) is { } where)
                {
                    MainConsole.Instance.Output("  SKIP {0}: logged in to {1}; log them out first, or use --force and relog", spec.DisplayName, where.RegionInfo.RegionName);
                    skipped++;
                    continue;
                }

                var result = importer.Import(plan, doc, options);
                MainConsole.Instance.Output("  {0} {1}{2}", result.Success ? "OK  " : "FAIL", result.Name,
                    result.PrincipalID.IsZero() ? "" : $" ({result.PrincipalID})");
                foreach (var m in result.Messages) MainConsole.Instance.Output("       {0}", m);
                if (result.Success) ok++; else failed++;
            }
        }

        MainConsole.Instance.Output("{0}{1} imported, {2} failed, {3} skipped.", dryRun ? "[dry run] " : "", ok, failed, skipped);
    }

    // ------------------------------------------------------------------ appearance export

    private void HandleExport(string module, string[] cmd)
    {
        if (cmd.Length != 5)
        {
            MainConsole.Instance.Output("Usage: appearance export <first-name> <last-name> <file>");
            return;
        }
        var scene = FirstScene();
        if (scene is null) { MainConsole.Instance.Output("No region is loaded."); return; }

        var services = ServicesOf(scene);
        var account = services.Accounts.GetUserAccount(services.ScopeID, cmd[2], cmd[3]);
        if (account is null) { MainConsole.Instance.Output("No account {0} {1}.", cmd[2], cmd[3]); return; }

        var appearance = services.Avatars.GetAppearance(account.PrincipalID);
        if (appearance is null) { MainConsole.Instance.Output("{0} {1} has no stored appearance.", cmd[2], cmd[3]); return; }

        var spec = Export(services, account, appearance, out var notes);
        var doc = new ImportDocument { Version = 1, ParamScale = "value", Avatars = new List<AvatarSpec> { spec } };
        var path = Path.GetFullPath(cmd[4]);
        try { File.WriteAllText(path, ImportDocumentReader.Serialize(doc)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MainConsole.Instance.Output("Could not write {0}: {1}", path, e.Message);
            return;
        }
        foreach (var n in notes) MainConsole.Instance.Output("  {0}", n);
        MainConsole.Instance.Output("Wrote {0} wearables and {1} attachments of {2} to {3}.", spec.Wearables.Count, spec.Attachments.Count, account.Name, path);
    }

    private static AvatarSpec Export(ImportServices services, UserAccount account, AvatarAppearance appearance, out List<string> notes)
    {
        notes = new List<string>();
        var catalog = ParamCatalog.Embedded;
        var pid = account.PrincipalID;
        var spec = new AvatarSpec
        {
            FirstName = account.FirstName,
            LastName = account.LastName,
            OutfitName = AppearancePlanner.DefaultOutfitName,
        };

        var wearables = appearance.Wearables ?? Array.Empty<AvatarWearable>();
        for (var type = 0; type < wearables.Length; type++)
        {
            var slot = wearables[type];
            if (slot is null) continue;
            var kind = (WearableKind)type;
            for (var j = 0; j < slot.Count; j++)
            {
                var itemId = slot[j].ItemID;
                var assetId = slot[j].AssetID;
                var item = itemId.IsZero() ? null : services.Inventory.GetItem(pid, itemId);
                if (assetId.IsZero()) assetId = item?.AssetID ?? UUID.Zero;
                if (assetId.IsZero()) { notes.Add($"{WearableKinds.TypeName(kind)} {j}: no asset; skipped"); continue; }

                var asset = services.Assets.Get(assetId.ToString());
                if (asset?.Data is not { Length: > 0 }) { notes.Add($"{WearableKinds.TypeName(kind)} {j}: asset {assetId} not found; skipped"); continue; }

                ParsedWearable parsed;
                try { parsed = WearableParser.Parse(Encoding.UTF8.GetString(asset.Data).TrimEnd('\0')); }
                catch (FormatException e) { notes.Add($"{WearableKinds.TypeName(kind)} {j}: asset {assetId} unreadable ({e.Message}); written as assetId"); parsed = null; }

                var ws = new WearableSpec { Type = WearableKinds.TypeName(kind), Name = item?.Name ?? parsed?.Name };
                if (parsed is null)
                {
                    ws.AssetId = assetId.ToString();
                }
                else
                {
                    foreach (var (id, value) in parsed.Params.OrderBy(p => p.Key))
                    {
                        // The name when it resolves back to this id unambiguously; the id otherwise.
                        var key = id.ToString(CultureInfo.InvariantCulture);
                        if (catalog.Lad.Params.TryGetValue(id, out var def)
                            && catalog.Resolve(def.Name, kind, out var ambiguous) is { } back && back.Id == id && !ambiguous)
                            key = def.Name;
                        ws.Params[key] = Math.Round(value, 4);
                    }
                    foreach (var (texSlot, texId) in parsed.Textures.OrderBy(t => (int)t.Key))
                        ws.Textures[texSlot.ToString()] = texId.ToString();
                }
                spec.Wearables.Add(ws);
            }
        }

        foreach (var a in appearance.GetAttachments())
        {
            var item = services.Inventory.GetItem(pid, a.ItemID);
            var assetId = a.AssetID.IsZero() ? item?.AssetID ?? UUID.Zero : a.AssetID;
            if (assetId.IsZero()) { notes.Add($"attachment {a.ItemID} at {a.AttachPoint}: no asset; skipped"); continue; }
            spec.Attachments.Add(new AttachmentSpec
            {
                Point = Enum.IsDefined(typeof(AttachmentPoint), (byte)a.AttachPoint) ? ((AttachmentPoint)a.AttachPoint).ToString() : a.AttachPoint.ToString(CultureInfo.InvariantCulture),
                AssetId = assetId.ToString(),
                Name = item?.Name,
            });
        }
        return spec;
    }

    // ------------------------------------------------------------------ appearance params

    private void HandleParams(string module, string[] cmd)
    {
        var catalog = ParamCatalog.Embedded;
        IEnumerable<WearableKind> kinds;
        if (cmd.Length >= 3)
        {
            var k = AppearancePlanner.ParseKind(cmd[2]);
            if (k is null) { MainConsole.Instance.Output("Unknown wearable type {0}.", cmd[2]); return; }
            kinds = new[] { k.Value };
        }
        else
        {
            kinds = Enum.GetValues<WearableKind>().Where(k => k != WearableKind.Invalid);
        }

        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        foreach (var kind in kinds)
        {
            var defs = catalog.TweakablesOf(kind).ToList();
            var slots = AppearancePlanner.SlotsFor(kind);
            sb.AppendLine($"{WearableKinds.TypeName(kind)}: {defs.Count} parameters; textures: {(slots.Count == 0 ? "none" : string.Join(", ", slots))}");
            foreach (var d in defs)
                sb.AppendLine($"  {d.Id,6}  {d.Name,-28} {catalog.LabelOf(d.Id) ?? "",-24} {d.Min.ToString(inv),6} .. {d.Max.ToString(inv),-6} default {d.Default.ToString(inv)}");
        }
        MainConsole.Instance.Output(sb.ToString());
    }
}

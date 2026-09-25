# Appearance import

Region console commands that give test accounts a complete appearance (shape, skin, hair, eyes, clothing, attachments)
from a JSON document, so they look right when they log in. Code: `Source/OpenSim.Region.OptionalModules/Avatar/AppearanceImport/`.

```
appearance import <file-or-directory> [--dry-run] [--create-accounts] [--force]
appearance export <first> <last> <file>
appearance params [<wearable-type>]
```

## What an import writes

For each avatar in the document, through the scene's own services (so standalone and grid behave the same):

| Step | What | Where |
|---|---|---|
| 1 | Account (only when `account.create` is true or `--create-accounts` is given). Standalone: the sequence RemoteAdmin's `admin_create_user` runs (store account, set password, home = first default region, inventory skeleton). Grid: Robust's `createuser` (see Configuration) | user account, authentication, grid user, inventory services |
| 2 | One **LLWearable version 22 asset** per generated wearable — type 13 (body part) or 5 (clothing) — with every tweakable parameter of that type (avatar_lad.xml defaults, overridden by the document) and the texture slots the document gives | asset service |
| 3 | Image files named in `textures` are converted to JPEG 2000 (power-of-two, ≤ `MaxTextureSize`) and stored as texture assets; one inventory item each in `Clothing/<outfit>/Textures`. A file used by many avatars in one run is stored once | asset + inventory services |
| 4 | An inventory item per wearable and attachment in `Clothing/<outfitName>`; a same-named folder from an earlier import goes to the **Trash** (never deleted) | inventory service |
| 5 | The **Current Outfit Folder** is emptied (links only) and refilled with one link per worn item. Clothing links carry the viewer's ordering string `@<type*100+index>`, which `CofWearables.Derive` and the viewer both sort by, so the order in the document is the layering order (later = on top) | inventory service |
| 6 | The **avatar service row**: wearables (item + asset per type, in order), attachments (`_ap_<point>`), and `VisualParams` encoded exactly as a viewer sends them (`VisualParamEncoder`). No baked textures: `Serial` is bumped and the texture entry is left at its defaults | avatar service |

The next login bakes the avatar: on the server where `[Appearance] ServerSideBaking = true` (the login bake in
`ServerSideBakingModule`), in the viewer elsewhere. On a server-baking region you can also force it with
`appearance serverbake <first> <last>` once the avatar is in the region.

Checks run **before** anything is written: invalid documents, a missing account that may not be created, a missing
`assetId` wearable, a missing texture file. Any of these fails that avatar and leaves it untouched. The appearance row
is written last, so an avatar that fails part-way keeps its previous look.

A logged-in avatar is skipped (its viewer holds its own outfit and would write it back); log it out first, or use
`--force` and make it relog.

## Document

See `sample.json`. Top level: `{ "paramScale", "defaultPassword", "avatars": [...] }`, a bare array of avatars, or a
single avatar object. Comments and trailing commas are allowed.

```jsonc
{
  "firstName": "Load", "lastName": "Tester01",
  "uuid": "…",                           // optional, used only when the account is created
  "account": { "create": true, "password": "…", "email": "" },
  "outfitName": "Load Test Look",        // default "Imported Outfit"
  "paramScale": "slider",                // value (default) | slider (0–100) | byte (0–255)
  "replaceOutfit": true,                 // false keeps existing attachments
  "wearables": [
    { "type": "shape", "name": "…", "params": { "Height": 62, "33": 62, "Body Thickness": 40 } },
    { "type": "skin", "textures": { "head_bodypaint": "<texture uuid>", "upper_bodypaint": "img/upper.png" } },
    { "type": "hair", "assetId": "<existing wearable asset>" },
    { "type": "shirt", "params": { "shirt_red": 10 } },
    { "type": "shirt", "name": "worn over the first shirt" }
  ],
  "attachments": [ { "point": "Skull", "assetId": "<existing object asset>", "name": "Mesh hair" } ]
}
```

* **params** — key by avatar_lad.xml id, name or viewer editor label (`PARAMS.md`, or `appearance params <type>`).
  Values outside the range are clamped (with a warning); a parameter that belongs to another wearable type is ignored
  (with a warning). Parameters you leave out take the avatar_lad.xml default.
* **textures** — slot name (`HeadBodypaint` or `head_bodypaint`), slot number, or `texture` for a one-slot type.
  A UUID wears an existing texture; anything else is a file path relative to the document (png, jpg, webp, bmp, gif,
  tga; j2c/j2k/jp2 are uploaded unchanged). Leave a slot out to paint it by hand later.
* **Body parts** — one each of shape, skin, hair, eyes. A missing one is filled with the library default asset
  (`AvatarWearable.DEFAULT_*_ASSET`), as `create user` does.
* **Clothing** — up to 5 per type (the `AvatarWearable` limit).
* **Attachments** — the object asset must already exist on the grid (mesh bodies, heads, hair, HUDs…). A missing one is
  skipped with a warning.

`appearance export` writes an avatar's stored appearance in this format (raw `value` scale, texture UUIDs,
attachment asset ids). The easiest way to get a good document is to dress one account in the viewer, export it, and
use the result as a template for the rest.

## Configuration

Optional, in the region's ini:

```ini
[AppearanceImport]
    Enabled = true
    MaxTextureSize = 1024   ; 256, 512, 1024 or 2048
    TextureQuality = 0.9
```

Grid (Robust) deployments: Robust's `setaccount` only updates accounts that already exist, so on a grid the importer
creates accounts through Robust's `createuser` call (`UserAccountService.CreateUser`: account, password, inventory and
a default outfit in one step, which the import then replaces). Robust must allow it:

```ini
; Robust.ini
[UserAccountService]
    AllowCreateUser = true
```

Robust chooses the id of accounts created this way, so a `uuid` in the document is ignored there. If you would rather
not open that up, create the accounts on the Robust console (`create user`) and import without `account.create`.
On a standalone the importer creates the account locally, the way RemoteAdmin's `admin_create_user` does, and `uuid` is honoured.

## Limits

* No bakes are written; the avatar shows as the default/cloud look until its first login bakes it.
* Attachments are worn by asset id; the tool does not create objects.
* Physics and universal wearables are supported as types; alpha and tattoo layers need their textures to exist
  (UUID) or be given as files.

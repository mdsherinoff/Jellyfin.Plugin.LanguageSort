# Jellyfin Language Sort Plugin

Automatically organizes your movies and TV shows into **collections grouped by language** — no file or folder reorganisation required.

## How it works

The plugin detects each item's language using a fallback chain:

1. **Audio stream tags** in the media files (for TV shows, the first few episodes are sampled)
2. **Original-title script detection** — a title in Malayalam, Tamil, Korean, etc. script identifies the language
3. **TMDb lookup** (optional) — with a TMDb API key configured, the authoritative `original_language` is fetched using the item's TMDb id

A scheduled task, **Update Language Collections** (Dashboard → Scheduled Tasks → Library), then creates one Jellyfin collection per language ("Malayalam", "English", "French", …) and keeps them in sync as your library changes. It runs daily at 3:00 AM by default, and you can trigger it manually at any time.

The collections appear in every Jellyfin client under **Collections**. Collections created by the plugin are tagged internally, so your own hand-made collections are never touched; if a language disappears from your library, its collection is removed automatically.

## Configuration

Dashboard → Plugins → Language Sort:

| Setting | Description |
|---|---|
| Include Movies / TV Shows | Which item types to group |
| Show "Unknown Language" group | Collect items whose audio streams have no usable language tag |
| Language Display Format | English name ("French"), native name ("Français"), or ISO code ("FR") |
| Pinned Languages | Comma-separated ISO codes (e.g. `en,hi,fr`) listed first in API results |
| Guess language from original title script | Fallback when audio streams are untagged (on by default) |
| TMDb API Key | Optional; enables TMDb `original_language` lookups for items that are still unknown. Free key: themoviedb.org → Settings → API |

After changing settings, re-run the **Update Language Collections** task to apply them.

## REST API

The plugin also exposes endpoints (require an authenticated Jellyfin session or API key):

- `GET /LanguageSort/Groups` — all language groups with item counts
- `GET /LanguageSort/Items?language=French` — items in one group

## Installation

**Step 1 — Add repository**

1. Open Jellyfin → **Dashboard** → **Plugins** → **Repositories**
2. Click **+** (Add Repository)
3. Enter:
   - **Repository Name:** `Language Sort`
   - **Repository URL:**
     ```
     https://raw.githubusercontent.com/mdsherinoff/Jellyfin.Plugin.LanguageSort/main/manifest.json
     ```
4. Click **Save**

**Step 2 — Install plugin**

1. Go to **Dashboard** → **Plugins** → **Catalog**
2. Find **Language Sort** under the Library category
3. Click **Install**
4. **Restart Jellyfin**

**Step 3 — Run it**

1. Go to **Dashboard** → **Plugins** → **Language Sort** and adjust settings if needed
2. Go to **Dashboard** → **Scheduled Tasks** and run **Update Language Collections**
3. Browse your library's **Collections** view

> **Note:** language detection relies on the audio streams of your media files being tagged. If many items land in "Unknown Language", your files are missing audio language tags — a library scan with "Replace all metadata" can help Jellyfin (re-)probe them.

## Building from source

Requires the .NET 9 SDK (or newer):

```sh
dotnet build --configuration Release
```

Copy `bin/Release/net9.0/Jellyfin.Plugin.LanguageSort.dll` into a `LanguageSort` folder inside your Jellyfin `plugins` directory and restart Jellyfin.

Releases are automated: pushing a tag like `v1.0.0` builds the plugin, publishes a GitHub release, and updates `manifest.json`.

## Compatibility

- Jellyfin **10.11.x** (target ABI `10.11.0.0`)

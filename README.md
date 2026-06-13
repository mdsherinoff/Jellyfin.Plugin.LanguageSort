# Jellyfin Language Sort Plugin

Organize your movies and TV shows into **virtual language groups** without touching your files or folders.

---

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

**Step 3 — Configure**

1. Go to **Dashboard** → **Plugins** → **Language Sort**
2. Set pinned languages, display format, etc.
3. Run a **metadata refresh** on your libraries so TMDb/TheTVDB fills in `OriginalLanguage`

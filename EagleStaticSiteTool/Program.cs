using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

if (args.Length < 2)
{
    Console.WriteLine("Usage: EagleStaticSiteTool <LibraryPathOrRootPath> <OutputDirectory> [PublishTag]");
    Console.WriteLine(@"Example: EagleStaticSiteTool ""E:\Develop\EagleShareSystem"" ""E:\Develop\EagleShareSystem\site-output"" ""发布""");
    return;
}

string inputPath = Path.GetFullPath(args[0]);
string outputPath = Path.GetFullPath(args[1]);
string publishTag = args.Length >= 3 ? args[2] : "发布";

var jsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true
};

List<string> libraries = ResolveLibraryPaths(inputPath);
if (libraries.Count == 0)
{
    Console.WriteLine($"No valid Eagle libraries found from: {inputPath}");
    return;
}

Directory.CreateDirectory(outputPath);
string assetsOutput = Path.Combine(outputPath, "assets");
Directory.CreateDirectory(assetsOutput);

var librariesData = new List<LibraryDto>();
var flatFolders = new List<FolderDto>();
var imageItems = new List<ImageItemDto>();

for (int i = 0; i < libraries.Count; i++)
{
    string libraryPath = libraries[i];
    string libraryName = Path.GetFileNameWithoutExtension(libraryPath);
    string libraryId = $"lib-{i + 1}";

    string libraryMetadataPath = Path.Combine(libraryPath, "metadata.json");
    if (!File.Exists(libraryMetadataPath))
    {
        continue;
    }

    LibraryMetadata libraryMetadata = JsonSerializer.Deserialize<LibraryMetadata>(
        File.ReadAllText(libraryMetadataPath), jsonOptions) ?? new LibraryMetadata();

    var folderIdMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    FlattenFolders(libraryMetadata.Folders, 0, null, libraryId, libraryName, flatFolders, folderIdMap);

    string imagesRoot = Path.Combine(libraryPath, "images");
    if (!Directory.Exists(imagesRoot))
    {
        continue;
    }

    int publishedCountForLibrary = 0;
    var infoDirectories = Directory.GetDirectories(imagesRoot, "*.info", SearchOption.TopDirectoryOnly);
    foreach (string infoDir in infoDirectories)
    {
        string metadataPath = Path.Combine(infoDir, "metadata.json");
        if (!File.Exists(metadataPath))
        {
            continue;
        }

        ImageMetadata? imageMeta;
        try
        {
            imageMeta = JsonSerializer.Deserialize<ImageMetadata>(File.ReadAllText(metadataPath), jsonOptions);
        }
        catch
        {
            continue;
        }

        if (imageMeta is null || string.IsNullOrWhiteSpace(imageMeta.Id))
        {
            continue;
        }

        bool isPublished = (imageMeta.Tags ?? []).Any(t => string.Equals(t, publishTag, StringComparison.OrdinalIgnoreCase));
        if (!isPublished)
        {
            continue;
        }

        string? sourceImagePath = FindSourceImage(infoDir, imageMeta);
        string? relativeAssetPath = null;

        if (sourceImagePath is not null)
        {
            string ext = Path.GetExtension(sourceImagePath);
            string targetName = $"{libraryId}_{imageMeta.Id}{ext}";
            string targetPath = Path.Combine(assetsOutput, targetName);
            File.Copy(sourceImagePath, targetPath, overwrite: true);
            relativeAssetPath = $"assets/{targetName}";
        }

        imageItems.Add(new ImageItemDto
        {
            Id = $"{libraryId}:{imageMeta.Id}",
            LibraryId = libraryId,
            LibraryName = libraryName,
            Name = imageMeta.Name ?? imageMeta.Id,
            Ext = imageMeta.Ext ?? "",
            Size = imageMeta.Size,
            Width = imageMeta.Width,
            Height = imageMeta.Height,
            Url = imageMeta.Url ?? "",
            Annotation = imageMeta.Annotation ?? "",
            Tags = imageMeta.Tags ?? [],
            FolderIds = (imageMeta.Folders ?? []).Select(fid =>
                folderIdMap.TryGetValue(fid, out string? mapped) ? mapped : $"{libraryId}:{fid}").ToList(),
            ModificationTime = imageMeta.ModificationTime,
            Btime = imageMeta.Btime,
            Mtime = imageMeta.Mtime,
            LastModified = imageMeta.LastModified,
            ImagePath = relativeAssetPath
        });
        publishedCountForLibrary++;
    }

    librariesData.Add(new LibraryDto
    {
        Id = libraryId,
        Name = libraryName,
        Path = libraryPath,
        ImageCount = publishedCountForLibrary
    });
}

imageItems = imageItems
    .OrderByDescending(i => i.ModificationTime)
    .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
    .ToList();

var siteData = new SiteDataDto
{
    SiteName = "Eagle Multi Library",
    GeneratedAt = DateTimeOffset.Now,
    PublishTag = publishTag,
    Libraries = librariesData,
    Folders = flatFolders,
    Images = imageItems
};

var siteJsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
};

string siteDataJson = JsonSerializer.Serialize(siteData, siteJsonOptions);

static List<string> ResolveLibraryPaths(string pathArg)
{
    var inputs = pathArg.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var result = new List<string>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (string raw in inputs)
    {
        string candidate = Path.GetFullPath(raw);
        if (!Directory.Exists(candidate))
        {
            continue;
        }

        bool isLibrary = candidate.EndsWith(".library", StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(candidate, "metadata.json"));
        if (isLibrary)
        {
            if (seen.Add(candidate))
            {
                result.Add(candidate);
            }
            continue;
        }

        foreach (string dir in Directory.GetDirectories(candidate, "*.library", SearchOption.TopDirectoryOnly))
        {
            if (File.Exists(Path.Combine(dir, "metadata.json")) && seen.Add(dir))
            {
                result.Add(dir);
            }
        }
    }

    return result.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
}

static void FlattenFolders(
    IEnumerable<FolderNode>? folders,
    int level,
    string? parentId,
    string libraryId,
    string libraryName,
    List<FolderDto> output,
    Dictionary<string, string> folderIdMap)
{
    if (folders is null)
    {
        return;
    }

    foreach (FolderNode folder in folders)
    {
        string rawId = folder.Id ?? Guid.NewGuid().ToString("N");
        string compositeId = $"{libraryId}:{rawId}";
        folderIdMap[rawId] = compositeId;
        output.Add(new FolderDto
        {
            Id = compositeId,
            RawId = rawId,
            Name = folder.Name ?? "",
            Level = level,
            ParentId = parentId,
            LibraryId = libraryId,
            LibraryName = libraryName
        });
        FlattenFolders(folder.Children, level + 1, compositeId, libraryId, libraryName, output, folderIdMap);
    }
}

static string? FindSourceImage(string infoDir, ImageMetadata imageMeta)
{
    var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "metadata.json" };
    var files = Directory.GetFiles(infoDir);
    string expectedBase = imageMeta.Id ?? "";
    string expectedExt = imageMeta.Ext ?? "";
    string expectedFile = string.IsNullOrWhiteSpace(expectedExt) ? expectedBase : $"{expectedBase}.{expectedExt}";

    string? preferred = files.FirstOrDefault(f =>
        string.Equals(Path.GetFileName(f), expectedFile, StringComparison.OrdinalIgnoreCase));
    if (preferred is not null)
    {
        return preferred;
    }

    return files.FirstOrDefault(f => !ignored.Contains(Path.GetFileName(f)));
}

const string HtmlTemplate = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
  <meta charset="UTF-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <title>Eagle 静态图库</title>
  <link rel="stylesheet" href="./styles.css" />
</head>
<body>
  <div class="app-shell">
    <aside class="left-panel" id="leftPanel">
      <div class="brand" id="libraryName">Eagle Multi Library</div>
      <button id="closeFolderPanel" class="mobile-close" type="button">收起分类</button>
      <div class="tree-title">分类导航</div>
      <nav id="folderTree" class="folder-tree"></nav>
    </aside>

    <main class="center-panel">
      <header class="toolbar">
        <button id="openFolderPanel" class="mobile-action" type="button">分类</button>
        <div class="toolbar-title" id="toolbarTitle">全部图片</div>
        <input id="searchInput" class="search" type="text" placeholder="搜索文件名..." />
      </header>
      <section id="galleryGrid" class="gallery-grid"></section>
    </main>

    <aside class="right-panel">
      <h2>图片详情</h2>
      <div id="preview" class="detail-image placeholder">预览</div>
      <div class="field"><label>文件名</label><input id="detailName" readonly /></div>
      <div class="field"><label>链接</label><input id="detailUrl" readonly /></div>
      <div class="field"><label>标签</label><input id="detailTags" readonly /></div>
      <div class="field"><label>备注</label><textarea id="detailAnnotation" readonly></textarea></div>
      <div class="meta-block">
        <div><span>尺寸</span><strong id="detailSize">-</strong></div>
        <div><span>格式</span><strong id="detailExt">-</strong></div>
        <div><span>大小</span><strong id="detailBytes">-</strong></div>
        <div><span>添加日期</span><strong id="detailAddTime">-</strong></div>
        <div><span>创建日期</span><strong id="detailCreateTime">-</strong></div>
        <div><span>修改日期</span><strong id="detailModifyTime">-</strong></div>
      </div>
    </aside>
  </div>
  <div id="lightbox" class="lightbox" aria-hidden="true">
    <button id="lightboxClose" class="lightbox-close" type="button">关闭</button>
    <img id="lightboxImage" class="lightbox-image" alt="全屏预览" />
  </div>
  <script src="./data.js"></script>
  <script src="./app.js"></script>
</body>
</html>
""";

const string CssTemplate = """
:root {
  --bg: #1f2227;
  --panel: #252a31;
  --panel-soft: #2b3038;
  --text: #d6d9df;
  --muted: #9aa0aa;
  --accent: #4e8ef7;
  --border: #3a404a;
}
* { box-sizing: border-box; }
body {
  margin: 0;
  font-family: "Segoe UI", "PingFang SC", sans-serif;
  background: var(--bg);
  color: var(--text);
}
.app-shell {
  height: 100vh;
  display: grid;
  grid-template-columns: 280px 1fr 320px;
}
.mobile-action,.mobile-close {
  display: none;
  border: 1px solid var(--border);
  background: var(--panel-soft);
  color: var(--text);
  border-radius: 8px;
  padding: 7px 10px;
}
.left-panel,.right-panel { background: var(--panel); }
.left-panel { border-right: 1px solid var(--border); }
.right-panel { border-left: 1px solid var(--border); padding: 16px; }
.brand { padding: 16px; font-weight: 600; border-bottom: 1px solid var(--border); }
.tree-title { padding: 12px 12px 6px; color: var(--muted); font-size: 12px; }
.folder-tree { padding: 0 12px 12px; overflow: auto; max-height: calc(100vh - 80px); }
.tree-item,.tree-library-header,.tree-subitem {
  width: 100%;
  text-align: left;
  border: 0;
  background: transparent;
  color: var(--muted);
  padding: 8px 10px;
  border-radius: 8px;
  cursor: pointer;
}
.tree-item:hover,.tree-item.is-active,.tree-subitem:hover,.tree-subitem.is-active,.tree-library-header:hover {
  background: var(--panel-soft);
  color: var(--text);
}
.tree-library-group { margin-bottom: 6px; border: 1px solid #303641; border-radius: 8px; overflow: hidden; }
.tree-library-header { display: flex; justify-content: space-between; align-items: center; font-weight: 600; border-radius: 0; }
.tree-chevron { color: var(--muted); font-size: 12px; }
.tree-library-content { padding: 6px; }
.tree-subitem { padding: 7px 10px; border-radius: 6px; color: var(--muted); }
.tree-subitem.all { font-weight: 600; color: var(--text); }
.center-panel { display: flex; flex-direction: column; min-width: 0; }
.toolbar {
  height: 56px;
  border-bottom: 1px solid var(--border);
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0 16px;
  background: var(--panel);
}
.search {
  width: 260px;
  background: var(--panel-soft);
  border: 1px solid var(--border);
  border-radius: 8px;
  color: var(--text);
  padding: 8px 10px;
}
.gallery-grid {
  padding: 16px;
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(200px, 1fr));
  gap: 14px;
  overflow: auto;
}
.asset-card {
  background: var(--panel);
  border: 1px solid var(--border);
  border-radius: 10px;
  padding: 10px;
  cursor: pointer;
}
.asset-card.is-selected { border-color: var(--accent); }
.thumb {
  width: 100%;
  height: 140px;
  object-fit: cover;
  border-radius: 8px;
  background: #353b45;
}
.asset-name {
  margin-top: 8px;
  font-size: 13px;
  color: var(--muted);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
h2 { margin: 0 0 12px; font-size: 16px; }
.detail-image {
  height: 170px;
  border-radius: 8px;
  margin-bottom: 12px;
  background: #353b45;
  display: flex;
  align-items: center;
  justify-content: center;
  color: var(--muted);
  overflow: hidden;
}
.detail-image img { width: 100%; height: 100%; object-fit: contain; background: #1b1e22; }
.field { margin-bottom: 10px; }
.field label { display: block; margin-bottom: 6px; color: var(--muted); font-size: 12px; }
.field input,.field textarea {
  width: 100%;
  padding: 8px 9px;
  border-radius: 8px;
  border: 1px solid var(--border);
  background: var(--panel-soft);
  color: var(--text);
}
.field textarea { min-height: 70px; resize: vertical; }
.meta-block { margin-top: 14px; border-top: 1px solid var(--border); padding-top: 12px; display: grid; gap: 8px; }
.meta-block div { display: flex; justify-content: space-between; }
.meta-block span { color: var(--muted); }
.lightbox {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.92);
  display: none;
  align-items: center;
  justify-content: center;
  z-index: 50;
  padding: 20px;
}
.lightbox.is-open { display: flex; }
.lightbox-image { max-width: 100%; max-height: 100%; object-fit: contain; }
.lightbox-close {
  position: absolute;
  top: 12px;
  right: 12px;
  border: 1px solid #5b6270;
  background: #2a303a;
  color: #fff;
  border-radius: 8px;
  padding: 8px 12px;
  cursor: pointer;
}

@media (max-width: 900px) {
  .app-shell {
    height: auto;
    min-height: 100vh;
    grid-template-columns: 1fr;
    grid-template-rows: auto auto;
  }
  .left-panel {
    position: fixed;
    top: 0;
    left: 0;
    width: 80%;
    max-width: 340px;
    height: 100vh;
    z-index: 20;
    transform: translateX(-100%);
    transition: transform 0.2s ease;
    box-shadow: 8px 0 30px rgba(0,0,0,0.35);
  }
  .left-panel.is-open { transform: translateX(0); }
  .right-panel { border-left: 0; border-top: 1px solid var(--border); }
  .mobile-action,.mobile-close { display: inline-block; }
  .mobile-close { margin: 0 12px 8px; width: calc(100% - 24px); }
  .toolbar { gap: 10px; height: auto; padding: 10px 12px; flex-wrap: wrap; }
  .search { width: 100%; }
  .gallery-grid { grid-template-columns: repeat(auto-fill, minmax(150px, 1fr)); padding: 12px; gap: 10px; }
}
""";

const string JsTemplate = """
let siteData = null;
let selectedLibraryId = null;
let selectedFolderId = null;
let selectedImageId = null;
const collapsedLibraryIds = new Set();

const folderTree = document.getElementById("folderTree");
const galleryGrid = document.getElementById("galleryGrid");
const searchInput = document.getElementById("searchInput");
const toolbarTitle = document.getElementById("toolbarTitle");
const libraryName = document.getElementById("libraryName");
const leftPanel = document.getElementById("leftPanel");
const openFolderPanel = document.getElementById("openFolderPanel");
const closeFolderPanel = document.getElementById("closeFolderPanel");

const preview = document.getElementById("preview");
const detailName = document.getElementById("detailName");
const detailUrl = document.getElementById("detailUrl");
const detailTags = document.getElementById("detailTags");
const detailAnnotation = document.getElementById("detailAnnotation");
const detailSize = document.getElementById("detailSize");
const detailExt = document.getElementById("detailExt");
const detailBytes = document.getElementById("detailBytes");
const detailAddTime = document.getElementById("detailAddTime");
const detailCreateTime = document.getElementById("detailCreateTime");
const detailModifyTime = document.getElementById("detailModifyTime");
const lightbox = document.getElementById("lightbox");
const lightboxImage = document.getElementById("lightboxImage");
const lightboxClose = document.getElementById("lightboxClose");

main();

function main() {
  siteData = window.__EAGLE_SITE_DATA__;
  if (!siteData) {
    galleryGrid.innerHTML = `<div style="color:#ff8a8a;">未找到站点数据，请重新生成。</div>`;
    return;
  }
  siteData.folders = siteData.folders || siteData.Folders || [];
  siteData.images = siteData.images || siteData.Images || [];
  siteData.libraries = siteData.libraries || siteData.Libraries || [];
  libraryName.textContent = siteData.siteName || "Eagle Multi Library";
  renderFolders();
  renderGallery();
}

function renderFolders() {
  folderTree.innerHTML = "";

  const allBtn = createFolderButton({ id: "", name: "全部图片" }, true);
  folderTree.appendChild(allBtn);

  for (const lib of siteData.libraries) {
    const group = document.createElement("section");
    group.className = "tree-library-group";

    const header = document.createElement("button");
    header.className = "tree-library-header";
    const collapsed = collapsedLibraryIds.has(lib.id);
    header.innerHTML = `<span>${escapeHtml(lib.name)} (${lib.imageCount || 0})</span><span class="tree-chevron">${collapsed ? "▶" : "▼"}</span>`;
    header.onclick = () => {
      if (collapsed) collapsedLibraryIds.delete(lib.id);
      else collapsedLibraryIds.add(lib.id);
      renderFolders();
    };
    group.appendChild(header);

    if (!collapsed) {
      const content = document.createElement("div");
      content.className = "tree-library-content";

      const allInLib = document.createElement("button");
      allInLib.className = "tree-subitem all";
      allInLib.textContent = "该库全部图片";
      if (selectedLibraryId === lib.id && !selectedFolderId) {
        allInLib.classList.add("is-active");
      }
      allInLib.onclick = () => {
        selectedLibraryId = lib.id;
        selectedFolderId = null;
        selectedImageId = null;
        renderFolders();
        renderGallery();
      };
      content.appendChild(allInLib);

      const folders = siteData.folders.filter(f => f.libraryId === lib.id);
      for (const folder of folders) {
        const btn = createFolderButton(folder, false);
        btn.classList.add("tree-subitem");
        btn.style.paddingLeft = `${10 + (folder.level || 0) * 14}px`;
        content.appendChild(btn);
      }
      group.appendChild(content);
    }

    folderTree.appendChild(group);
  }
}

function createFolderButton(folder, resetLibrary) {
  const btn = document.createElement("button");
  btn.className = "tree-item";
  btn.textContent = folder.name;
  if (resetLibrary && !selectedLibraryId && !selectedFolderId) {
    btn.classList.add("is-active");
  } else if (!resetLibrary && (selectedFolderId || "") === (folder.id || "")) {
    btn.classList.add("is-active");
  }
  btn.onclick = () => {
    selectedLibraryId = resetLibrary ? null : (folder.libraryId || null);
    selectedFolderId = resetLibrary ? null : (folder.id || null);
    selectedImageId = null;
    renderFolders();
    renderGallery();
    if (window.innerWidth <= 900) {
      leftPanel.classList.remove("is-open");
    }
  };
  return btn;
}

function renderGallery() {
  const search = (searchInput.value || "").trim().toLowerCase();
  const images = siteData.images.filter(img => {
    const inLibrary = !selectedLibraryId || img.libraryId === selectedLibraryId;
    const inFolder = !selectedFolderId || (img.folderIds || []).includes(selectedFolderId);
    const inSearch = !search || (img.name || "").toLowerCase().includes(search);
    return inLibrary && inFolder && inSearch;
  });

  const title = selectedFolderId
    ? (siteData.folders.find(f => f.id === selectedFolderId)?.name || "分类")
    : (selectedLibraryId ? (siteData.libraries.find(l => l.id === selectedLibraryId)?.name || "图库") : "全部图片");
  toolbarTitle.textContent = `${title} (${images.length})`;

  galleryGrid.innerHTML = "";
  if (!images.length) {
    galleryGrid.innerHTML = `<div style="color:#9aa0aa;">当前筛选条件下没有图片。</div>`;
    renderDetail(null);
    return;
  }

  if (!selectedImageId || !images.some(x => x.id === selectedImageId)) {
    selectedImageId = images[0].id;
  }

  for (const image of images) {
    const card = document.createElement("article");
    card.className = "asset-card";
    if (image.id === selectedImageId) {
      card.classList.add("is-selected");
    }

    const thumb = image.imagePath
      ? `<img class="thumb" src="${image.imagePath}" alt="${escapeHtml(image.name)}" />`
      : `<div class="thumb" style="display:flex;align-items:center;justify-content:center;color:#9aa0aa;">无源图</div>`;

    card.innerHTML = `${thumb}<div class="asset-name">${escapeHtml(image.name || image.id)}</div>`;
    const thumbEl = card.querySelector(".thumb");
    thumbEl?.addEventListener("click", (e) => {
      e.stopPropagation();
      if (image.imagePath) {
        openLightbox(image.imagePath, image.name || image.id);
      }
    });
    card.onclick = () => {
      selectedImageId = image.id;
      renderGallery();
    };
    galleryGrid.appendChild(card);
  }

  renderDetail(images.find(x => x.id === selectedImageId) || null);
}

function renderDetail(image) {
  if (!image) {
    preview.innerHTML = "预览";
    detailName.value = "";
    detailUrl.value = "";
    detailTags.value = "";
    detailAnnotation.value = "";
    detailSize.textContent = "-";
    detailExt.textContent = "-";
    detailBytes.textContent = "-";
    detailAddTime.textContent = "-";
    detailCreateTime.textContent = "-";
    detailModifyTime.textContent = "-";
    return;
  }

  preview.innerHTML = image.imagePath
    ? `<img src="${image.imagePath}" alt="${escapeHtml(image.name)}" />`
    : "无源图";
  detailName.value = image.name || "";
  detailUrl.value = image.url || "";
  detailTags.value = [image.libraryName, ...(image.tags || [])].filter(Boolean).join(", ");
  detailAnnotation.value = image.annotation || "";
  detailSize.textContent = `${image.width || 0} x ${image.height || 0}`;
  detailExt.textContent = (image.ext || "").toUpperCase();
  detailBytes.textContent = formatBytes(image.size || 0);
  detailAddTime.textContent = formatMsTime(image.modificationTime);
  detailCreateTime.textContent = formatMsTime(image.btime);
  detailModifyTime.textContent = formatMsTime(image.lastModified || image.mtime);

  preview.onclick = () => {
    if (image.imagePath) {
      openLightbox(image.imagePath, image.name || image.id);
    }
  };
  preview.style.cursor = image.imagePath ? "zoom-in" : "default";
}

function formatBytes(bytes) {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(2)} MB`;
}

function formatMsTime(ms) {
  if (!ms || Number.isNaN(ms)) return "-";
  const d = new Date(ms);
  if (Number.isNaN(d.getTime())) return "-";
  const y = d.getFullYear();
  const m = String(d.getMonth() + 1).padStart(2, "0");
  const day = String(d.getDate()).padStart(2, "0");
  const h = String(d.getHours()).padStart(2, "0");
  const min = String(d.getMinutes()).padStart(2, "0");
  return `${y}/${m}/${day} ${h}:${min}`;
}

function escapeHtml(text) {
  return (text || "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#39;");
}

searchInput.addEventListener("input", () => {
  selectedImageId = null;
  renderGallery();
});

openFolderPanel?.addEventListener("click", () => {
  leftPanel.classList.add("is-open");
});

closeFolderPanel?.addEventListener("click", () => {
  leftPanel.classList.remove("is-open");
});

function openLightbox(src, alt) {
  lightboxImage.src = src;
  lightboxImage.alt = alt || "全屏预览";
  lightbox.classList.add("is-open");
  lightbox.setAttribute("aria-hidden", "false");
}

function closeLightbox() {
  lightbox.classList.remove("is-open");
  lightbox.setAttribute("aria-hidden", "true");
  lightboxImage.src = "";
}

lightboxClose?.addEventListener("click", closeLightbox);
lightbox?.addEventListener("click", (e) => {
  if (e.target === lightbox) {
    closeLightbox();
  }
});

window.addEventListener("keydown", (e) => {
  if (e.key === "Escape" && lightbox?.classList.contains("is-open")) {
    closeLightbox();
  }
});
""";

File.WriteAllText(Path.Combine(outputPath, "data.json"), siteDataJson);
File.WriteAllText(Path.Combine(outputPath, "data.js"), $"window.__EAGLE_SITE_DATA__ = {siteDataJson};");
File.WriteAllText(Path.Combine(outputPath, "index.html"), HtmlTemplate);
File.WriteAllText(Path.Combine(outputPath, "styles.css"), CssTemplate);
File.WriteAllText(Path.Combine(outputPath, "app.js"), JsTemplate);

Console.WriteLine($"Done. Generated static site to: {outputPath}");
Console.WriteLine($"Publish tag: {publishTag}");
Console.WriteLine($"Libraries merged: {librariesData.Count}");
Console.WriteLine($"Published images: {imageItems.Count}");
Console.WriteLine($"Images with source file: {imageItems.Count(i => !string.IsNullOrWhiteSpace(i.ImagePath))}/{imageItems.Count}");

public sealed class LibraryMetadata
{
    [JsonPropertyName("folders")]
    public List<FolderNode> Folders { get; set; } = [];
}

public sealed class FolderNode
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("children")]
    public List<FolderNode>? Children { get; set; } = [];
}

public sealed class ImageMetadata
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("ext")]
    public string? Ext { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("folders")]
    public List<string>? Folders { get; set; } = [];

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; } = [];

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("annotation")]
    public string? Annotation { get; set; }

    [JsonPropertyName("modificationTime")]
    public long ModificationTime { get; set; }

    [JsonPropertyName("btime")]
    public long Btime { get; set; }

    [JsonPropertyName("mtime")]
    public long Mtime { get; set; }

    [JsonPropertyName("lastModified")]
    public long LastModified { get; set; }
}

public sealed class SiteDataDto
{
    public string SiteName { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAt { get; set; }
    public string PublishTag { get; set; } = string.Empty;
    public List<LibraryDto> Libraries { get; set; } = [];
    public List<FolderDto> Folders { get; set; } = [];
    public List<ImageItemDto> Images { get; set; } = [];
}

public sealed class LibraryDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int ImageCount { get; set; }
}

public sealed class FolderDto
{
    public string Id { get; set; } = string.Empty;
    public string RawId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Level { get; set; }
    public string? ParentId { get; set; }
    public string LibraryId { get; set; } = string.Empty;
    public string LibraryName { get; set; } = string.Empty;
}

public sealed class ImageItemDto
{
    public string Id { get; set; } = string.Empty;
    public string LibraryId { get; set; } = string.Empty;
    public string LibraryName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Ext { get; set; } = string.Empty;
    public long Size { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Url { get; set; } = string.Empty;
    public string Annotation { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = [];
    public List<string> FolderIds { get; set; } = [];
    public long ModificationTime { get; set; }
    public long Btime { get; set; }
    public long Mtime { get; set; }
    public long LastModified { get; set; }
    public string? ImagePath { get; set; }
}

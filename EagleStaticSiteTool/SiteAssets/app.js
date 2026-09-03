let siteData = null;
let selectedImageId = null;
const collapsedLibraryIds = new Set();
const filterState = {
  libraryId: null,
  folderId: null,
  keyword: "",
  sortBy: "latest",
  selectedTags: new Set()
};

const folderTree = document.getElementById("folderTree");
const galleryGrid = document.getElementById("galleryGrid");
const searchInput = document.getElementById("searchInput");
const sortSelect = document.getElementById("sortSelect");
const tagFilters = document.getElementById("tagFilters");
const clearFilters = document.getElementById("clearFilters");
const toolbarTitle = document.getElementById("toolbarTitle");
const libraryName = document.getElementById("libraryName");
const leftPanel = document.getElementById("leftPanel");
const openFolderPanel = document.getElementById("openFolderPanel");
const closeFolderPanel = document.getElementById("closeFolderPanel");

const preview = document.getElementById("preview");
const detailName = document.getElementById("detailName");
const detailUrl = document.getElementById("detailUrl");
const detailTags = document.getElementById("detailTags");
const detailLibrary = document.getElementById("detailLibrary");
const detailFolderPath = document.getElementById("detailFolderPath");
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
  siteData.allTags = siteData.allTags || siteData.AllTags || [];
  libraryName.textContent = siteData.siteName || "Eagle Multi Library";
  renderFolders();
  renderTagFilters();
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
      if (filterState.libraryId === lib.id && !filterState.folderId) allInLib.classList.add("is-active");
      allInLib.onclick = () => updateFilter({ libraryId: lib.id, folderId: null, resetSelectedImage: true, closePanel: true });
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
  if (resetLibrary && !filterState.libraryId && !filterState.folderId) btn.classList.add("is-active");
  if (!resetLibrary && (filterState.folderId || "") === (folder.id || "")) btn.classList.add("is-active");
  btn.onclick = () => updateFilter({
    libraryId: resetLibrary ? null : (folder.libraryId || null),
    folderId: resetLibrary ? null : (folder.id || null),
    resetSelectedImage: true,
    closePanel: true
  });
  return btn;
}

function renderTagFilters() {
  tagFilters.innerHTML = "";
  for (const tag of siteData.allTags) {
    const chip = document.createElement("button");
    chip.className = "tag-chip";
    if (filterState.selectedTags.has(tag)) chip.classList.add("is-active");
    chip.textContent = tag;
    chip.onclick = () => {
      if (filterState.selectedTags.has(tag)) filterState.selectedTags.delete(tag);
      else filterState.selectedTags.add(tag);
      updateFilter({ resetSelectedImage: true });
    };
    tagFilters.appendChild(chip);
  }
}

function updateFilter(opts = {}) {
  if ("libraryId" in opts) filterState.libraryId = opts.libraryId;
  if ("folderId" in opts) filterState.folderId = opts.folderId;
  if ("keyword" in opts) filterState.keyword = opts.keyword;
  if ("sortBy" in opts) filterState.sortBy = opts.sortBy;
  if (opts.resetSelectedImage) selectedImageId = null;
  renderFolders();
  renderTagFilters();
  renderGallery();
  if (opts.closePanel && window.innerWidth <= 900) leftPanel.classList.remove("is-open");
}

function getFilteredImages() {
  const keyword = (filterState.keyword || "").trim().toLowerCase();
  const selectedTags = [...filterState.selectedTags];
  const filtered = siteData.images.filter(img => {
    const inLibrary = !filterState.libraryId || img.libraryId === filterState.libraryId;
    const inFolder = !filterState.folderId || (img.folderIds || []).includes(filterState.folderId);
    const inSearch = !keyword || (img.searchTokens || "").includes(keyword);
    const inTags = selectedTags.length === 0 || selectedTags.every(tag => (img.tags || []).includes(tag));
    return inLibrary && inFolder && inSearch && inTags;
  });

  if (filterState.sortBy === "name") {
    filtered.sort((a, b) => (a.name || "").localeCompare(b.name || ""));
  } else if (filterState.sortBy === "modified") {
    filtered.sort((a, b) => (b.lastModified || b.mtime || 0) - (a.lastModified || a.mtime || 0));
  } else {
    filtered.sort((a, b) => (b.modificationTime || 0) - (a.modificationTime || 0));
  }
  return filtered;
}

function renderGallery() {
  const images = getFilteredImages();
  const title = filterState.folderId
    ? (siteData.folders.find(f => f.id === filterState.folderId)?.name || "分类")
    : (filterState.libraryId ? (siteData.libraries.find(l => l.id === filterState.libraryId)?.name || "图库") : "全部图片");
  toolbarTitle.textContent = `${title} (${images.length})`;

  galleryGrid.innerHTML = "";
  if (!images.length) {
    galleryGrid.innerHTML = `<div style="color:#9aa0aa;">当前筛选条件下没有图片。</div>`;
    renderDetail(null);
    return;
  }

  if (!selectedImageId || !images.some(x => x.id === selectedImageId)) selectedImageId = images[0].id;
  for (const image of images) {
    const card = document.createElement("article");
    card.className = "asset-card";
    if (image.id === selectedImageId) card.classList.add("is-selected");
    const thumb = image.imagePath
      ? `<img class="thumb" src="${image.imagePath}" alt="${escapeHtml(image.name)}" />`
      : `<div class="thumb" style="display:flex;align-items:center;justify-content:center;color:#9aa0aa;">无源图</div>`;
    card.innerHTML = `${thumb}<div class="asset-name">${escapeHtml(image.name || image.id)}</div>`;
    const thumbEl = card.querySelector(".thumb");
    thumbEl?.addEventListener("click", (e) => {
      e.stopPropagation();
      if (image.imagePath) openLightbox(image.imagePath, image.name || image.id);
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
    detailLibrary.value = "";
    detailFolderPath.value = "";
    detailAnnotation.value = "";
    detailSize.textContent = "-";
    detailExt.textContent = "-";
    detailBytes.textContent = "-";
    detailAddTime.textContent = "-";
    detailCreateTime.textContent = "-";
    detailModifyTime.textContent = "-";
    return;
  }
  preview.innerHTML = image.imagePath ? `<img src="${image.imagePath}" alt="${escapeHtml(image.name)}" />` : "无源图";
  detailName.value = image.name || "";
  detailUrl.value = image.url || "";
  detailTags.value = (image.tags || []).join(", ");
  detailLibrary.value = image.libraryName || "";
  detailFolderPath.value = image.folderPath || "";
  detailAnnotation.value = image.annotation || "";
  detailSize.textContent = `${image.width || 0} x ${image.height || 0}`;
  detailExt.textContent = (image.ext || "").toUpperCase();
  detailBytes.textContent = formatBytes(image.size || 0);
  detailAddTime.textContent = formatMsTime(image.modificationTime);
  detailCreateTime.textContent = formatMsTime(image.btime);
  detailModifyTime.textContent = formatMsTime(image.lastModified || image.mtime);
  preview.onclick = () => {
    if (image.imagePath) openLightbox(image.imagePath, image.name || image.id);
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

searchInput.addEventListener("input", () => updateFilter({ keyword: searchInput.value, resetSelectedImage: true }));
sortSelect.addEventListener("change", () => updateFilter({ sortBy: sortSelect.value, resetSelectedImage: true }));
clearFilters.addEventListener("click", () => {
  filterState.libraryId = null;
  filterState.folderId = null;
  filterState.keyword = "";
  filterState.sortBy = "latest";
  filterState.selectedTags.clear();
  searchInput.value = "";
  sortSelect.value = "latest";
  selectedImageId = null;
  renderFolders();
  renderTagFilters();
  renderGallery();
});
openFolderPanel?.addEventListener("click", () => leftPanel.classList.add("is-open"));
closeFolderPanel?.addEventListener("click", () => leftPanel.classList.remove("is-open"));

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
  if (e.target === lightbox) closeLightbox();
});
window.addEventListener("keydown", (e) => {
  if (e.key === "Escape" && lightbox?.classList.contains("is-open")) closeLightbox();
});

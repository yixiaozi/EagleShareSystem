const STORAGE_KEY = "eagle-site-prefs-v1";

let siteData = null;
let selectedImageId = null;
let filteredImages = [];
let lightboxIndex = -1;
let folderCollapseInitialized = false;
let galleryRaf = 0;

const collapsedLibraryIds = new Set();
const collapsedFolderIds = new Set();
const filterState = {
  libraryId: null,
  folderId: null,
  keyword: "",
  sortBy: "latest",
  selectedTags: new Set()
};

const folderTree = document.getElementById("folderTree");
const galleryViewport = document.getElementById("galleryViewport");
const gallerySpacer = document.getElementById("gallerySpacer");
const galleryGrid = document.getElementById("galleryGrid");
const searchInput = document.getElementById("searchInput");
const sortSelect = document.getElementById("sortSelect");
const tagFilters = document.getElementById("tagFilters");
const clearFilters = document.getElementById("clearFilters");
const breadcrumb = document.getElementById("breadcrumb");
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
const lightboxPrev = document.getElementById("lightboxPrev");
const lightboxNext = document.getElementById("lightboxNext");
const lightboxCaption = document.getElementById("lightboxCaption");

main();

function main() {
  siteData = window.__EAGLE_SITE_DATA__;
  if (!siteData) {
    galleryGrid.innerHTML = `<div style="color:#ff8a8a;padding:16px;">未找到站点数据，请重新生成。</div>`;
    return;
  }
  siteData.folders = siteData.folders || siteData.Folders || [];
  siteData.images = siteData.images || siteData.Images || [];
  siteData.libraries = siteData.libraries || siteData.Libraries || [];
  siteData.allTags = siteData.allTags || siteData.AllTags || [];
  siteData.publishTag = siteData.publishTag || siteData.PublishTag || "发布";
  libraryName.textContent = siteData.siteName || "Eagle Multi Library";

  loadPrefs();
  initFolderCollapseDefaults();
  applyDeepLinkFromLocation({ openLightboxIfPresent: true });

  renderFolders();
  renderTagFilters();
  renderBreadcrumb();
  renderGallery();

  galleryViewport.addEventListener("scroll", scheduleVirtualRender, { passive: true });
  window.addEventListener("resize", scheduleVirtualRender);
}

function loadPrefs() {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return;
    const prefs = JSON.parse(raw);
    if (Array.isArray(prefs.collapsedLibraries)) {
      prefs.collapsedLibraries.forEach((id) => collapsedLibraryIds.add(id));
    }
    if (Array.isArray(prefs.collapsedFolders)) {
      prefs.collapsedFolders.forEach((id) => collapsedFolderIds.add(id));
      folderCollapseInitialized = true;
    }
    if (prefs.sortBy) {
      filterState.sortBy = prefs.sortBy;
      sortSelect.value = prefs.sortBy;
    }
    if (prefs.keyword) {
      filterState.keyword = prefs.keyword;
      searchInput.value = prefs.keyword;
    }
    if (prefs.libraryId) filterState.libraryId = prefs.libraryId;
    if (prefs.folderId) filterState.folderId = prefs.folderId;
    if (Array.isArray(prefs.selectedTags)) {
      prefs.selectedTags.forEach((t) => filterState.selectedTags.add(t));
    }
  } catch {
    // ignore broken prefs
  }
}

function savePrefs() {
  const prefs = {
    collapsedLibraries: [...collapsedLibraryIds],
    collapsedFolders: [...collapsedFolderIds],
    sortBy: filterState.sortBy,
    keyword: filterState.keyword,
    libraryId: filterState.libraryId,
    folderId: filterState.folderId,
    selectedTags: [...filterState.selectedTags]
  };
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(prefs));
  } catch {
    // ignore quota errors
  }
}

function initFolderCollapseDefaults() {
  if (folderCollapseInitialized) return;
  folderCollapseInitialized = true;
  for (const folder of siteData.folders) {
    if (hasChildFolders(folder.id)) collapsedFolderIds.add(folder.id);
  }
}

function sameId(a, b) {
  if (a == null && b == null) return true;
  return String(a || "") === String(b || "");
}

function hasChildFolders(folderId) {
  return siteData.folders.some((f) => sameId(f.parentId, folderId));
}

function countImagesInFolder(folderId) {
  return siteData.images.filter((img) =>
    (img.folderIds || []).some((fid) => isFolderOrDescendant(fid, folderId))
  ).length;
}

function toggleFolderCollapsed(folderId, opts = {}) {
  if (collapsedFolderIds.has(folderId)) collapsedFolderIds.delete(folderId);
  else collapsedFolderIds.add(folderId);
  savePrefs();
  if (!opts.skipRender) renderFolders();
}

function getChildFolders(libraryId, parentId) {
  return siteData.folders.filter(
    (f) => f.libraryId === libraryId && sameId(f.parentId, parentId)
  );
}

function thumbOf(image) {
  return image.thumbnailPath || image.ThumbnailPath || image.imagePath || image.ImagePath || "";
}

function originalOf(image) {
  return image.imagePath || image.ImagePath || thumbOf(image);
}

function renderFolders() {
  folderTree.innerHTML = "";
  const allBtn = createTopButton("全部图片", !filterState.libraryId && !filterState.folderId, () => {
    updateFilter({ libraryId: null, folderId: null, resetSelectedImage: true, closePanel: true });
  });
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
      savePrefs();
      renderFolders();
    };
    group.appendChild(header);

    if (!collapsed) {
      const content = document.createElement("div");
      content.className = "tree-library-content";
      const allInLib = document.createElement("button");
      allInLib.className = "tree-subitem all";
      allInLib.textContent = `该库全部图片 (${lib.imageCount || 0})`;
      if (filterState.libraryId === lib.id && !filterState.folderId) allInLib.classList.add("is-active");
      allInLib.onclick = () => updateFilter({ libraryId: lib.id, folderId: null, resetSelectedImage: true, closePanel: true });
      content.appendChild(allInLib);
      appendFolderNodes(content, lib.id, null, 0);
      group.appendChild(content);
    }
    folderTree.appendChild(group);
  }
}

function createTopButton(label, active, onClick) {
  const btn = document.createElement("button");
  btn.className = "tree-item";
  btn.textContent = label;
  if (active) btn.classList.add("is-active");
  btn.onclick = onClick;
  return btn;
}

function appendFolderNodes(container, libraryId, parentId, depth) {
  const folders = getChildFolders(libraryId, parentId);
  for (const folder of folders) {
    const hasChildren = hasChildFolders(folder.id);
    const row = document.createElement("div");
    row.className = "tree-folder-row";
    row.style.paddingLeft = `${depth * 12}px`;

    if (hasChildren) {
      const toggle = document.createElement("button");
      toggle.type = "button";
      toggle.className = "tree-folder-toggle";
      const isCollapsed = collapsedFolderIds.has(folder.id);
      toggle.textContent = isCollapsed ? "▶" : "▼";
      toggle.title = isCollapsed ? "展开" : "折叠";
      toggle.onclick = (e) => {
        e.stopPropagation();
        toggleFolderCollapsed(folder.id);
      };
      row.appendChild(toggle);
    } else {
      const spacer = document.createElement("span");
      spacer.className = "tree-folder-spacer";
      row.appendChild(spacer);
    }

    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "tree-subitem tree-folder-item";
    const count = countImagesInFolder(folder.id);
    btn.textContent = `${folder.name} (${count})`;
    if ((filterState.folderId || "") === (folder.id || "")) btn.classList.add("is-active");
    btn.onclick = () => {
      if (hasChildren) toggleFolderCollapsed(folder.id, { skipRender: true });
      updateFilter({
        libraryId: folder.libraryId || null,
        folderId: folder.id || null,
        resetSelectedImage: true,
        closePanel: true
      });
    };
    row.appendChild(btn);
    container.appendChild(row);

    if (hasChildren && !collapsedFolderIds.has(folder.id)) {
      appendFolderNodes(container, libraryId, folder.id, depth + 1);
    }
  }
}

function isHiddenDefaultTag(tag) {
  const publishTag = (siteData.publishTag || "发布").trim().toLowerCase();
  const value = (tag || "").trim().toLowerCase();
  return value === publishTag || value === "发布";
}

function renderTagFilters() {
  tagFilters.innerHTML = "";
  for (const tag of siteData.allTags) {
    if (isHiddenDefaultTag(tag)) continue;
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

function renderBreadcrumb() {
  breadcrumb.innerHTML = "";
  const parts = [{ label: "全部", libraryId: null, folderId: null }];

  if (filterState.libraryId) {
    const lib = siteData.libraries.find((l) => l.id === filterState.libraryId);
    parts.push({
      label: lib?.name || "图库",
      libraryId: filterState.libraryId,
      folderId: null
    });
  }

  if (filterState.folderId) {
    const chain = [];
    let current = siteData.folders.find((f) => sameId(f.id, filterState.folderId));
    const guard = new Set();
    while (current && !guard.has(current.id)) {
      guard.add(current.id);
      chain.unshift(current);
      current = current.parentId != null
        ? siteData.folders.find((f) => sameId(f.id, current.parentId))
        : null;
    }
    for (const folder of chain) {
      parts.push({
        label: folder.name,
        libraryId: folder.libraryId,
        folderId: folder.id
      });
    }
  }

  parts.forEach((part, index) => {
    if (index > 0) {
      const sep = document.createElement("span");
      sep.className = "sep";
      sep.textContent = "/";
      breadcrumb.appendChild(sep);
    }
    const isLast = index === parts.length - 1;
    if (isLast) {
      const cur = document.createElement("span");
      cur.className = "current";
      cur.textContent = part.label;
      breadcrumb.appendChild(cur);
    } else {
      const btn = document.createElement("button");
      btn.type = "button";
      btn.textContent = part.label;
      btn.onclick = () => updateFilter({
        libraryId: part.libraryId,
        folderId: part.folderId,
        resetSelectedImage: true
      });
      breadcrumb.appendChild(btn);
    }
  });
}

function updateFilter(opts = {}) {
  if ("libraryId" in opts) filterState.libraryId = opts.libraryId;
  if ("folderId" in opts) filterState.folderId = opts.folderId;
  if ("keyword" in opts) filterState.keyword = opts.keyword;
  if ("sortBy" in opts) filterState.sortBy = opts.sortBy;
  if (opts.resetSelectedImage) selectedImageId = null;
  savePrefs();
  syncUrlHash({ replace: true });
  renderFolders();
  renderTagFilters();
  renderBreadcrumb();
  renderGallery();
  if (opts.closePanel && window.innerWidth <= 900) leftPanel.classList.remove("is-open");
}

function isFolderOrDescendant(folderId, ancestorId) {
  if (!ancestorId) return true;
  if (sameId(folderId, ancestorId)) return true;
  let current = siteData.folders.find((f) => sameId(f.id, folderId));
  const guard = new Set();
  while (current && current.parentId != null && !guard.has(current.id)) {
    guard.add(current.id);
    if (sameId(current.parentId, ancestorId)) return true;
    current = siteData.folders.find((f) => sameId(f.id, current.parentId));
  }
  return false;
}

function getFilteredImages() {
  const keyword = (filterState.keyword || "").trim().toLowerCase();
  const selectedTags = [...filterState.selectedTags];
  const filtered = siteData.images.filter((img) => {
    const inLibrary = !filterState.libraryId || img.libraryId === filterState.libraryId;
    const inFolder = !filterState.folderId ||
      (img.folderIds || []).some((fid) => isFolderOrDescendant(fid, filterState.folderId));
    const inSearch = !keyword || (img.searchTokens || "").includes(keyword);
    const inTags = selectedTags.length === 0 || selectedTags.every((tag) => (img.tags || []).includes(tag));
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

function getGridMetrics() {
  const styles = getComputedStyle(galleryGrid);
  const gap = parseFloat(styles.gap) || 14;
  const pad = parseFloat(getComputedStyle(document.documentElement).getPropertyValue("--grid-pad")) || 16;
  const minCol = window.innerWidth <= 900 ? 150 : 200;
  const width = Math.max(galleryViewport.clientWidth - pad * 2, minCol);
  const cols = Math.max(1, Math.floor((width + gap) / (minCol + gap)));
  const cardH = parseFloat(getComputedStyle(document.documentElement).getPropertyValue("--card-h")) || 188;
  const rowH = cardH + gap;
  return { cols, rowH, gap, pad, cardH };
}

function scheduleVirtualRender() {
  if (galleryRaf) cancelAnimationFrame(galleryRaf);
  galleryRaf = requestAnimationFrame(() => {
    galleryRaf = 0;
    renderVirtualWindow();
  });
}

function renderGallery() {
  filteredImages = getFilteredImages();

  if (!filteredImages.length) {
    gallerySpacer.style.height = "120px";
    galleryGrid.style.transform = "translateY(0)";
    galleryGrid.innerHTML = `<div style="color:#9aa0aa;padding:8px;">当前筛选条件下没有图片。</div>`;
    renderDetail(null);
    return;
  }

  if (!selectedImageId || !filteredImages.some((x) => x.id === selectedImageId)) {
    selectedImageId = filteredImages[0].id;
  }

  const { cols, rowH, pad } = getGridMetrics();
  const rows = Math.ceil(filteredImages.length / cols);
  gallerySpacer.style.height = `${rows * rowH + pad * 2}px`;
  renderVirtualWindow();
  renderDetail(filteredImages.find((x) => x.id === selectedImageId) || null);
}

function renderVirtualWindow() {
  if (!filteredImages.length) return;
  const { cols, rowH, pad } = getGridMetrics();
  const scrollTop = galleryViewport.scrollTop;
  const viewH = galleryViewport.clientHeight;
  const buffer = 2;
  const startRow = Math.max(0, Math.floor((scrollTop - pad) / rowH) - buffer);
  const endRow = Math.min(
    Math.ceil(filteredImages.length / cols) - 1,
    Math.ceil((scrollTop + viewH - pad) / rowH) + buffer
  );
  const startIndex = startRow * cols;
  const endIndex = Math.min(filteredImages.length, (endRow + 1) * cols);

  galleryGrid.style.transform = `translateY(${startRow * rowH}px)`;
  galleryGrid.innerHTML = "";

  for (let i = startIndex; i < endIndex; i++) {
    const image = filteredImages[i];
    const card = document.createElement("article");
    card.className = "asset-card";
    if (image.id === selectedImageId) card.classList.add("is-selected");

    const thumbSrc = thumbOf(image);
    if (thumbSrc) {
      const img = document.createElement("img");
      img.className = "thumb";
      img.alt = image.name || image.id;
      img.loading = "lazy";
      img.decoding = "async";
      img.src = thumbSrc;
      img.addEventListener("click", (e) => {
        e.stopPropagation();
        openLightboxAt(i);
      });
      card.appendChild(img);
    } else {
      const ph = document.createElement("div");
      ph.className = "thumb thumb-placeholder";
      ph.textContent = "无缩略图";
      card.appendChild(ph);
    }

    const name = document.createElement("div");
    name.className = "asset-name";
    name.textContent = image.name || image.id;
    card.appendChild(name);

    card.onclick = () => {
      selectedImageId = image.id;
      syncUrlHash({ replace: true });
      renderVirtualWindow();
      renderDetail(image);
    };
    galleryGrid.appendChild(card);
  }
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

  const thumbSrc = thumbOf(image);
  const originalSrc = originalOf(image);
  preview.innerHTML = thumbSrc
    ? `<img src="${thumbSrc}" alt="${escapeHtml(image.name)}" />`
    : "无源图";
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
    const idx = filteredImages.findIndex((x) => x.id === image.id);
    if (idx >= 0) openLightboxAt(idx);
  };
  preview.style.cursor = originalSrc ? "zoom-in" : "default";
}

function buildDownloadName(image) {
  const base = (image.name || image.id || "image").replace(/[\\/:*?"<>|]/g, "_");
  const ext = (image.ext || "").replace(/^\./, "");
  return ext ? `${base}.${ext}` : base;
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

function showToast(message) {
  let toast = document.getElementById("toast");
  if (!toast) {
    toast = document.createElement("div");
    toast.id = "toast";
    toast.className = "toast";
    document.body.appendChild(toast);
  }
  toast.textContent = message;
  toast.classList.add("is-show");
  clearTimeout(showToast._timer);
  showToast._timer = setTimeout(() => toast.classList.remove("is-show"), 1600);
}

function buildShareUrl(imageId) {
  const url = new URL(location.href);
  url.search = "";
  url.hash = imageId ? `img=${encodeURIComponent(imageId)}` : "";
  return url.toString();
}

function syncUrlHash(opts = {}) {
  const next = selectedImageId ? `#img=${encodeURIComponent(selectedImageId)}` : "";
  if (opts.replace) history.replaceState(null, "", next || location.pathname + location.search);
  else history.pushState(null, "", next || location.pathname + location.search);
}

function applyDeepLinkFromLocation(opts = {}) {
  const hash = location.hash || "";
  const match = hash.match(/^#img=(.+)$/);
  if (!match) return;
  const id = decodeURIComponent(match[1]);
  const image = siteData.images.find((x) => x.id === id);
  if (!image) return;
  selectedImageId = id;
  filterState.libraryId = image.libraryId || null;
  filterState.folderId = (image.folderIds && image.folderIds[0]) || null;
  if (opts.openLightboxIfPresent) {
    // open after first gallery compute
    setTimeout(() => {
      filteredImages = getFilteredImages();
      const idx = filteredImages.findIndex((x) => x.id === id);
      if (idx >= 0) openLightboxAt(idx);
    }, 0);
  }
}

let lightboxCloseTimer = null;

function openLightboxAt(index) {
  if (index < 0 || index >= filteredImages.length) return;
  lightboxIndex = index;
  const image = filteredImages[index];
  selectedImageId = image.id;
  syncUrlHash({ replace: true });

  const originalSrc = originalOf(image);
  if (lightboxCloseTimer) {
    clearTimeout(lightboxCloseTimer);
    lightboxCloseTimer = null;
  }

  lightboxCaption.textContent = `${image.name || image.id} (${index + 1}/${filteredImages.length})`;

  lightboxImage.alt = image.name || "全屏预览";
  // show thumb first, then swap to original for perceived speed
  const thumbSrc = thumbOf(image);
  lightboxImage.src = thumbSrc || originalSrc;
  lightbox.setAttribute("aria-hidden", "false");
  lightbox.classList.remove("is-open");
  void lightbox.offsetWidth;
  requestAnimationFrame(() => lightbox.classList.add("is-open"));

  if (originalSrc && originalSrc !== thumbSrc) {
    const loader = new Image();
    loader.onload = () => {
      if (lightboxIndex === index && lightbox.classList.contains("is-open")) {
        lightboxImage.src = originalSrc;
      }
    };
    loader.src = originalSrc;
  }

  renderDetail(image);
  scheduleVirtualRender();
}

function closeLightbox() {
  lightbox.classList.remove("is-open");
  lightbox.setAttribute("aria-hidden", "true");
  lightboxIndex = -1;
  lightboxCloseTimer = setTimeout(() => {
    if (!lightbox.classList.contains("is-open")) lightboxImage.src = "";
    lightboxCloseTimer = null;
  }, 280);
}

function stepLightbox(delta) {
  if (!lightbox.classList.contains("is-open") || !filteredImages.length) return;
  const next = (lightboxIndex + delta + filteredImages.length) % filteredImages.length;
  openLightboxAt(next);
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
  savePrefs();
  syncUrlHash({ replace: true });
  renderFolders();
  renderTagFilters();
  renderBreadcrumb();
  renderGallery();
});
openFolderPanel?.addEventListener("click", () => leftPanel.classList.add("is-open"));
closeFolderPanel?.addEventListener("click", () => leftPanel.classList.remove("is-open"));

lightboxClose?.addEventListener("click", closeLightbox);lightboxPrev?.addEventListener("click", () => stepLightbox(-1));
lightboxNext?.addEventListener("click", () => stepLightbox(1));
lightbox?.addEventListener("click", (e) => {
  if (e.target === lightbox) closeLightbox();
});

window.addEventListener("keydown", (e) => {
  if (e.key === "/" && document.activeElement !== searchInput) {
    e.preventDefault();
    searchInput.focus();
    searchInput.select();
    return;
  }
  if (!lightbox?.classList.contains("is-open")) return;
  if (e.key === "Escape") closeLightbox();
  if (e.key === "ArrowLeft") stepLightbox(-1);
  if (e.key === "ArrowRight") stepLightbox(1);
});

window.addEventListener("hashchange", () => {
  applyDeepLinkFromLocation({ openLightboxIfPresent: true });
  renderFolders();
  renderBreadcrumb();
  renderGallery();
});

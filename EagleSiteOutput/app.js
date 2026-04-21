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
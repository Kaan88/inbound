// Lays out one store image (store-frame.html): a caption and the extension page in an iframe, scaled for the popup.
// Query: kind=shot|promo, caption, sub, page (an extension page with its own query), w, h ("auto": the page's own
// height), scale, top (promo: the crop's offset), clip (promo: the crop's height). Sets window.frameReady when drawn.
(() => {
  const query = new URLSearchParams(location.search);
  const kind = query.get("kind") ?? "shot";
  const width = Number(query.get("w") ?? "380");
  const scale = Number(query.get("scale") ?? "1");
  const fixedHeight = query.get("h") === "auto" ? 0 : Number(query.get("h") ?? "0");

  const iframe = document.createElement("iframe");
  iframe.src = `/${query.get("page") ?? "popup.html"}`;
  iframe.width = String(width);
  iframe.title = "Extension page";
  iframe.style.width = `${width}px`;
  iframe.style.transform = scale === 1 ? "" : `scale(${scale})`;
  const surface = document.createElement("div");
  surface.className = "surface";

  if (kind === "promo") {
    document.body.classList.add("promo");
    const head = document.createElement("div");
    head.className = "promo-head";
    const icon = document.createElement("img");
    icon.src = "/icons/icon128.png";
    icon.width = 64;
    icon.height = 64;
    icon.alt = "";
    const text = document.createElement("div");
    const title = document.createElement("h1");
    title.textContent = "Inbound Download Manager";
    const sub = document.createElement("p");
    sub.textContent = "Send browser downloads to your Windows download manager";
    text.append(title, sub);
    head.append(icon, text);
    document.body.append(head, surface);
  } else {
    document.documentElement.style.setProperty("--caption-width", query.get("captionWidth") ? `${query.get("captionWidth")}px` : "430px");
    const caption = document.createElement("div");
    caption.className = "caption";
    const icon = document.createElement("img");
    icon.src = "/icons/icon128.png";
    icon.width = 64;
    icon.height = 64;
    icon.alt = "";
    const title = document.createElement("h1");
    title.textContent = query.get("caption") ?? "";
    const sub = document.createElement("p");
    sub.textContent = query.get("sub") ?? "";
    caption.append(icon, title, sub);
    const stage = document.createElement("div");
    stage.className = "stage";
    stage.append(surface);
    document.body.append(caption, stage);
  }
  surface.append(iframe);

  /** Sizes the iframe and its frame; store-screens.mjs calls it once the page inside is ready. */
  window.fitFrame = () => {
    const inner = iframe.contentDocument;
    const contentHeight = fixedHeight || Math.ceil(inner.documentElement.getBoundingClientRect().height);
    iframe.style.height = `${contentHeight}px`;
    iframe.height = String(contentHeight);
    if (kind === "promo") {
      const top = Number(query.get("top") ?? "0");
      const clip = Number(query.get("clip") ?? "114");
      iframe.style.top = `${-top}px`;
      surface.style.width = `${width}px`;
      surface.style.height = `${clip}px`;
    } else {
      surface.style.width = `${Math.round(width * scale)}px`;
      surface.style.height = `${Math.round(contentHeight * scale)}px`;
    }
    window.frameReady = true;
    return contentHeight;
  };
})();

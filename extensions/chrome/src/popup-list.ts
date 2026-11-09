// The popup's download rows (EXT.R8 §3.1): one list, 56 px rows of constant height (name and size, status line, a
// reserved bar slot), at most two buttons in a fixed column, and group rows "This PC" / "<server> (server)" when a
// server is paired. Rows are keyed by place and job id and updated in place, never rebuilt, so a focused button
// keeps its focus across the 1 s poll. Words and numbers are the web UI's (webui/src/lib/format.ts), which are the
// desktop list's.

import { bytes, duration, speed, statusText, tone } from "../../../webui/src/lib/format";
import type { Job } from "../../../webui/src/lib/types";
import { fileIcon, icon } from "./ui/icon";
import { actionsFor, groupLabel, latestJobs, POPUP_JOB_LIMIT, type JobAction, type JobSummary } from "./job-list";

export interface PopupListView {
  /** A server is paired and this PC is used too: rows come in two groups. */
  paired: boolean;
  serverName: string;
  /** Whether each place answered the last jobs query. */
  groupsOk: Partial<Record<"local" | "server", boolean>>;
}

export interface PopupListHandlers {
  onAction: (job: JobSummary, action: JobAction) => void;
  onOpen: (job: JobSummary) => void;
}

const ACTION_LABELS: Record<JobAction, string> = { start: "Start", pause: "Stop", resume: "Resume", retry: "Retry", remove: "Delete" };
const ACTION_ICONS: Record<JobAction, string> = { start: "play", pause: "pause", resume: "play", retry: "retry", remove: "trash" };
const ACTION_TINTS: Record<JobAction, string> = {
  start: "tint-start", pause: "tint-pause", resume: "tint-start", retry: "tint-info", remove: "tint-danger",
};

/** Seconds left for a download, from the Host's estimate (a TimeSpan text, ISO 8601, seconds or ticks) or its rate. */
export function parseEtaSeconds(job: JobSummary): number | null {
  const value = job.estimatedRemaining;
  if (typeof value === "number" && Number.isFinite(value) && value > 0) {
    // Ticks are far larger than any plausible second count.
    return value > 1_000_000_000 ? value / 10_000_000 : value;
  }

  if (typeof value === "string" && value.trim().length > 0) {
    const text = value.trim();
    const iso = /^P(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:([\d.]+)S)?)?$/i.exec(text);
    if (iso) {
      const total = Number(iso[1] ?? 0) * 86_400 + Number(iso[2] ?? 0) * 3_600 + Number(iso[3] ?? 0) * 60 + Number(iso[4] ?? 0);
      return total > 0 ? total : null;
    }

    // "hh:mm:ss" or "d.hh:mm:ss.fffffff".
    const span = /^(?:(\d+)\.)?(\d+):(\d+):(\d+)(?:\.\d+)?$/.exec(text);
    if (span) {
      const total = Number(span[1] ?? 0) * 86_400 + Number(span[2]) * 3_600 + Number(span[3]) * 60 + Number(span[4]);
      return total > 0 ? total : null;
    }
  }

  const rate = job.bytesPerSecond ?? 0;
  if (rate > 0 && typeof job.totalBytes === "number" && job.totalBytes > job.receivedBytes) {
    return (job.totalBytes - job.receivedBytes) / rate;
  }
  return null;
}

function isActive(state: string): boolean {
  return state === "connecting" || state === "downloading" || state === "retrying" || state === "verifying" || state === "muxing";
}

/** The row's words: the status (with percent), and the detail after it (rate and time left, or the failure). */
export function rowText(job: JobSummary): { status: string; detail: string; size: string; tone: string } {
  const status = statusText(job as unknown as Job);
  const parts: string[] = [];
  if (isActive(job.state)) {
    const rate = speed(job.bytesPerSecond);
    if (rate) parts.push(rate);
    const left = duration(parseEtaSeconds(job));
    if (left) parts.push(left);
  } else if (job.failure?.message && job.state !== "cancelled") {
    // The state already says "Cancelled"; only real errors add detail.
    parts.push(job.failure.message);
  }
  const size = typeof job.totalBytes === "number" && job.totalBytes > 0 ? bytes(job.totalBytes) : "";
  return { status, detail: parts.join(" · "), size, tone: tone(job.state) };
}

/** The bar: a fraction for a download or a pause with a known size; "moving" while downloading an unknown size. */
export function rowBar(job: JobSummary): number | "moving" | null {
  const known = typeof job.totalBytes === "number" && job.totalBytes > 0;
  if (job.state === "downloading" && !known) return "moving";
  if ((job.state === "downloading" || job.state === "paused") && known) {
    const fraction = typeof job.progress === "number" ? job.progress : job.receivedBytes / job.totalBytes!;
    return Math.max(0, Math.min(1, fraction));
  }
  return null;
}

interface RowParts {
  item: HTMLLIElement;
  glyph: HTMLSpanElement;
  name: HTMLSpanElement;
  size: HTMLSpanElement;
  status: HTMLSpanElement;
  detail: HTMLSpanElement;
  bar: HTMLSpanElement;
  fill: HTMLSpanElement;
  actions: HTMLDivElement;
  job: JobSummary;
  signature: string;
  glyphFor: string;
}

const rowsByList = new WeakMap<HTMLElement, Map<string, RowParts | HTMLLIElement>>();

function keyOf(job: JobSummary): string {
  return `${job.target ?? "local"}:${job.id}`;
}

function createRow(handlers: PopupListHandlers): RowParts {
  const item = document.createElement("li");
  item.className = "job";
  item.tabIndex = 0;
  const glyph = document.createElement("span");
  glyph.className = "file-glyph";
  glyph.setAttribute("aria-hidden", "true");
  const main = document.createElement("div");
  main.className = "job-main";
  const line1 = document.createElement("div");
  line1.className = "job-line1";
  const name = document.createElement("span");
  name.className = "job-name";
  const size = document.createElement("span");
  size.className = "job-size tabular";
  line1.append(name, size);
  const line2 = document.createElement("div");
  line2.className = "job-line2";
  const status = document.createElement("span");
  status.className = "job-status";
  const detail = document.createElement("span");
  detail.className = "job-detail tabular";
  line2.append(status, detail);
  const bar = document.createElement("span");
  bar.className = "progress job-bar";
  const fill = document.createElement("span");
  bar.append(fill);
  main.append(line1, line2, bar);
  const actions = document.createElement("div");
  actions.className = "job-actions";
  item.append(glyph, main, actions);
  const parts: RowParts = { item, glyph, name, size, status, detail, bar, fill, actions, job: {} as JobSummary, signature: "", glyphFor: "" };
  item.addEventListener("dblclick", (event) => {
    if ((event.target as Element).closest?.("button")) return;
    handlers.onOpen(parts.job);
  });
  item.addEventListener("keydown", (event) => {
    if (event.key === "Enter" && event.target === item) handlers.onOpen(parts.job);
  });
  return parts;
}

function updateRow(parts: RowParts, job: JobSummary, handlers: PopupListHandlers): void {
  parts.job = job;
  const name = job.fileName || job.id;
  const text = rowText(job);
  if (parts.glyphFor !== name) {
    parts.glyph.replaceChildren(fileIcon(name, 16));
    parts.glyphFor = name;
  }
  parts.name.textContent = name;
  parts.name.title = name;
  parts.size.textContent = text.size;
  parts.status.textContent = text.status;
  parts.status.className = `job-status tone-${text.tone}`;
  // A no-break space: a flex item drops its leading space.
  parts.detail.textContent = text.detail ? (job.state === "failed" ? ` · ${text.detail}` : text.detail) : "";
  parts.detail.title = text.detail;
  parts.detail.classList.toggle("job-detail-end", isActive(job.state));
  parts.item.setAttribute("aria-label", [name, text.status, text.size].filter(Boolean).join(", "));

  const bar = rowBar(job);
  parts.bar.classList.toggle("slot-empty", bar === null);
  parts.bar.classList.toggle("indeterminate", bar === "moving");
  parts.fill.style.width = typeof bar === "number" ? `${(bar * 100).toFixed(1)}%` : "";

  const actions = actionsFor(job);
  const signature = actions.join(",") + `|${name}`;
  if (signature !== parts.signature) {
    parts.signature = signature;
    parts.actions.replaceChildren(...actions.map((action) => {
      const button = document.createElement("button");
      button.type = "button";
      button.className = `btn-icon ${ACTION_TINTS[action]}`;
      button.dataset.action = action;
      button.title = ACTION_LABELS[action];
      button.setAttribute("aria-label", `${ACTION_LABELS[action]} ${name}`);
      button.append(icon(ACTION_ICONS[action], 16));
      button.addEventListener("click", () => handlers.onAction(parts.job, action));
      return button;
    }));
  }
}

function groupRow(rows: Map<string, RowParts | HTMLLIElement>, key: string, text: string, className: string): HTMLLIElement {
  let row = rows.get(key) as HTMLLIElement | undefined;
  if (!row) {
    row = document.createElement("li");
    row.className = className;
    row.setAttribute("role", "presentation");
    rows.set(key, row);
  }
  row.textContent = text;
  return row;
}

/**
 * Draws the rows into the list, reusing the ones already there. Returns how many downloads are shown. The newest
 * of each place come first (20 each); with a server paired, this PC's group comes first, then the server's.
 */
export function renderPopupRows(list: HTMLElement, jobs: readonly JobSummary[], view: PopupListView, handlers: PopupListHandlers): number {
  let rows = rowsByList.get(list);
  if (!rows) { rows = new Map(); rowsByList.set(list, rows); }
  const wanted: HTMLLIElement[] = [];
  let shown = 0;
  const addJobs = (group: readonly JobSummary[]) => {
    for (const job of latestJobs(group, POPUP_JOB_LIMIT)) {
      const key = keyOf(job);
      let parts = rows!.get(key) as RowParts | undefined;
      if (!parts || !(parts as RowParts).item) {
        parts = createRow(handlers);
        rows!.set(key, parts);
      }
      updateRow(parts, job, handlers);
      wanted.push(parts.item);
      shown++;
    }
  };

  if (view.paired) {
    for (const target of ["local", "server"] as const) {
      const group = jobs.filter((job) => (job.target ?? "local") === target);
      wanted.push(groupRow(rows, `group:${target}`, groupLabel(target, view.serverName), "group-row"));
      if (view.groupsOk[target] === false) {
        wanted.push(groupRow(rows, `note:${target}`, "Not answering", "group-note"));
      } else if (group.length === 0) {
        wanted.push(groupRow(rows, `note:${target}`, "No downloads", "group-note"));
      }
      addJobs(group);
    }
  } else {
    addJobs(jobs);
  }

  // Put the rows in order, moving only what is out of place, so a focused row stays where it is.
  const keep = new Set(wanted);
  for (const child of Array.from(list.children)) {
    if (!keep.has(child as HTMLLIElement)) child.remove();
  }
  wanted.forEach((row, index) => {
    if (list.children[index] !== row) list.insertBefore(row, list.children[index] ?? null);
  });
  for (const [key, value] of rows) {
    const node = (value as RowParts).item ?? (value as HTMLLIElement);
    if (!keep.has(node)) rows.delete(key);
  }
  return shown;
}

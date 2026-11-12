// A confirmation over the page (EXT.R8): the kit's .dialog with a backdrop, buttons bottom right with the primary first
// and the cancel last (docs/design-guidelines.md §6). Focus is trapped inside, starts on Cancel for a destructive
// question, Esc cancels, and focus goes back to what had it before.

export interface ConfirmOptions {
  title: string;
  message: string;
  confirmLabel: string;
  cancelLabel?: string;
  danger?: boolean;
  /** A box under the message, unticked unless said. */
  checkbox?: { label: string; checked?: boolean };
  /** A muted line under the message. */
  note?: string;
  /**
   * A text box under the message (EXT.R11 Rename), focused with the part before the last dot selected. Enter confirms.
   * The answer carries what it holds.
   */
  input?: { label: string; value: string };
}

export interface ConfirmAnswer {
  checked: boolean;
  /** The text box's value, when there is one. */
  value?: string;
}

let openCount = 0;
let dialogSerial = 0;

/** Whether a dialog is open on this page. */
export function dialogOpen(): boolean {
  return openCount > 0;
}

/** Shows the question; resolves with the box's state on confirm, or null on cancel. */
export function confirmDialog(options: ConfirmOptions, host: HTMLElement = document.body): Promise<ConfirmAnswer | null> {
  const returnTo = document.activeElement as HTMLElement | null;
  const backdrop = document.createElement("div");
  backdrop.className = "dialog-backdrop";
  const dialog = document.createElement("div");
  dialog.className = "dialog";
  dialog.setAttribute("role", options.danger ? "alertdialog" : "dialog");
  dialog.setAttribute("aria-modal", "true");
  const titleId = `dialog-title-${++dialogSerial}`;
  dialog.setAttribute("aria-labelledby", titleId);

  const header = document.createElement("div");
  header.className = "dialog-header";
  header.id = titleId;
  header.textContent = options.title;

  const body = document.createElement("div");
  body.className = "dialog-body";
  const message = document.createElement("p");
  message.className = "dialog-message";
  message.textContent = options.message;
  body.append(message);
  let box: HTMLInputElement | undefined;
  if (options.checkbox) {
    const label = document.createElement("label");
    label.className = "check";
    box = document.createElement("input");
    box.type = "checkbox";
    box.checked = options.checkbox.checked === true;
    const text = document.createElement("span");
    text.textContent = options.checkbox.label;
    label.append(box, text);
    body.append(label);
  }
  let field: HTMLInputElement | undefined;
  if (options.input) {
    const label = document.createElement("label");
    label.className = "dialog-field";
    const text = document.createElement("span");
    text.textContent = options.input.label;
    field = document.createElement("input");
    field.type = "text";
    field.className = "field";
    field.value = options.input.value;
    field.spellcheck = false;
    field.autocomplete = "off";
    label.append(text, field);
    body.append(label);
  }
  if (options.note) {
    const note = document.createElement("p");
    note.className = "hint";
    note.textContent = options.note;
    body.append(note);
  }

  const footer = document.createElement("div");
  footer.className = "dialog-footer";
  const confirm = document.createElement("button");
  confirm.type = "button";
  confirm.className = options.danger ? "btn-danger" : "btn-primary";
  confirm.textContent = options.confirmLabel;
  const cancel = document.createElement("button");
  cancel.type = "button";
  cancel.className = "btn";
  cancel.textContent = options.cancelLabel ?? "Cancel";
  footer.append(confirm, cancel);
  dialog.append(header, body, footer);
  backdrop.append(dialog);
  host.append(backdrop);
  openCount++;
  // A short popup (one download is about 120 px) would leave the question no room: the page grows while it is open.
  host.classList.add("dialog-open");

  return new Promise((resolve) => {
    const finish = (answer: ConfirmAnswer | null) => {
      openCount--;
      backdrop.remove();
      if (!host.querySelector(":scope > .dialog-backdrop")) host.classList.remove("dialog-open");
      document.removeEventListener("keydown", onKey, true);
      try { returnTo?.focus(); } catch { /* gone with a redraw */ }
      resolve(answer);
    };
    const focusable = () => Array.from(dialog.querySelectorAll<HTMLElement>("button, input"))
      .filter((element) => !(element as HTMLButtonElement).disabled);
    const onKey = (event: KeyboardEvent) => {
      if (event.key === "Escape") { event.preventDefault(); event.stopPropagation(); finish(null); return; }
      if (event.key !== "Tab") return;
      const items = focusable();
      if (items.length === 0) return;
      const first = items[0]!;
      const last = items[items.length - 1]!;
      if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
      else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
    };
    document.addEventListener("keydown", onKey, true);
    const answer = (): ConfirmAnswer => field ? { checked: box?.checked === true, value: field.value } : { checked: box?.checked === true };
    confirm.addEventListener("click", () => finish(answer()));
    field?.addEventListener("keydown", (event) => {
      if (event.key === "Enter") { event.preventDefault(); finish(answer()); }
    });
    cancel.addEventListener("click", () => finish(null));
    backdrop.addEventListener("click", (event) => { if (event.target === backdrop) finish(null); });
    if (field) {
      field.focus();
      const dot = field.value.lastIndexOf(".");
      field.setSelectionRange(0, dot > 0 ? dot : field.value.length);
    } else {
      (options.danger ? cancel : confirm).focus();
    }
  });
}

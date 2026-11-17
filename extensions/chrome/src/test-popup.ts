// EXT.R15: TEST BUILD ONLY. An opt-in switch, "Open the popup when a download from this browser finishes". This module
// is reached only through `if (__TEST_POPUP__)` (a build-time define, see scripts/build.mjs --test-popup), so the
// store builds contain neither the option nor this code.


export const TEST_POPUP_KEY = "testOpenPopupOnFinish";

export async function testPopupEnabled(): Promise<boolean> {
  try {
    return (await chrome.storage.local.get(TEST_POPUP_KEY))[TEST_POPUP_KEY] === true;
  } catch {
    return false;
  }
}

export interface FinishPopupDeps {
  /** The focused normal browser window, if any. */
  focusedWindow: () => Promise<{ id?: number } | undefined>;
  /** The ask-where-to-save popup (EXT.R7) is open or opening, or has downloads waiting. */
  askPopupBusy: () => boolean;
}

/**
 * Opens the popup for a finished download when the switch is on, a browser window is focused and the ask popup is not
 * in the way. True when it did; false means "fall back to the finished mark". Failures are silent.
 */
export async function openPopupForTest(deps: FinishPopupDeps): Promise<boolean> {
  try {
    if (!(await testPopupEnabled())) return false;
    if (deps.askPopupBusy()) return false;
    const action = (chrome as { action?: typeof chrome.action }).action;
    if (typeof action?.openPopup !== "function") return false;
    const front = await deps.focusedWindow();
    if (!front || typeof front.id !== "number") return false;
    await action.openPopup({ windowId: front.id });
    return true;
  } catch {
    return false;
  }
}

/** The switch on the options page, with a note that it is a test build. */
export function mountTestPopupOption(doc: Document): void {
  const host = doc.querySelector("#onFinish")?.closest(".form-grid") ?? doc.querySelector("main") ?? doc.body;
  const label = doc.createElement("label");
  label.className = "check";
  label.id = "testPopupOption";
  const box = doc.createElement("input");
  box.type = "checkbox";
  box.id = "testOpenPopupOnFinish";
  label.append(box, doc.createTextNode(" Open the popup when a download from this browser finishes (test build)"));
  const hint = doc.createElement("p");
  hint.className = "hint";
  hint.textContent = "Only while this browser window is in front and the where-to-save popup is not showing; otherwise the icon shows the finished mark. Off by default.";
  host.after(label, hint);
  void testPopupEnabled().then((on) => { box.checked = on; });
  box.addEventListener("change", (event) => {
    event.stopPropagation(); // it saves itself; it is not an Apply change
    void chrome.storage.local.set({ [TEST_POPUP_KEY]: box.checked }).catch(() => undefined);
  });
}

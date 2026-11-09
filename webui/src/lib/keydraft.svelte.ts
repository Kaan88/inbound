// A provider key someone typed and has not saved yet. It lives in memory only, so leaving the page does not lose it
// and nothing secret reaches browser storage.
export const keyDraft = $state<Record<string, string>>({});

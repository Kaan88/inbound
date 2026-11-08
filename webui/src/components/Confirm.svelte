<script lang="ts">
  import Dialog from './Dialog.svelte';

  // A question with Cancel as the default: it comes first, so it has the focus and Enter on it does nothing.
  let { title, message, primary, danger = false, busy = false, onconfirm, onclose }: {
    title: string;
    message: string;
    primary: string;
    danger?: boolean;
    busy?: boolean;
    onconfirm: () => void;
    onclose: () => void;
  } = $props();
</script>

<Dialog {title} {onclose}>
  <p class="text-[13px]">{message}</p>
  {#snippet footer()}
    <button class="btn" onclick={onclose}>Cancel</button>
    <button class={danger ? 'btn-danger' : 'btn-primary'} disabled={busy} onclick={onconfirm}>{primary}</button>
  {/snippet}
</Dialog>

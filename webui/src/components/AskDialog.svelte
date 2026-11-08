<script lang="ts">
  import Dialog from './Dialog.svelte';

  // A question with a safe default: the cancelling button comes first, so it takes the focus (Enter keeps things as they are).
  let { title, text, confirm, keep = 'Cancel', danger = false, busy = false, onconfirm, onclose }: {
    title: string;
    text: string;
    confirm: string;
    keep?: string;
    danger?: boolean;
    busy?: boolean;
    onconfirm: () => void;
    onclose: () => void;
  } = $props();
</script>

<Dialog {title} {onclose}>
  <p class="text-[13px]">{text}</p>
  {#snippet footer()}
    <button class="btn" onclick={onclose}>{keep}</button>
    <button class={danger ? 'btn-danger' : 'btn-primary'} disabled={busy} onclick={onconfirm}>{confirm}</button>
  {/snippet}
</Dialog>

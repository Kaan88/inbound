<script lang="ts">
  import Dialog from './Dialog.svelte';

  // A question with a safe default: the cancelling button comes first, so it takes the focus (Enter keeps things as they are).
  let { title, text, confirm, keep = 'Cancel', danger = false, busy = false, sheet = false, onconfirm, onclose }: {
    title: string;
    text: string;
    confirm: string;
    keep?: string;
    danger?: boolean;
    busy?: boolean;
    /** A phone: slides up from the bottom as a sheet instead of filling the screen. */
    sheet?: boolean;
    onconfirm: () => void;
    onclose: () => void;
  } = $props();
</script>

<Dialog {title} {onclose} {sheet}>
  <p class="text-[13px]">{text}</p>
  {#snippet footer()}
    <!-- A sheet on a phone: the two buttons full width, the action on top and Cancel under it (the mockup's sheets). -->
    <button class="btn {sheet ? 'max-sm:order-2 max-sm:w-full max-sm:justify-center max-sm:min-h-11' : ''}" onclick={onclose}>{keep}</button>
    <button class="{danger ? 'btn-danger' : 'btn-primary'} {sheet ? 'max-sm:order-1 max-sm:w-full max-sm:justify-center max-sm:min-h-11' : ''}" disabled={busy} onclick={onconfirm}>{confirm}</button>
  {/snippet}
</Dialog>

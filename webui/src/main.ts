import { mount } from 'svelte';
import { tabHandoff, takeHandoff } from './lib/hosts';
import App from './App.svelte';
import './app.css';

// A jump from another host's web UI brings that host and its list in the fragment (U-WU-1); it comes off first.
tabHandoff.value = takeHandoff();

export default mount(App, { target: document.getElementById('app')! });

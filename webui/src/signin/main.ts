import { mount } from 'svelte';
import SignIn from './SignIn.svelte';
import '../app.css';

export default mount(SignIn, { target: document.getElementById('app')! });

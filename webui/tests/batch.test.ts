import { describe, expect, it } from 'vitest';
import { removeInputs } from '../src/lib/addlines';
import { extractInputs, patternProblem } from '../src/lib/addview';
import { expandNumbered } from '../src/lib/batch';

describe('numbered addresses (U-WU-11), as BatchUrlExpander', () => {
  it('expands a range, keeps leading zeros, runs letters and lists, and multiplies groups', () => {
    expect(expandNumbered('http://h/img[1-3].jpg?size=1000')).toEqual({ ok: true, urls: ['http://h/img1.jpg?size=1000', 'http://h/img2.jpg?size=1000', 'http://h/img3.jpg?size=1000'] });
    expect((expandNumbered('http://h/p[08-10].png') as { urls: string[] }).urls).toEqual(['http://h/p08.png', 'http://h/p09.png', 'http://h/p10.png']);
    expect((expandNumbered('http://h/[c-a]') as { urls: string[] }).urls).toEqual(['http://h/c', 'http://h/b', 'http://h/a']);
    expect((expandNumbered('http://h/[x,y]/[1-2]') as { urls: string[] }).urls).toEqual(['http://h/x/1', 'http://h/x/2', 'http://h/y/1', 'http://h/y/2']);
  });

  it('leaves an address without a pattern, an IPv6 host and a PHP-style [] alone', () => {
    expect(expandNumbered('http://h/file.zip')).toBeNull();
    expect(expandNumbered('http://[::1]:8080/file.zip')).toBeNull();
    expect(expandNumbered('http://h/f?a[]=1')).toBeNull();
    expect(expandNumbered('magnet:?xt=urn:btih:abc')).toBeNull();
  });

  it('says what is wrong with a range it cannot expand', () => {
    expect(expandNumbered('http://h/[1-b]')).toEqual({ ok: false, error: 'The range [1-b] must use numbers, or single letters of the same case.' });
    expect(expandNumbered('http://h/[a,,b]')).toEqual({ ok: false, error: 'The list [a,,b] has an empty item.' });
    expect(expandNumbered('http://h/[1-5000]')?.ok).toBe(false);
    expect(patternProblem('http://h/[1-b]')).toMatch(/Nothing was added/);
    expect(patternProblem('http://h/a.zip')).toBeNull();
  });

  it('turns a pasted pattern into one line per address', () => {
    expect(extractInputs('get http://127.0.0.1:18850/file/img[1-3].jpg?size=1000 now')).toEqual([
      'http://127.0.0.1:18850/file/img1.jpg?size=1000', 'http://127.0.0.1:18850/file/img2.jpg?size=1000', 'http://127.0.0.1:18850/file/img3.jpg?size=1000',
    ]);
    expect(extractInputs('http://h/[1-b]')).toEqual(['http://h/[1-b]']);
  });

  it('takes the pattern out of the text once every address it made was added', () => {
    expect(removeInputs('http://h/[1-2]\nhttp://other/x', ['http://h/1', 'http://h/2'])).toBe('http://other/x');
    expect(removeInputs('http://h/[1-2]', ['http://h/1'])).toBe('http://h/[1-2]');
  });
});

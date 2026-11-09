import { describe, expect, it } from 'vitest';
import { pathParts, samePath, uniquePaths } from '../src/lib/paths';

describe('paths in narrow places (U-WU-5, U-WR-14)', () => {
  it('keeps the last folder as the part that always shows', () => {
    expect(pathParts('C:\\Users\\kaank\\Downloads\\Compressed')).toEqual({ head: 'C:\\Users\\kaank\\Downloads', tail: '\\Compressed' });
    expect(pathParts('/downloads/movies/')).toEqual({ head: '/downloads', tail: '/movies' });
    expect(pathParts('/downloads')).toEqual({ head: '', tail: '/downloads' });
    expect(pathParts('C:\\')).toEqual({ head: '', tail: 'C:\\' });
    expect(pathParts('/')).toEqual({ head: '', tail: '/' });
    expect(pathParts('C:\\Users')).toEqual({ head: 'C:', tail: '\\Users' });
  });

  it('two Recent entries that differ only at the end show different tails', () => {
    const a = pathParts('C:\\Users\\kaank\\Downloads\\idm\\Compressed');
    const b = pathParts('C:\\Users\\kaank\\Downloads\\idm\\Programs');
    expect(a.tail).not.toBe(b.tail);
  });

  it('lists a folder once however it is written', () => {
    expect(samePath('C:\\Users\\Me\\Downloads', 'c:\\users\\me\\downloads\\')).toBe(true);
    expect(samePath('/downloads/A', '/downloads/a')).toBe(false);
    expect(uniquePaths(['C:\\Users\\Me\\Downloads', 'C:\\Users\\Me\\Downloads\\', 'D:\\Other'])).toEqual(['C:\\Users\\Me\\Downloads', 'D:\\Other']);
  });
});

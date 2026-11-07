# Extracting archives

Inbound can extract archives after they download, or when you ask, with the copy of 7-Zip that comes with
it. Nothing else needs to be installed, and there is no program path to set. It handles single archives and
split sets (`name.part01.rar`, or `name.rar` with `name.r00`), RAR4 and RAR5, and archives protected by a
password, including ones with encrypted file names. A split set waits until every volume has arrived, and is
extracted once. Automatic extraction is off until you turn it on, and archives are kept unless you choose
to delete them, which happens only after a verified success. Files go through a temporary folder next to
their destination and appear only when the whole set has checked out, so a crash or restart cannot leave
a half-extracted result: the set simply runs again. An archive that tries to write outside its folder, or
that holds links, is refused. NTFS data streams stored in an archive are not written. When the archive
itself carries the mark of the web, the extracted files get the same zone, without the addresses.

A password you enter is stored encrypted for your Windows account. A password for one set is forgotten after
it has worked or been refused, and a password for a package stays until the package is gone, since it may
fit its other sets. It reaches 7-Zip through its standard input, as UTF-8, never on its command line, so other
programs cannot read it there. 7-Zip uses at most the first 127 characters of a password.

## 7-Zip

Extraction uses 7-Zip by Igor Pavlov, which is free software under the GNU LGPL, with the unRAR license
restriction for its RAR decoder. Inbound ships the unmodified 7-Zip 26.04 console program (`7z.exe` and
`7z.dll`) in `tools\7-Zip` next to the app, with 7-Zip's `License.txt`. Source code and more information:
https://www.7-zip.org. Where the files came from and their hashes: `third_party/7-Zip/SOURCE.md`.

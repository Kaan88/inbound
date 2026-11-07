# 7-Zip, bundled for extraction

Inbound extracts archives with 7-Zip's console program. 7-Zip is free software by Igor Pavlov,
licensed under the GNU LGPL, with the unRAR license restriction for its RAR decoder.
`License.txt` here is 7-Zip's own license text. Source code and more information:
https://www.7-zip.org.

These files are unmodified, from 7-Zip 26.04 x64, taken from the official release
`https://github.com/ip7z/7zip/releases/download/26.04/7z2604-x64.msi`.

| File | SHA-256 |
| --- | --- |
| 7z.exe | 95fff19b73eff77c87c0d7bd32822e43fb90dd2932459f962eb532d8dfb6ba53 |
| 7z.dll | 899cd73d7fc60e1c1c35c7de0f5bed3dc69bfe2cecbd48014d868a347970a0b1 |
| License.txt | 519ac0a4bded9c18ea02e0afb71f663d8c47373bd9facd3ac96a79f51d77765d |
| readme.txt | 6929cb624c82d5d1c55a146b5dcb4542445628e4ad8bcfe6272a0814e1383618 |

To update: replace the four files from a newer official release, update this table, and run
the Archives tests. The build copies them to `tools\7-Zip` next to every program that
references `DownloadManager.Archives`.

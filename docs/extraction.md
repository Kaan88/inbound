# Extracting RAR archives

Inbound can extract RAR archives after they download, or when you ask, using an UnRAR you have installed
(WinRAR includes one, and you can set the path to another). It handles single archives and split sets
(`name.part01.rar`, or `name.rar` with `name.r00`), RAR4 and RAR5, and archives protected by a password,
including ones with encrypted file names. A split set waits until every volume has arrived, and is
extracted once. Automatic extraction is off until you turn it on, and archives are kept unless you choose
to delete them, which happens only after a verified success. Files go through a temporary folder next to
their destination and appear only when the whole set has checked out, so a crash or restart cannot leave
a half-extracted result: the set simply runs again. An archive that tries to write outside its folder, or
that holds links, is refused.

A password you enter is stored encrypted for your Windows account. A password for one set is forgotten after
it has worked or been refused, and a password for a package stays until the package is gone, since it may
fit its other sets. It reaches UnRAR through its standard input, not its command line, with one limit in UnRAR
itself: it reads standard input in the Windows ANSI code page and stops at 127 characters. A password with
characters outside that code page, or a longer one, can only be passed as a command-line argument, where
other programs running as you, and administrators, could read it while UnRAR runs.

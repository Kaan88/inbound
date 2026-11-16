import re, sys, xml.etree.ElementTree as ET
# Reads a uiautomator dump on stdin; prints "x y description" of the first enabled, clickable node whose
# text / content-desc / resource-id matches the regex in argv[1]. With argv[2]=="list" prints every clickable node instead.
pat = re.compile(sys.argv[1])
data = sys.stdin.read()
i = data.find('<?xml')
if i < 0:
    sys.exit(0)
root = ET.fromstring(data[i:data.rfind('>') + 1])
for n in root.iter('node'):
    for k in ('text', 'content-desc', 'resource-id'):
        v = n.get(k, '')
        if v and pat.search(v) and n.get('enabled') == 'true' and n.get('clickable') == 'true':
            m = re.match(r'\[(\d+),(\d+)\]\[(\d+),(\d+)\]', n.get('bounds', ''))
            if m:
                a = list(map(int, m.groups()))
                print((a[0] + a[2]) // 2, (a[1] + a[3]) // 2, f'{k}={v!r}')
                sys.exit(0)

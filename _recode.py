data = open(r'd:/CODE/LPS/_ccmap_dump.txt','rb').read()
try:
    text = data.decode('gbk')
except Exception as e:
    text = data.decode('cp936', errors='replace')
open(r'd:/CODE/LPS/_ccmap_utf8.txt','w',encoding='utf-8').write(text)
print('done')

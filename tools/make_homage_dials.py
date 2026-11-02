# Grandstand homage clock dials generator (2026-10-24). Original artwork - safe to ship.
# Writes homage_<name>.svg into the current folder, laid out on the -4.56..104.56 viewBox so the
# rendered 1024x1024 PNGs drop straight into WatchRenderer's purpose-made dial rect, with the three
# registers centred on the watch's subdial pivots (SUB below = WatchRenderer SubL/SubR/SubB).
# Render each SVG to PNG at 1024x1024 (e.g. headless Chromium screenshot of the <svg>, or
#   rsvg-convert -w 1024 -h 1024 homage_navy.svg -o homage_navy.png) and copy into assets/clock/.
import math
SUB = {'L':(28.8,47.65),'R':(70.75,47.65),'B':(49.95,71.35)}
V = (-4.56,-4.56,109.12,109.12)
def dial(name, face, face2, sub_fill, sub_ring, sub_tick, idx, txt, accent, lume="#f2e8d0"):
    o=[f'<svg xmlns="http://www.w3.org/2000/svg" width="1024" height="1024" viewBox="{V[0]} {V[1]} {V[2]} {V[3]}">',
       '<defs>',
       f'<radialGradient id="f" cx="50%" cy="42%" r="60%"><stop offset="0" stop-color="{face}"/><stop offset="1" stop-color="{face2}"/></radialGradient>',
       f'<linearGradient id="gold" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#f6e2a6"/><stop offset=".55" stop-color="#c9a24e"/><stop offset="1" stop-color="#8f6a26"/></linearGradient>',
       '<radialGradient id="sub" cx="50%" cy="45%" r="60%"><stop offset="0" stop-color="#ffffff" stop-opacity=".10"/><stop offset="1" stop-color="#000" stop-opacity=".18"/></radialGradient>',
       '</defs>',
       f'<rect x="{V[0]}" y="{V[1]}" width="{V[2]}" height="{V[3]}" fill="{face2}"/>',
       '<circle cx="50" cy="50" r="46" fill="url(#f)"/>']
    # minute track (seconds)
    for i in range(240):
        a=math.radians(i*1.5-90); r1=43.2; long=i%20==0; mid=i%4==0
        r2=41.4 if long else (42.2 if mid else 42.7)
        if not (mid or long): continue
        o.append(f'<line x1="{50+r1*math.cos(a):.2f}" y1="{50+r1*math.sin(a):.2f}" x2="{50+r2*math.cos(a):.2f}" y2="{50+r2*math.sin(a):.2f}" stroke="{txt}" stroke-width="{0.45 if long else 0.22}" stroke-opacity=".85"/>')
    o.append(f'<circle cx="50" cy="50" r="43.2" fill="none" stroke="{txt}" stroke-width=".18" stroke-opacity=".6"/>')
    # applied baton indices with lume
    for h in range(12):
        if h in (3,6,9): continue
        a=math.radians(h*30-90)
        def bar(off):
            px,py=math.cos(a+math.pi/2)*off, math.sin(a+math.pi/2)*off
            x1,y1=50+40*math.cos(a)+px,50+40*math.sin(a)+py; x2,y2=50+33.5*math.cos(a)+px,50+33.5*math.sin(a)+py
            return (f'<line x1="{x1:.2f}" y1="{y1:.2f}" x2="{x2:.2f}" y2="{y2:.2f}" stroke="url(#gold)" stroke-width="2.3" stroke-linecap="round"/>'
                    f'<line x1="{x1:.2f}" y1="{y1:.2f}" x2="{x2:.2f}" y2="{y2:.2f}" stroke="{idx}" stroke-width="1.0" stroke-linecap="round"/>')
        o.append(bar(-1.7)+bar(1.7) if h==0 else bar(0))
    # subdials
    for k,(x,y) in SUB.items():
        o.append(f'<circle cx="{x}" cy="{y}" r="9.6" fill="{sub_fill}"/><circle cx="{x}" cy="{y}" r="9.6" fill="url(#sub)"/>')
        o.append(f'<circle cx="{x}" cy="{y}" r="9.6" fill="none" stroke="{sub_ring}" stroke-width=".9"/>')
        for j in range(30):
            a=math.radians(j*12); rr=8.6 if j%5 else 7.4
            o.append(f'<line x1="{x+9.0*math.cos(a):.2f}" y1="{y+9.0*math.sin(a):.2f}" x2="{x+rr*math.cos(a):.2f}" y2="{y+rr*math.sin(a):.2f}" stroke="{sub_tick}" stroke-width="{0.35 if j%5==0 else 0.18}"/>')
        nums = {'L':['60','20','40'],'R':['30','10','20'],'B':['12','4','8']}[k]
        for n,ang in zip(nums,(-90,30,150)):
            a=math.radians(ang); tx,ty=x+5.6*math.cos(a),y+5.6*math.sin(a)+1.1
            o.append(f'<text x="{tx:.2f}" y="{ty:.2f}" font-family="DejaVu Sans" font-size="2.7" font-weight="700" fill="{sub_tick}" text-anchor="middle">{n}</text>')
    # emblem: small chequered flag in a shield instead of any brand crown
    ex,ey=50,22.6
    o.append(f'<g transform="translate({ex-2.6} {ey-2.4})">')
    for r in range(3):
        for c in range(4):
            if (r+c)%2==0: o.append(f'<rect x="{c*1.3}" y="{r*1.3}" width="1.3" height="1.3" fill="{txt}"/>')
    o.append(f'<rect x="0" y="0" width="5.2" height="3.9" fill="none" stroke="{txt}" stroke-width=".25"/></g>')
    T=lambda y,s,t,w="700",fill=txt,ls=".15": f'<text x="50" y="{y}" font-family="DejaVu Sans" font-size="{s}" font-weight="{w}" letter-spacing="{ls}" fill="{fill}" text-anchor="middle">{t}</text>'
    o += [T(29.4,4.1,"GRANDSTAND","700",txt,".35"), T(32.6,1.75,"BROADCAST CHRONOGRAPH","400",txt,".25"),
          T(35.3,1.75,"RACE CONTROL","700",accent,".3"), T(86.5,1.45,"EST.  2026","400",txt,".2")]
    o.append('</svg>')
    open(f'{name}.svg','w').write('\n'.join(o))

dial('homage_ivory',  '#fbf8f1','#e9e3d6','#e8d8ad','url(#gold)','#2a2418','#1b1b1b','#1b1b1b','#c0392b')
dial('homage_panda',  '#f7f6f2','#e4e2dc','#121212','#cfcfcf','#f2f2f2','#1b1b1b','#1b1b1b','#c0392b')
dial('homage_black',  '#26282c','#0d0e10','#f1efe8','url(#gold)','#1b1b1b','#f2e8d0','#efe7d4','#d64933')
dial('homage_navy',   '#1d3263','#0b1630','#0d1838','url(#gold)','#e9d9a8','#f2e8d0','#efe7d4','#e9b44c')
dial('homage_green',  '#1f5a3a','#0c2c1b','#0e2e1d','url(#gold)','#e9d9a8','#f2e8d0','#efe7d4','#e9b44c')
